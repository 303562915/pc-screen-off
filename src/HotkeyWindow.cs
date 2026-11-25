using System;
using System.Windows.Forms;

namespace ScreenOff;

/// <summary>承载全局热键的隐藏窗口：Alt+1 静默 / Alt+2 锁鼠标 / Alt+3 恢复。</summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    public const int HkNormal = 1;
    public const int HkLockMouse = 2;
    public const int HkRestore = 3;

    private readonly TrayContext _context;
    private bool _registered;

    public HotkeyWindow(TrayContext context)
    {
        _context = context;
        CreateHandle(new CreateParams());
        Register();
    }

    public bool Registered => _registered;

    private void Register()
    {
        var mods = Native.MOD_ALT | Native.MOD_NOREPEAT;
        _registered =
            Native.RegisterHotKey(Handle, HkNormal, mods, 0x31) &   // Alt+1
            Native.RegisterHotKey(Handle, HkLockMouse, mods, 0x32) & // Alt+2
            Native.RegisterHotKey(Handle, HkRestore, mods, 0x33);    // Alt+3
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY)
        {
            _context.OnHotkey(m.WParam.ToInt32());
            return;
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            Native.UnregisterHotKey(Handle, HkNormal);
            Native.UnregisterHotKey(Handle, HkLockMouse);
            Native.UnregisterHotKey(Handle, HkRestore);
            DestroyHandle();
        }
    }
}
