using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ScreenOff;

/// <summary>
/// 输入锁。锁鼠标模式用：
///   - 物理鼠标 / 触摸板的事件全部吞掉，系统收不到 → 动鼠标不点亮屏幕
///   - 物理键盘的按键也全部吞掉 → 按键盘也不点亮屏幕
///   - 唯一例外：按下 Alt+3 时放行这一组按键并通知外部退出（否则就解不开了）
/// 程序自己注入的键鼠事件（LLMHF_INJECTED / LLKHF_INJECTED）一律放行，
/// 这样界面上的按钮和恢复动作还能正常工作。
/// </summary>
internal sealed class InputLock : IDisposable
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

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
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

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint WM_SYSKEYUP = 0x0105;

    private const uint LLMHF_INJECTED = 0x00000001;
    private const uint LLMHF_LOWER_IL_INJECTED = 0x00000002;
    private const uint LLKHF_INJECTED = 0x00000010;
    private const uint LLKHF_LOWER_IL_INJECTED = 0x00000002;

    private const uint VK_MENU = 0x12;
    private const uint VK_3 = 0x33;

    private static HookProc _mouseProc;
    private static HookProc _kbProc;
    private static IntPtr _mouseHook = IntPtr.Zero;
    private static IntPtr _kbHook = IntPtr.Zero;
    private static InputLock _owner;   // 让静态钩子回调能触发实例事件

    private static volatile bool _active;
    private static volatile bool _swallowKeyboard;
    private static bool _altDown;
    private static int _swallowedMouse;
    private static int _swallowedKey;

    /// <summary>识别到 Alt+3 时触发（在钩子线程上）。</summary>
    public event Action AltThreePressed;

    private void FireAltThree() => AltThreePressed?.Invoke();

    public bool Active => _active;
    public int SwallowedMouse => _swallowedMouse;
    public int SwallowedKey => _swallowedKey;
    public int LastError { get; private set; }

    /// <summary>装钩子。swallowKeyboard=true 时键盘也一起吞。</summary>
    public bool Install(bool swallowKeyboard)
    {
        if (_mouseHook != IntPtr.Zero) return true;

        _owner = this;
        _swallowKeyboard = swallowKeyboard;
        _swallowedMouse = 0;
        _swallowedKey = 0;
        _altDown = false;

        var mod = GetModuleHandle(null);
        _mouseProc = MouseCallback;
        _kbProc = KeyboardCallback;

        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, mod, 0);
        _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, mod, 0);

        LastError = 0;
        if (_mouseHook == IntPtr.Zero) LastError |= 1;
        if (_kbHook == IntPtr.Zero) LastError |= 2;

        _active = _mouseHook != IntPtr.Zero && _kbHook != IntPtr.Zero;
        return _active;
    }

    public void Stop()
    {
        _active = false;
        _swallowKeyboard = false;
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        if (_kbHook != IntPtr.Zero) { UnhookWindowsHookEx(_kbHook); _kbHook = IntPtr.Zero; }
        _owner = null;
    }

    private static IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && _active)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var injected = (info.flags & (LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED)) != 0;
                if (!injected)
                {
                    _swallowedMouse++;
                    return new IntPtr(1);   // 吞掉
                }
            }
        }
        catch
        {
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var msg = wParam.ToInt32();
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var injected = (info.flags & (LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED)) != 0;
                var isDown = msg == (int)WM_KEYDOWN || msg == (int)WM_SYSKEYDOWN;

                if (_active && !injected)
                {
                    // 维护 Alt 状态：系统吞掉按键后 GetAsyncKeyState 也不一定准，自己记
                    if (info.vkCode == VK_MENU)
                    {
                        _altDown = isDown;
                    }

                    // 唯一出口：Alt + 3
                    if (isDown && info.vkCode == VK_3 && _altDown)
                    {
                        _altDown = false;
                        ((InputLock)_owner)?.FireAltThree();
                        return CallNextHookEx(_kbHook, nCode, wParam, lParam);   // 这一组放行
                    }

                    _swallowedKey++;
                    return new IntPtr(1);   // 其他按键全部吞掉
                }
            }
        }
        catch
        {
        }

        return CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();
}