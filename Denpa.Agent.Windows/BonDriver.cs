using System.Runtime.InteropServices;
using System.Text.Json;

namespace Denpa.Agent;

/// <summary>
/// 物理チャンネル ("T27" / "BS15_0") を BonDriver の (space, channel) 索引へ写す表。
///
/// <para>
/// **BonDriver は周波数を受け取らない。** どの space/channel が何の局かは
/// <c>BonDriver_*.dll</c> ごとの定義 (多くは同梱の <c>.ini</c>) で決まっていて、
/// 並び順もドライバでばらばら。だから denpa の物理チャンネル表記との対応は、
/// **ドライバごとに1つ、こちらで表を持つ**しかない。
/// </para>
///
/// <para>
/// 形は <c>{ "T27": [0, 25], "BS15_0": [1, 0] }</c> ([space, channel])。既定の
/// 置き場は設定と同じフォルダの <c>bondriver-map.json</c>。地デジは
/// <c>--enum</c> で吐いた一覧を見ながら埋めるのが確実
/// (<see cref="BonDriverTuner"/> の列挙)。
/// </para>
/// </summary>
public sealed class ChannelMap
{
    private readonly Dictionary<string, (uint Space, uint Channel)> _map = new(StringComparer.OrdinalIgnoreCase);

    public static ChannelMap Load(string? path)
    {
        var map = new ChannelMap();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return map;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            var pair = entry.Value;
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2) continue;
            map._map[entry.Name] = (pair[0].GetUInt32(), pair[1].GetUInt32());
        }
        return map;
    }

    public bool TryGet(string channel, out uint space, out uint index)
    {
        if (_map.TryGetValue(channel, out var found))
        {
            (space, index) = found;
            return true;
        }
        (space, index) = (0, 0);
        return false;
    }

    public int Count => _map.Count;
}

/// <summary>
/// <c>BonDriver_*.dll</c> を読み込んで選局し、TS を流す (<see cref="ITuneDevice"/>)。
/// Windows でチューナーに触るのはここだけ。
///
/// <para>
/// **IBonDriver2 の vtable を直に叩く。** BonDriver は C++ の COM 風インターフェイス
/// (<c>CreateBonDriver()</c> が <c>IBonDriver2*</c> を返す) で、.NET には型が無いので
/// 関数ポインタで呼ぶ。呼び出し規約は <c>__thiscall</c> (x86 は ECX、x64 は第1引数が
/// this) — どちらも <c>delegate* unmanaged[Thiscall]</c> で通る。
/// </para>
///
/// <para>
/// **未実機検証。** ABI の定義どおりに書いてあるが、実機の BonDriver・カードでの
/// 動作確認はこれから (README 参照)。多くの BonDriver は 32bit なので、その場合は
/// x86 でビルドすること。
/// </para>
/// </summary>
public sealed unsafe partial class BonDriverTuner : ITuneDevice
{
    // IBonDriver / IBonDriver2 の vtable 索引 (de-facto 標準。TVTest ほかと同じ並び)
    private const int VtOpenTuner = 0; //  const BOOL OpenTuner()
    private const int VtCloseTuner = 1; //  void CloseTuner()
    private const int VtWaitTsStream = 4; //  DWORD WaitTsStream(DWORD timeout)
    private const int VtGetTsStreamPtr = 7; //  BOOL GetTsStream(BYTE** ppDst, DWORD* size, DWORD* remain)
    private const int VtPurgeTsStream = 8; //  void PurgeTsStream()
    private const int VtRelease = 9; //  void Release()
    private const int VtSetChannel2 = 12; //  const BOOL SetChannel(DWORD space, DWORD channel)  ← IBonDriver2
    private const int VtEnumChannelName = 11; //  LPCTSTR EnumChannelName(DWORD space, DWORD channel)
    private const int VtEnumTuningSpace = 10; //  LPCTSTR EnumTuningSpace(DWORD space)

