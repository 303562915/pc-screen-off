using System;
using System.Drawing;
using System.Windows.Forms;

namespace ScreenOff;

/// <summary>主界面：两个开始按钮、一个恢复按钮、静音开关、状态与日志。</summary>
internal sealed class MainForm : Form
{
    private readonly TrayContext _owner;
    private readonly Timer _refresh;

    private Label _status;
    private Label _hint;
    private Button _btnNormal;
    private Button _btnLock;
    private Button _btnRestore;
    private CheckBox _chkMute;
    private TextBox _log;

    public MainForm(TrayContext owner)
    {
        _owner = owner;

        Text = "关屏";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = new Size(460, 430);
        Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = Color.FromArgb(246, 247, 250);

        BuildUi();

        _refresh = new Timer { Interval = 700 };
        _refresh.Tick += (_, _) => RefreshState();
        _refresh.Start();

        FormClosing += OnFormClosing;
    }

    private void BuildUi()
    {
        var header = new Label
        {
            Text = "关屏",
            Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold),
            ForeColor = Color.FromArgb(30, 42, 58),
            AutoSize = true,
            Location = new Point(20, 16)
        };
        Controls.Add(header);

        var sub = new Label
        {
            Text = "屏幕关掉、声音静音，系统继续运行。Alt+1 静默 / Alt+2 锁鼠标 / Alt+3 恢复 / Alt+0 打开本窗口",
            ForeColor = Color.FromArgb(110, 118, 132),
            AutoSize = false,
            Size = new Size(420, 34),
            Location = new Point(20, 44)
        };
        Controls.Add(sub);

        _status = new Label
        {
            Text = "",
            Font = new Font("Microsoft YaHei UI", 10f),
            AutoSize = false,
            Size = new Size(420, 24),
            Location = new Point(20, 86)
        };
        Controls.Add(_status);

        _btnNormal = MakeButton("① 开始 · 静默模式（Alt+1）", new Point(20, 118), OnNormal);
        _btnLock = MakeButton("② 开始 · 锁鼠标模式（Alt+2）", new Point(20, 158), OnLock);

        _btnRestore = MakeButton("③ 恢复屏幕（Alt+3）", new Point(20, 206), OnRestore);
        _btnRestore.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);

        _chkMute = new CheckBox
        {
            Text = "关屏时同时静音（恢复时自动取消静音）",
            Checked = _owner.MuteOnLock,
            AutoSize = true,
            Location = new Point(22, 254)
        };
        _chkMute.CheckedChanged += (_, _) => _owner.MuteOnLock = _chkMute.Checked;
        Controls.Add(_chkMute);

        _hint = new Label
        {
            Text = "",
            ForeColor = Color.FromArgb(110, 118, 132),
            AutoSize = false,
            Size = new Size(420, 40),
            Location = new Point(20, 282)
        };
        Controls.Add(_hint);

        _log = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 8.5f),
            Size = new Size(420, 108),
            Location = new Point(20, 322)
        };
        Controls.Add(_log);

        var btnLog = new Button
        {
            Text = "打开日志文件夹",
            FlatStyle = FlatStyle.Flat,
            Size = new Size(130, 26),
            Location = new Point(20, 434 - 24)
        };
        btnLog.Click += (_, _) => _owner.OpenLogFolder();
        Controls.Add(btnLog);

        RefreshState();
    }

    private Button MakeButton(string text, Point location, Action onClick)
    {
        var btn = new Button
        {
            Text = text,
            TextAlign = ContentAlignment.MiddleLeft,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            Size = new Size(420, 34),
            Location = location
        };
        btn.Click += (_, _) => onClick();
        Controls.Add(btn);
        return btn;
    }

    private void OnNormal()
    {
        _owner.StartNormal("界面");
    }

    private void OnLock()
    {
        _owner.StartLockMouse("界面");
    }

    private void OnRestore()
    {
        _owner.RestoreAll("界面");
    }

    private void RefreshState()
    {
        var s = _owner.Snapshot();

        _status.Text = $"当前状态：{s.StateText}";
        _status.ForeColor = s.Running ? Color.FromArgb(0, 121, 107) : Color.FromArgb(84, 92, 106);

        _btnRestore.Enabled = s.Running || s.MutedByUs;

        _hint.Text =
            $"热键 {(s.Hotkeys ? "已就绪" : "未注册（可能被其他程序占用，用下面按钮）")}" +
            $"　灭屏指令已发 {s.Sent} 次　{(s.MutedByUs ? "当前静音中" : "当前未静音")}";

        _log.Text = TrayContext.ReadLogTail(20);
    }

    private void OnFormClosing(object sender, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refresh?.Stop();
            _refresh?.Dispose();
        }

        base.Dispose(disposing);
    }
}