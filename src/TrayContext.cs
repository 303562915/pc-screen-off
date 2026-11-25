using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace ScreenOff;

/// <summary>
/// 常驻主体：托盘 + 主窗口 + 全局热键 + 灭屏调度。
/// Alt+0 打开界面 / Alt+1 静默 / Alt+2 锁鼠标 / Alt+3 恢复。
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private enum Mode
    {
        Off,
        Normal,
        LockMouse
    }

    internal readonly struct StatusSnapshot
    {
        public readonly bool Running;
        public readonly string StateText;
        public readonly int Sent;
        public readonly bool MutedByUs;
        public readonly bool Hotkeys;

        public StatusSnapshot(bool running, string stateText, int sent, bool mutedByUs, bool hotkeys)
        {
            Running = running;
            StateText = stateText;
            Sent = sent;
            MutedByUs = mutedByUs;
            Hotkeys = hotkeys;
        }
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
    private bool _muteOnLock = true;
    private MainForm _form;

    public TrayContext()
    {
        LoadSettings();

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Get(),
            Visible = true,
            Text = "关屏 · 未启用（双击打开界面）",
            ContextMenuStrip = BuildMenu()
        };
        _tray.DoubleClick += (_, _) => ShowWindow();

        _hotkeys = new HotkeyWindow(this);

        MouseBlocker.PassThrough = FormIsForeground;

        _timer = new Timer { Interval = 100 };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        Log($"启动 pid={Environment.ProcessId} 热键={(_hotkeys.Registered ? "已注册" : "注册失败")}");
        UpdateTray();

        // 首次运行自动弹界面，之后只在托盘
        ShowWindow();
    }

    // ---------- 对外（主界面调用） ----------

    public bool MuteOnLock
    {
        get => _muteOnLock;
        set
        {
            _muteOnLock = value;
            SaveSettings();
        }
    }

    public StatusSnapshot Snapshot() => new(
        _mode != Mode.Off,
        _mode switch
        {
            Mode.Normal => "静默中 · 屏幕保持关闭",
            Mode.LockMouse => "锁鼠标中 · 屏幕保持关闭",
            _ => "未启用"
        },
        _sent,
        _mutedByUs,
        _hotkeys.Registered);

    public void StartNormal(string reason) => Start(Mode.Normal, reason);

    public void StartLockMouse(string reason) => Start(Mode.LockMouse, reason);

    public void RestoreAll(string reason)
    {
        var wasActive = _mode != Mode.Off || _mutedByUs;
        var modeName = ModeName(_mode);

        ShutdownToIdle();
        if (wasActive)
        {
            Native.ScreenOn();
            Log($"恢复（{reason}）原模式={modeName} 灭屏发送={_sent}");
        }

        UpdateTray();
        _form?.Invoke(new Action(() => _form?.Refresh()));
    }

    public void ShowWindow()
    {
        if (_form == null || _form.IsDisposed)
        {
            _form = new MainForm(this);
        }

        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
        _form.BringToFront();
    }

    public void OpenLogFolder()
    {
        try
        {
            var dir = LogDirectory();
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log($"打开日志目录失败：{ex.Message}");
        }
    }

    // ---------- 热键 ----------

    internal void OnHotkey(int id)
    {
        switch (id)
        {
            case HotkeyWindow.HkShowUi:
                ShowWindow();
                break;
            case HotkeyWindow.HkNormal:
                Start(Mode.Normal, "Alt+1");
                break;
            case HotkeyWindow.HkLockMouse:
                Start(Mode.LockMouse, "Alt+2");
                break;
            case HotkeyWindow.HkRestore:
                RestoreAll("Alt+3");
                break;
        }
    }

    // ---------- 内部 ----------

    private void Start(Mode mode, string reason)
    {
        if (_mode != Mode.Off) ShutdownToIdle();

        _mode = mode;

        _blocker.Stop();
        if (mode == Mode.LockMouse) _blocker.Install();

        _mutedByUs = _muteOnLock && AudioMute.SetMute(true);
        _lastInput = Native.LastInputTick();
        _sent = 0;
        _lastOff = Environment.TickCount64;
        _lastMuteCheck = _lastOff;

        Native.ScreenOff();

        Log($"{ModeName(mode)} 开始（{reason}）静音={_mutedByUs} 鼠标钩子={_blocker.Active}");
        UpdateTray();
        _form?.Invoke(new Action(() => _form?.Refresh()));
    }

    private void ShutdownToIdle()
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
        if (input != _lastInput) _lastInput = input;

        // 关键：只有「这段时间完全没人碰输入」才补发灭屏。
        // 之前是「检测到输入就补发」，鼠标一动就发一次 → 屏幕被反复点亮又灭 → 看起来在闪。
        var curTick = unchecked((uint)Environment.TickCount);
        var idleMs = curTick - input;                 // 同为 32 位 tick，减法自动回绕
        var sinceOff = now - _lastOff;

        var fire = idleMs >= (uint)IdleBeforeOffMs && sinceOff >= MinGapMs;
        if (fire)
        {
            Native.ScreenOff();
            _lastOff = Environment.TickCount64;
            _sent++;
            if (_sent <= 5 || _sent % 60 == 0)
            {
                Log($"补灭屏 第 {_sent} 次（空闲 {idleMs}ms）");
            }
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

    /// <summary>停止操作多久之后才补灭屏（毫秒）。0.6 秒后补，几乎感觉不到延迟。</summary>
    private const int IdleBeforeOffMs = 600;

    /// <summary>两次灭屏指令之间至少间隔多久（毫秒），防止频闪。</summary>
    private const int MinGapMs = 5000;

    /// <summary>界面在前台时放行鼠标，否则锁鼠标模式下连按钮都点不了。</summary>
    private bool FormIsForeground()
    {
        if (_form == null || _form.IsDisposed || !_form.Visible) return false;
        return Native.GetForegroundWindow() == _form.Handle;
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开界面（Alt+0）", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("① 静默模式（Alt+1）", null, (_, _) => Start(Mode.Normal, "托盘"));
        menu.Items.Add("② 锁鼠标模式（Alt+2）", null, (_, _) => Start(Mode.LockMouse, "托盘"));
        menu.Items.Add("③ 恢复屏幕（Alt+3）", null, (_, _) => RestoreAll("托盘"));
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

        _tray.Text = $"关屏 · {state}（双击打开界面）";
    }

    private void ExitAll()
    {
        RestoreAll("退出");
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

    // ---------- 设置与日志 ----------

    private static string DataDirectory()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScreenOff");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string LogDirectory() => DataDirectory();

    private static string SettingsPath() => Path.Combine(DataDirectory(), "settings.ini");

    private void LoadSettings()
    {
        try
        {
            var path = SettingsPath();
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path))
            {
                var idx = line.IndexOf('=');
                if (idx <= 0) continue;
                var key = line[..idx].Trim();
                var value = line[(idx + 1)..].Trim();
                if (key.Equals("mute", StringComparison.OrdinalIgnoreCase))
                {
                    _muteOnLock = value != "0";
                }
            }
        }
        catch
        {
            // 设置读取失败就用默认值
        }
    }

    private void SaveSettings()
    {
        try
        {
            File.WriteAllText(SettingsPath(), $"mute={(_muteOnLock ? 1 : 0)}{Environment.NewLine}");
        }
        catch
        {
            // 写失败不影响功能
        }
    }

    internal static string ReadLogTail(int lines)
    {
        try
        {
            var path = Path.Combine(LogDirectory(), "screenoff.log");
            if (!File.Exists(path)) return "（还没有日志）";
            var all = File.ReadAllLines(path);
            var start = Math.Max(0, all.Length - lines);
            return string.Join(Environment.NewLine, all[start..]);
        }
        catch (Exception ex)
        {
            return $"（日志读取失败：{ex.Message}）";
        }
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(LogDirectory(), "screenoff.log"),
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
            try { if (_form != null && !_form.IsDisposed) _form.Dispose(); } catch { }
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}