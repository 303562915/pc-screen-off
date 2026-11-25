using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ScreenOff;

/// <summary>运行时绘制图标（不往仓库塞二进制 ico）：深色圆底 + 青色电源符号。</summary>
internal static class AppIcon
{
    private static Icon _cached;

    public static Icon Get()
    {
        if (_cached != null) return _cached;

        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using (var bg = new SolidBrush(Color.FromArgb(18, 20, 24)))
            {
                g.FillEllipse(bg, 0, 0, 63, 63);
            }

            var accent = Color.FromArgb(77, 208, 176);
            using (var pen = new Pen(accent, 6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                // 电源符号：下方 270° 圆弧 + 顶部竖线
                g.DrawArc(pen, 14f, 16f, 36f, 36f, 300, 300);
                g.DrawLine(pen, 32f, 10f, 32f, 30f);
            }
        }

        var handle = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(handle).Clone() as Icon;
            _cached = (Icon)tmp.Clone();
        }
        finally
        {
            Native.DestroyIcon(handle);
        }

        return _cached;
    }
}