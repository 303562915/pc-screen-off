using System;
using System.Runtime.InteropServices;

namespace ScreenOff;

/// <summary>显示器当前是否亮着。用 kernel32 的 GetDevicePowerState（监视器电源状态）。</summary>
internal static class DisplayState
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetDevicePowerState(IntPtr hDevice, out int pfOn);

    /// <summary>true=亮着，false=已关（含低功耗），null=取不到。</summary>
    public static bool? IsOn()
    {
        try
        {
            // 监视器设备的伪句柄是 0
            if (!GetDevicePowerState(IntPtr.Zero, out var on)) return null;
            return on != 0;
        }
        catch
        {
            return null;
        }
    }
}