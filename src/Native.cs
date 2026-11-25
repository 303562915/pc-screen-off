using System;
using System.Runtime.InteropServices;

namespace ScreenOff;

internal static class Native
{
    // ---- 关屏 / 点亮 ----
    private const uint WM_SYSCOMMAND = 0x0112;
    private const int SC_MONITORPOWER = 0xF170;
    private static readonly IntPtr HWND_BROADCAST = new(0xffff);
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    public static void ScreenOff()
    {
        SendMessageTimeout(HWND_BROADCAST, WM_SYSCOMMAND, new IntPtr(SC_MONITORPOWER), new IntPtr(2),
            SMTO_ABORTIFHUNG, 1000, out _);
    }

    public static void ScreenOn()
    {
        SendMessageTimeout(HWND_BROADCAST, WM_SYSCOMMAND, new IntPtr(SC_MONITORPOWER), new IntPtr(-1),
            SMTO_ABORTIFHUNG, 1000, out _);
        mouse_event(0x0001, 2, 0, 0, UIntPtr.Zero);
        mouse_event(0x0001, -2, 0, 0, UIntPtr.Zero);
    }

    // ---- 全局热键 ----
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    // ---- 空闲/输入 ----
    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    public static uint LastInputTick()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? info.dwTime : 0;
    }
}
