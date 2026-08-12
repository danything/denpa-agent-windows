using System.Diagnostics;

namespace Denpa.Agent;

/// <summary>
/// チューナーを**掴んだまま**選局する口。Linux 版 (denpa 本体の agent) と同じ形。
///
/// <para>
/// Windows では実体は <see cref="BonDriverTuner"/> ただ1つ。<c>BonDriver_*.dll</c> を
/// 読み込んで選局し、TS を <see cref="Output"/> から流す。denpa との HTTP 契約
/// (<c>/denpa/stream</c> ほか) は Linux 版とまったく同じ。
/// </para>
/// </summary>
public interface ITuneDevice : IDisposable
{
    /// <summary>
    /// 選局する。**開いたまま何度でも呼べる。** 同期しなければ例外。
    ///
    /// <para>
    /// Linux 版と違い <paramref name="channel"/> (物理チャンネル文字列 "T27" /
    /// "BS15_0") も受ける。BonDriver は周波数ではなく (space, channel) 索引で選局
    /// するので、対応表を引くのにこれが要る (<see cref="BonDriverTuner"/>)。
    /// </para>
    /// </summary>
    void Tune(string channel, ChannelTable.Tuning tuning, uint streamId);

    /// <summary>TS の読み口。選局し直しても同じものが続く</summary>
    Stream Output { get; }
}

/// <summary>
/// TS の産出 (BonDriver を読むスレッド) と消費 (denpa へ流す側) をつなぐ環。
///
/// <para>
/// **Linux 版 (fd を直に読む <c>DeviceStream</c>) と同じ表の口**にしてある
/// (<see cref="Begin"/> / 4引数 <see cref="Read(byte[], int, int, Func{bool}?)"/> /
/// <see cref="TakeOverflows"/> / <see cref="Stop"/>) ので、<c>TunerPool</c> は
/// そのまま動く。違うのは中身だけ — あちらはカーネルの環を読み、こちらは
/// 自前の環に <see cref="Write"/> で積む。
/// </para>
///
/// <para>
/// **溢れは終わりにしない。** 読むのが追いつかず環が一杯になったら、古いぶんを
/// 捨てて数えるだけ。選局は生きているので、読み続ければ続きが来る。
/// </para>
/// </summary>
internal sealed class DeviceStream : Stream
{
    /// <summary>環の大きさ。地上波 (~19Mbps) で約 3.5 秒ぶん。Linux 版の DVR 環と揃える</summary>
    private const int Capacity = 8 * 1024 * 1024;

    /// <summary>この間隔で起きて、畳めと言われていないか見る</summary>
    private const int WakeMs = 200;

    private readonly byte[] _ring = new byte[Capacity];
    // Monitor.Wait/PulseAll と併用するので Lock 型ではなく素の object にする
    private readonly object _gate = new();
    private int _head; // 次に書く位置
    private int _tail; // 次に読む位置
    private int _size; // 溜まっているバイト数
    private volatile bool _closed;

    private int _overflows;
    private int _worstGap;
    private int _stale;
    private bool _handed;
    private long _handedAt = Stopwatch.GetTimestamp();

    /// <summary>前に聞かれてから溢れた回数と、そのとき読み手がどれだけ空いていたか</summary>
    public (int Count, int WorstGapMs, int Stale) TakeOverflows()
    {
        lock (_gate)
        {
            var taken = (_overflows, _worstGap, _stale);
            _overflows = 0;
            _worstGap = 0;
            _stale = 0;
            return taken;
        }
    }

    /// <summary>その選局のぶんだけ数えはじめる (前の選局・選局中に溜まったぶんを持ち越さない)</summary>
    public void Begin()
    {
        lock (_gate)
        {
            _overflows = 0;
            _worstGap = 0;
            _stale = 0;
            _handed = false;
            _handedAt = Stopwatch.GetTimestamp();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _closed = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// **産出側から積む。** BonDriver を読むスレッドが呼ぶ。環が一杯なら古いぶんを
    /// 捨てて溢れとして数える (選局は生きているので終わりにはしない)。
    /// </summary>
    public void Feed(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        lock (_gate)
        {
            if (_closed) return;

            // 入りきらないぶんは古い側を捨てて席を空ける
            var overflow = data.Length - (Capacity - _size);
            if (overflow > 0)
            {
                _tail = (_tail + overflow) % Capacity;
                _size -= overflow;
                // 最初の1回 (この選局で一度も返していない間) は選局中に溜まったぶんで、読み手のせいではない
                if (_handed)
                {
                    _overflows++;
                    var gap = (int)Stopwatch.GetElapsedTime(_handedAt).TotalMilliseconds;
                    if (gap > _worstGap) _worstGap = gap;
                    _handedAt = Stopwatch.GetTimestamp();
                }
                else
                {
                    _stale++;
                }
            }

            // 入力が環より大きいことは無い前提だが、念のため末尾ぶんだけ入れる
            var span = data.Length > Capacity ? data[^Capacity..] : data;
            var first = Math.Min(span.Length, Capacity - _head);
            span[..first].CopyTo(_ring.AsSpan(_head));
            span[first..].CopyTo(_ring.AsSpan(0));
            _head = (_head + span.Length) % Capacity;
            _size += span.Length;
            Monitor.PulseAll(_gate);
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer, offset, count, null);

    /// <param name="giveUp">
    /// **読むのをやめる合図。** デバイスは選局を跨いで開いたままなので <see cref="Stop"/> は
    /// 使えない (次の選局が同じものを読む)。読み手が降りたいときはこれを渡す。
    /// 渡さないと、電波が来ていない間は溜まらず、呼んだ側は降りようがない。
    /// </param>
    public int Read(byte[] buffer, int offset, int count, Func<bool>? giveUp)
    {
        lock (_gate)
        {
            while (true)
            {
                if (_size > 0)
                {
                    var take = Math.Min(count, _size);
                    var first = Math.Min(take, Capacity - _tail);
                    _ring.AsSpan(_tail, first).CopyTo(buffer.AsSpan(offset));
                    _ring.AsSpan(0, take - first).CopyTo(buffer.AsSpan(offset + first));
                    _tail = (_tail + take) % Capacity;
                    _size -= take;
                    _handed = true;
                    _handedAt = Stopwatch.GetTimestamp();
                    return take;
                }
                if (_closed) return 0;
                if (giveUp?.Invoke() == true) return 0;
                Monitor.Wait(_gate, WakeMs);
            }
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Stop();
        base.Dispose(disposing);
    }
}
