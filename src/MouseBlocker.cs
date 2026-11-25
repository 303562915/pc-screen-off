using System;
using System.Runtime.InteropServices;

namespace ScreenOff;

/// <summary>
/// 锁鼠标模式用：吞掉「物理鼠标 / 触摸板」事件，系统收不到，动鼠标就不会点亮屏幕。
/// 远程桌面、串流软件注入的鼠标事件（LLMHF_INJECTED）照常放行。
/// 键盘完全不管，键盘照常能点亮屏幕。
/// </summary>
internal sealed class MouseBlocker : IDisposable
{
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public int x;
        public int y;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private const int WH_MOUSE_LL = 14;
    private const uint LLMHF_INJECTED = 0x00000001;
    private const uint LLMHF_LOWER_IL_INJECTED = 0x00000002;

    private static HookProc _proc;          // 必须保持强引用，否则会被 GC 回收
    private static IntPtr _hook = IntPtr.Zero;
    private static volatile bool _active;
    private static int _swallowed;

    public bool Active => _active;
    public int Swallowed => _swallowed;
    public int LastError { get; private set; }

    /// <summary>返回 true 时放行（例如界面窗口在前台，要能点按钮）。</summary>
    public static Func<bool> PassThrough;

    public bool Install()
    {
        if (_hook != IntPtr.Zero) return true;
        _proc = Callback;
        _active = true;
        _swallowed = 0;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        LastError = _hook == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        if (_hook == IntPtr.Zero) _active = false;
        return _hook != IntPtr.Zero;
    }

    public void Stop()
    {
        _active = false;
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && _active)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var injected = (info.flags & (LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED)) != 0;
                if (!injected)
                {
                    var pass = PassThrough;
                    if (pass == null || !pass())
                    {
                        _swallowed++;
                        return new IntPtr(1);   // 丢弃：系统看不到这次鼠标动作
                    }
                }
            }
        }
        catch
        {
            // 钩子里不能抛异常
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();
}
