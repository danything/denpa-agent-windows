using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Denpa.Agent;

/// <summary>
/// カードリーダーが見えているか。**Windows は WinSCard で見る** (pcscd は要らない)。
///
/// <para>
/// Linux 版は pcscd を起こして pcsc_scan を読んでいたが、Windows は OS 標準の
/// スマートカードサービス (SCardSvr) が常駐していて、<c>winscard.dll</c> から
/// リーダー一覧が取れる。デーモンを起こす手順が無いぶん簡単。
/// </para>
///
/// <para>
/// 返す形は Linux 版と合わせる (<c>ok</c> / <c>readers</c> / <c>message</c>) ので、
/// denpa 側の画面はそのまま。B-CAS の解除そのものは <c>aribb25.dll</c> が
/// WinSCard 越しにやる (<see cref="AribB25"/>)。
/// </para>
/// </summary>
public static partial class Card
{
    private const uint ScopeSystem = 2;
    private const uint Success = 0;

    [LibraryImport("winscard.dll", EntryPoint = "SCardEstablishContext")]
    private static partial uint SCardEstablishContext(uint scope, nint reserved1, nint reserved2, out nint context);

    [LibraryImport("winscard.dll", EntryPoint = "SCardReleaseContext")]
    private static partial uint SCardReleaseContext(nint context);

    // mszReaders は '\0' 区切り・末尾 '\0\0' のマルチ文字列 (UTF-16)
    [LibraryImport("winscard.dll", EntryPoint = "SCardListReadersW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint SCardListReaders(nint context, string? groups, char[]? readers, ref uint length);

    /// <summary>Linux 版に口を合わせるためだけの空実装。Windows は起こすデーモンが無い</summary>
    public static Task EnsurePcscd() => Task.CompletedTask;

    public static Task<JsonObject> Status()
    {
        var readers = new JsonArray();
        string message;
        var ok = false;

        if (SCardEstablishContext(ScopeSystem, 0, 0, out var context) != Success)
        {
            message = "スマートカードサービスに繋げません (SCardSvr が動いていますか)";
            return Task.FromResult(Result(ok, readers, message));
        }

        try
        {
            uint length = 0;
            var query = SCardListReaders(context, null, null, ref length);
            if (query == Success && length > 0)
            {
                var buffer = new char[length];
                if (SCardListReaders(context, null, buffer, ref length) == Success)
                {
                    foreach (var name in SplitMultiString(buffer))
                    {
                        readers.Add((JsonNode?)JsonValue.Create(name));
                    }
                }
            }
        }
        finally
        {
            SCardReleaseContext(context);
        }

        ok = readers.Count > 0;
        message = ok
            ? $"カードリーダーが見えています ({readers.Count} 台)"
            : "カードリーダーが見つかりません (刺さっていますか・ドライバは入っていますか)";
        return Task.FromResult(Result(ok, readers, message));
    }

    private static JsonObject Result(bool ok, JsonArray readers, string message) => new()
    {
        ["ok"] = ok,
        ["readers"] = readers,
        ["message"] = message,
    };

    private static IEnumerable<string> SplitMultiString(char[] buffer)
    {
        var start = 0;
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != '\0') continue;
            if (i > start) yield return new string(buffer, start, i - start);
            start = i + 1;
            // 二連続の '\0' で終端
            if (i + 1 < buffer.Length && buffer[i + 1] == '\0') break;
        }
    }
}

/// <summary>
/// 掛かったまま録れたTSを、後から解く。**中身は Linux 版と同じ** (aribb25 に投げるだけ)。
///
/// <para>
/// **こちらの返事だけでは足りない。** 解けたつもりで掛かったままのものが出来る道が
/// 残る (鍵が合わない・ECM が流れていない) ので、出来上がったものを読んで確かめるのは
/// 呼び出し側 (denpa の <c>scramble.ts</c>)。
/// </para>
/// </summary>
public static class Scramble
{
    /// <summary>置き場の中に収まるパスだけ受け付ける。外を読み書きさせない</summary>
    private static string? Inside(string root, string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var full = Path.GetFullPath(Path.Combine(root, name));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    public static JsonObject Decode(string recorded, string? input, string? output, string? cardUrl)
    {
        var source = Inside(recorded, input);
        var target = Inside(recorded, output);
        if (source is null || target is null)
        {
            return new JsonObject { ["ok"] = false, ["error"] = "生TSの置き場の外は解除に回せません" };
        }
        if (!File.Exists(source))
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = $"{source} が見えません。denpa と同じ置き場をこの機にも見せてください",
            };
        }

        try
        {
            using var b25 = AribB25.Open(cardUrl);
            using var reading = File.OpenRead(source);
            using var writing = File.Create(target);
            var buffer = new byte[188 * 1024];
            int read;
            while ((read = reading.Read(buffer)) > 0) writing.Write(b25.Decode(buffer.AsSpan(0, read)));
            writing.Write(b25.Flush());
        }
        catch (Exception error)
        {
            return new JsonObject { ["ok"] = false, ["error"] = error.Message };
        }
        return new JsonObject { ["ok"] = true, ["error"] = "" };
    }
}