    [LibraryImport("kernel32", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadLibraryW(string path);

    [LibraryImport("kernel32", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetProcAddress(nint module, string name);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeLibrary(nint module);

    private readonly nint _module;
    private readonly nint _bon; // IBonDriver2*
    private readonly nint* _vtable;
    private readonly ChannelMap _channels;
    private readonly DeviceStream _stream = new();
    private readonly Thread _pump;
    private volatile bool _stopping;
    private bool _opened;

    /// <param name="dll"><c>BonDriver_*.dll</c> のパス</param>
    /// <param name="channelMapPath"><see cref="ChannelMap"/> の JSON</param>
    public BonDriverTuner(string dll, string? channelMapPath)
    {
        _channels = ChannelMap.Load(channelMapPath);

        _module = LoadLibraryW(dll);
        if (_module == 0) throw new IOException($"{dll} を読み込めません ({Marshal.GetLastPInvokeErrorMessage()})");

        var create = GetProcAddress(_module, "CreateBonDriver");
        if (create == 0)
        {
            FreeLibrary(_module);
            throw new IOException($"{dll} に CreateBonDriver がありません");
        }

        // extern "C" IBonDriver* CreateBonDriver();  (cdecl, 戻りは IBonDriver2*)
        _bon = ((delegate* unmanaged[Cdecl]<nint>)create)();
        if (_bon == 0)
        {
            FreeLibrary(_module);
            throw new IOException($"{dll} の CreateBonDriver が null を返しました");
        }
        _vtable = (nint*)*(nint*)_bon;

        if (!OpenTuner()) throw new IOException($"{dll}: OpenTuner に失敗しました (機器を掴めていない可能性)");
        _opened = true;

        _pump = new Thread(Pump) { IsBackground = true, Name = "bondriver-pump" };
        _pump.Start();
    }

    private nint Method(int index) => _vtable[index];

    private bool OpenTuner() => ((delegate* unmanaged[Thiscall]<nint, int>)Method(VtOpenTuner))(_bon) != 0;
    private void CloseTuner() => ((delegate* unmanaged[Thiscall]<nint, void>)Method(VtCloseTuner))(_bon);
    private void PurgeTsStream() => ((delegate* unmanaged[Thiscall]<nint, void>)Method(VtPurgeTsStream))(_bon);
    private void Release() => ((delegate* unmanaged[Thiscall]<nint, void>)Method(VtRelease))(_bon);
    private uint WaitTsStream(uint timeout) => ((delegate* unmanaged[Thiscall]<nint, uint, uint>)Method(VtWaitTsStream))(_bon, timeout);
    private bool SetChannel2(uint space, uint channel) => ((delegate* unmanaged[Thiscall]<nint, uint, uint, int>)Method(VtSetChannel2))(_bon, space, channel) != 0;

    private bool GetTsStream(out byte* dst, out uint size, out uint remain)
    {
        byte* p;
        uint s, r;
        var ok = ((delegate* unmanaged[Thiscall]<nint, byte**, uint*, uint*, int>)Method(VtGetTsStreamPtr))(_bon, &p, &s, &r) != 0;
        dst = p;
        size = s;
        remain = r;
        return ok;
    }

    public Stream Output => _stream;

    public void Tune(string channel, ChannelTable.Tuning tuning, uint streamId)
    {
        if (!_channels.TryGet(channel, out var space, out var index))
        {
            throw new IOException(
                $"{channel} が bondriver-map.json にありません。space/channel を対応表に足してください");
        }
        // 前の選局のぶんが環に残らないよう、切り替え時に BonDriver 側の溜めも捨てる
        PurgeTsStream();
        if (!SetChannel2(space, index))
        {
            throw new IOException($"{channel} (space {space}, ch {index}) に選局できません (同期しない・その索引に放送が無い)");
        }
    }

    /// <summary>BonDriver を読み続けて環に積む。選局を跨いで回りっぱなし (Linux 版の産出スレッドと同じ役)</summary>
    private void Pump()
    {
        try
        {
            while (!_stopping)
            {
                // データが来るまで最大 200ms 待つ (待たずに回すと CPU を焼く)
                WaitTsStream(200);
                while (GetTsStream(out var src, out var size, out var remain))
                {
                    if (src != null && size > 0)
                    {
                        _stream.Feed(new ReadOnlySpan<byte>(src, checked((int)size)));
                    }
                    if (remain == 0) break;
                    if (_stopping) break;
                }
            }
        }
        catch (Exception error)
        {
            Log.Write($"BonDriver 読み取りが止まりました: {error.Message}");
        }
        finally
        {
            _stream.Stop();
        }
    }

    /// <summary>space/channel の一覧を吐く (対応表を埋めるとき用。<c>--enum</c>)</summary>
    public IEnumerable<(uint Space, string SpaceName, uint Channel, string ChannelName)> Enumerate()
    {
        for (uint space = 0; space < 64; space++)
        {
            var spaceName = ReadString(Method(VtEnumTuningSpace), space);
            if (spaceName is null) yield break;
            for (uint ch = 0; ch < 256; ch++)
            {
                var name = ReadString(Method(VtEnumChannelName), space, ch);
                if (name is null) break;
                yield return (space, spaceName, ch, name);
            }
        }
    }

    // EnumTuningSpace/EnumChannelName は LPCTSTR を返す。多くの BonDriver は UNICODE ビルド
    // (LPCWSTR) なので UTF-16 として読む。null / 空で打ち切り
    private string? ReadString(nint method, uint a)
    {
        var p = ((delegate* unmanaged[Thiscall]<nint, uint, nint>)method)(_bon, a);
        return p == 0 ? null : Marshal.PtrToStringUni(p) is { Length: > 0 } s ? s : null;
    }

    private string? ReadString(nint method, uint a, uint b)
    {
        var p = ((delegate* unmanaged[Thiscall]<nint, uint, uint, nint>)method)(_bon, a, b);
        return p == 0 ? null : Marshal.PtrToStringUni(p) is { Length: > 0 } s ? s : null;
    }

    public void Dispose()
    {
        _stopping = true;
        _stream.Stop();
        try
        {
            _pump.Join(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // 落ちても続ける。掴んだものは下で必ず離す
        }
        if (_opened)
        {
            try { CloseTuner(); } catch { /* 掴めていなくても Release までは進める */ }
        }
        if (_bon != 0) Release();
        if (_module != 0) FreeLibrary(_module);
        _stream.Dispose();
    }
}
