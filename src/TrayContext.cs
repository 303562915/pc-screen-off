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
    private readonly InputLock _inputLock = new();
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

        // 钩子跑在别的线程；ApplicationContext 本身不是控件，用托盘图标所在 UI 线程 marshal
        _inputLock.AltThreePressed += () => _tray?.ContextMenuStrip?.BeginInvoke((Action)(() => RestoreAll("Alt+3（锁定中）")));

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
            Mode.LockMouse => "全锁定中 · 屏幕关闭，只有 Alt+3 能解开",
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

        _inputLock.Stop();
        if (mode == Mode.LockMouse) _inputLock.Install(true);

        _mutedByUs = _muteOnLock && AudioMute.SetMute(true);
        _lastInput = Native.LastInputTick();
        _sent = 0;
        _lockMouseGapMs = 1500;
        _lastOff = Environment.TickCount64;
        _lastMuteCheck = _lastOff;

        Native.ScreenOff();

        Log($"{ModeName(mode)} 开始（{reason}）静音={_mutedByUs} 输入锁={_inputLock.Active}");
        UpdateTray();
        _form?.Invoke(new Action(() => _form?.Refresh()));
    }

    private void ShutdownToIdle()
    {
        _inputLock.Stop();
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

        // ---- 静默模式：检测到输入后,等输入停了(600ms)才补灭屏 ----
        // 这样不会「亮-灭-亮-灭」频闪;代价是手放在鼠标上时屏幕是亮的。
        if (_mode == Mode.Normal)
        {
            var curTick = unchecked((uint)Environment.TickCount);
            var input = Native.LastInputTick();
            var idleMs = curTick - input;
            var sinceOff = now - _lastOff;

            if (idleMs >= (uint)IdleBeforeOffMs && sinceOff >= MinGapMs)
            {
                Native.ScreenOff();
                _lastOff = Environment.TickCount64;
                _sent++;
                if (_sent <= 5 || _sent % 60 == 0)
                    Log($"补灭屏 第 {_sent} 次（空闲 {idleMs}ms）");
            }
        }
        // ---- 锁鼠标模式：物理鼠标已被吞掉，应由程序自己维持黑色 ----
        // 关键经验（2026-11-26 实测）：面板收到一次关屏后不会立刻「稳定黑」，
        // 它要 ~1.5 秒才真正进入低功耗。这段时间里如果再发一次关屏，
        // 面板会被重新上电一次 → 就是肉眼看到的「一直闪」。
        // 所以这里必须给足静默期，而不是 0.8 秒一发。
        else
        {
            if (now - _lastOff >= _lockMouseGapMs)
            {
                Native.ScreenOff();
                _lastOff = Environment.TickCount64;
                _sent++;
                // 从第 2 次起逐步拉长间隔：1.5s → 3s → 6s → 10s 封顶。
                // 面板要是被别的程序反复点亮，10 秒一次也足够把它压回去，且不会再闪。
                _lockMouseGapMs = Math.Min(_lockMouseGapMs * 2, 10000);
                Log($"锁鼠标补灭屏 第 {_sent} 次（下次间隔 {_lockMouseGapMs}ms）");
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

    /// <summary>锁鼠标模式当前的重试间隔（毫秒）。会被逐步拉长到 10 秒。</summary>
    private long _lockMouseGapMs = 1500;

    /// <summary>停止操作多久之后才补灭屏（毫秒）。0.6 秒后补，几乎感觉不到延迟。</summary>
    private const int IdleBeforeOffMs = 600;

    /// <summary>两次灭屏指令之间至少间隔多久（毫秒），防止频闪。</summary>
    private const int MinGapMs = 5000;

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开界面（Alt+0）", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("① 静默模式（Alt+1）", null, (_, _) => Start(Mode.Normal, "托盘"));
        menu.Items.Add("② 全锁定模式（Alt+2，只有 Alt+3 能解）", null, (_, _) => Start(Mode.LockMouse, "托盘"));
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
            Mode.LockMouse => "全锁定中（Alt+3 解除）",
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
            try { _inputLock?.Dispose(); } catch { }
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