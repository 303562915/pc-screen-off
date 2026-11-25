using System;
using System.IO;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace ScreenOff;

/// <summary>
/// 托盘常驻程序：关屏 + 静音 + 可选锁鼠标。
/// Alt+1 静默开始 / Alt+2 锁鼠标开始 / Alt+3 恢复；托盘双击也是恢复。
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private enum Mode
    {
        Off,
        Normal,
        LockMouse
    }

    private readonly NotifyIcon _tray;
    private readonly Timer _timer;
    private readonly MouseBlocker _blocker = new();
    private readonly HotkeyWindow _hotkeys;

    private Mode _mode = Mode.Off;
    private uint _lastInput;
    private long _lastOff = -1;
    private long _lastMuteCheck;
    private int _sent;
    private bool _mutedByUs;
    private bool _disposed;

    public TrayContext()
    {
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "关屏 · 未启用（Alt+1 / Alt+2 / Alt+3）",
            ContextMenuStrip = BuildMenu()
        };
        _tray.DoubleClick += (_, _) => Restore("托盘双击");

        _hotkeys = new HotkeyWindow(this);

        _timer = new Timer { Interval = 100 };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        Log($"启动 pid={Environment.ProcessId} 热键={(_hotkeys.Registered ? "已注册" : "注册失败（可能被其他程序占用，用托盘菜单）")}");
        UpdateTray();
    }

    internal void OnHotkey(int id)
    {
        switch (id)
        {
            case HotkeyWindow.HkNormal:
                Start(Mode.Normal, "Alt+1");
                break;
            case HotkeyWindow.HkLockMouse:
                Start(Mode.LockMouse, "Alt+2");
                break;
            case HotkeyWindow.HkRestore:
                Restore("Alt+3");
                break;
        }
    }

    private void Start(Mode mode, string reason)
    {
        if (_mode != Mode.Off) ShutdownToIdle("切换模式");

        _mode = mode;

        _blocker.Stop();
        if (mode == Mode.LockMouse) _blocker.Install();

        _mutedByUs = AudioMute.SetMute(true);
        _lastInput = Native.LastInputTick();
        _sent = 0;
        _lastOff = Environment.TickCount64;
        _lastMuteCheck = _lastOff;

        Native.ScreenOff();

        Log($"{ModeName(mode)} 开始（{reason}）静音={_mutedByUs} 鼠标钩子={_blocker.Active}");
        UpdateTray();
    }

    private void Restore(string reason)
    {
        var wasActive = _mode != Mode.Off || _mutedByUs;
        var modeName = ModeName(_mode);

        ShutdownToIdle(reason);

        if (wasActive)
        {
            Native.ScreenOn();
            Log($"恢复（{reason}）原模式={modeName} 灭屏发送={_sent}");
        }

        UpdateTray();
    }

    /// <summary>只把状态复位（卸载钩子、取消静音），不动屏幕。</summary>
    private void ShutdownToIdle(string reason)
    {
        _blocker.Stop();
        if (_mutedByUs)
        {
            AudioMute.SetMute(false);
            _mutedByUs = false;
        }

        _mode = Mode.Off;
    }

    private void Tick()
    {
        if (_mode == Mode.Off) return;

        var now = Environment.TickCount64;
        var input = Native.LastInputTick();
        var newInput = input != _lastInput;
        if (newInput) _lastInput = input;

        var sinceOff = now - _lastOff;
        var fire = sinceOff >= 15000 || (newInput && sinceOff >= 500);
        if (fire)
        {
            Native.ScreenOff();
            _lastOff = Environment.TickCount64;
            _sent++;
        }

        if (now - _lastMuteCheck >= 5000)
        {
            _lastMuteCheck = now;
            if (_mutedByUs && !AudioMute.IsMuted())
            {
                AudioMute.SetMute(true);
                Log("静音被解除，已重新静音");
            }
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("① 开始 - 静默模式（Alt+1）", null, (_, _) => Start(Mode.Normal, "菜单"));
        menu.Items.Add("② 开始 - 锁鼠标模式（Alt+2）", null, (_, _) => Start(Mode.LockMouse, "菜单"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("③ 恢复屏幕（Alt+3）", null, (_, _) => Restore("菜单"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitAll());
        return menu;
    }

    private void UpdateTray()
    {
        var state = _mode switch
        {
            Mode.Normal => "静默中",
            Mode.LockMouse => "锁鼠标中",
            _ => "未启用"
        };

        _tray.Text = $"关屏 · {state} | Alt+1 静默 / Alt+2 锁鼠标 / Alt+3 恢复";
    }

    private void ExitAll()
    {
        Restore("退出");
        _timer.Stop();
        _tray.Visible = false;
        Log("退出");
        ExitThread();
    }

    private static string ModeName(Mode mode) => mode switch
    {
        Mode.Normal => "静默版",
        Mode.LockMouse => "锁鼠标版",
        _ => "未启用"
    };

    private static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScreenOff");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "screenoff.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响功能
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try { _timer?.Stop(); } catch { }
            try { _hotkeys?.Dispose(); } catch { }
            try { _blocker?.Dispose(); } catch { }
            try { AudioMute.SetMute(false); } catch { }
            try { Native.ScreenOn(); } catch { }
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
