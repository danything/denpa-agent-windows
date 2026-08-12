namespace Denpa.Agent;

/// <summary>
/// 刺さっている機材を自動で見つける口。**Windows では自動検出しない。**
///
/// <para>
/// Linux 版は <c>/dev/dvb/*</c> を数えられたが、BonDriver は「どの DLL がどの機材か」
/// が名前からは決まらない (<c>BonDriver_PX4-T0.dll</c> のような命名は慣習にすぎない)。
/// どの <c>BonDriver_*.dll</c> を何本使うかは <c>tuners.yaml</c> に書いてもらう
/// (<see cref="Config.ResolveTuners"/>)。ここは常に空を返す。
/// </para>
/// </summary>
public static class DeviceProbe
{
    public static List<TunerSpec> Detect() => [];
}
