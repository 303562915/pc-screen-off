using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace ScreenOff;

/// <summary>
/// 极简主界面：
///   - 顶部一个大状态标签
///   - 中间一个主按钮（未启用=关屏 / 运行中=恢复）
///   - 底下一排小 segmented 切换（关屏 / 锁鼠标 / 恢复 / 日志）
/// 不放说明文字，热键只做小字角标。
/// </summary>
internal sealed class MainForm : Form
{
    private static readonly Color BgColor = Color.FromArgb(18, 20, 24);
    private static readonly Color CardColor = Color.FromArgb(28, 31, 37);
    private static readonly Color TextColor = Color.FromArgb(236, 239, 244);
    private static readonly Color SubTextColor = Color.FromArgb(138, 146, 158);
    private static readonly Color AccentColor = Color.FromArgb(77, 208, 176);
    private static readonly Color WarnColor = Color.FromArgb(224, 122, 95);

    private readonly TrayContext _owner;
    private readonly Timer _refresh;

    private Label _state;
    private Label _meta;
    private PillButton _primary;
    private PillButton _segNormal;
    private PillButton _segLock;
    private PillButton _segRestore;
    private PillButton _segMute;
    private LinkLabel _logLink;

    public MainForm(TrayContext owner)
    {
        _owner = owner;

        Text = "关屏";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = new Size(340, 236);
        BackColor = BgColor;
        Icon = AppIcon.Get();
        Font = new Font("Microsoft YaHei UI", 9f);

        BuildUi();

        _refresh = new Timer { Interval = 500 };
        _refresh.Tick += (_, _) => RefreshState();
        _refresh.Start();

        FormClosing += OnFormClosing;
        RefreshState();
    }

    private void BuildUi()
    {
        _state = new Label
        {
            Text = "未启用",
            Font = new Font("Microsoft YaHei UI", 19f, FontStyle.Regular),
            ForeColor = TextColor,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Size = new Size(300, 34),
            Location = new Point(20, 34)
        };
        Controls.Add(_state);

        _meta = new Label
        {
            Text = "",
            Font = new Font("Microsoft YaHei UI", 8.5f),
            ForeColor = SubTextColor,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Size = new Size(300, 18),
            Location = new Point(20, 68)
        };
        Controls.Add(_meta);

        _primary = new PillButton
        {
            Text = "关屏",
            Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold),
            Size = new Size(300, 48),
            Location = new Point(20, 100),
            FillColor = AccentColor,
            TextColor = Color.FromArgb(10, 30, 26)
        };
        _primary.Click += (_, _) => OnPrimary();
        Controls.Add(_primary);

        var y = 162;
        var w = 71;
        var gap = 5;

        _segNormal = MakeSeg("关屏", y, 20, w, () => _owner.StartNormal("界面"));
        _segLock = MakeSeg("锁鼠标", y, 20 + (w + gap), w, () => _owner.StartLockMouse("界面"));
        _segRestore = MakeSeg("恢复", y, 20 + (w + gap) * 2, w, () => _owner.RestoreAll("界面"));
        _segMute = MakeSeg("静音", y, 20 + (w + gap) * 3, w, ToggleMute);

        _logLink = new LinkLabel
        {
            Text = "日志",
            LinkColor = SubTextColor,
            ActiveLinkColor = AccentColor,
            VisitedLinkColor = SubTextColor,
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 8.5f),
            Location = new Point(20, 206)
        };
        _logLink.LinkClicked += (_, _) => _owner.OpenLogFolder();
        Controls.Add(_logLink);
    }

    private PillButton MakeSeg(string text, int y, int x, int w, Action onClick)
    {
        var b = new PillButton
        {
            Text = text,
            Font = new Font("Microsoft YaHei UI", 9f),
            Size = new Size(w, 32),
            Location = new Point(x, y),
            FillColor = CardColor,
            TextColor = SubTextColor,
            Radius = 8
        };
        b.Click += (_, _) => onClick();
        Controls.Add(b);
        return b;
    }

    private void OnPrimary()
    {
        var s = _owner.Snapshot();
        if (s.Running) _owner.RestoreAll("界面");
        else _owner.StartNormal("界面");
    }

    private void ToggleMute()
    {
        _owner.MuteOnLock = !_owner.MuteOnLock;
        RefreshState();
    }

    private void RefreshState()
    {
        var s = _owner.Snapshot();

        _state.Text = s.Running ? "屏幕已关闭" : "未启用";
        _state.ForeColor = s.Running ? AccentColor : TextColor;

        _primary.Text = s.Running ? "恢复屏幕" : "关屏";
        _primary.FillColor = s.Running ? WarnColor : AccentColor;
        _primary.TextColor = s.Running ? Color.FromArgb(36, 12, 8) : Color.FromArgb(10, 30, 26);
        _primary.Invalidate();

        _meta.Text = "Alt+1 静默　Alt+2 锁鼠标　Alt+3 恢复　Alt+0 界面";
        _meta.ForeColor = SubTextColor;

        _segMute.Text = _owner.MuteOnLock ? "静音 开" : "静音 关";
        _segMute.TextColor = _owner.MuteOnLock ? TextColor : SubTextColor;
        _segMute.Invalidate();
    }

    private void OnFormClosing(object sender, FormClosingEventArgs e)
    {
        if (e.CloseReason != CloseReason.UserClosing) return;
        e.Cancel = true;
        Hide();
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

    /// <summary>圆角按钮：去掉系统边框，纯色填充 + 居中文字。</summary>
    private sealed class PillButton : Button
    {
        public Color FillColor { get; set; } = Color.Gray;
        public Color TextColor = Color.White;
        public int Radius { get; set; } = 12;

        private bool _hover;
        private bool _down;

        public PillButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            _down = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            _down = true;
            Invalidate();
            base.OnMouseDown(mevent);
        }

        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            _down = false;
            Invalidate();
            base.OnMouseUp(mevent);
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            var g = pevent.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            var fill = FillColor;

            if (!Enabled) fill = Blend(fill, BgColor, 0.55f);
            else if (_down) fill = Blend(fill, Color.Black, 0.22f);
            else if (_hover) fill = Blend(fill, Color.White, 0.10f);

            using var path = Rounded(rect, Radius);
            using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);

            TextRenderer.DrawText(
                g,
                Text,
                Font,
                rect,
                Enabled ? TextColor : SubTextColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private static GraphicsPath Rounded(Rectangle rect, int radius)
        {
            var d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static Color Blend(Color a, Color b, float amount)
            => Color.FromArgb(
                (int)(a.R + (b.R - a.R) * amount),
                (int)(a.G + (b.G - a.G) * amount),
                (int)(a.B + (b.B - a.B) * amount));
    }
}