using System.Diagnostics;

namespace Denpa.Agent;

/// <summary>
/// 外の選局コマンド (逃げ道の <c>Command</c>) を**子ごと**終わらせる。
///
/// <para>
/// Linux 版は setsid + 負PID で kill していたが、Windows は
/// <see cref="Process.Kill(bool)"/> の <c>entireProcessTree: true</c> で
/// 子孫まで落とせる。**既定では誰も使わない** — 選局は BonDriver を自分で掴む
/// (<see cref="BonDriverTuner"/>)。
/// </para>
/// </summary>
public static class Interop
{
    public static void KillGroup(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 既に終わっている・掴めない。止めるのが目的なので、それで良い
        }
    }
}
