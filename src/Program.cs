using System;
using System.Threading;
using System.Windows.Forms;

namespace ScreenOff;

internal static class Program
{
    private const string MutexName = @"Local\ScreenOff.SingleInstance";

    [STAThread]
    private static void Main()
    {
        // 单实例：两个副本会抢全局热键，第二个直接退出
        using var mutex = new Mutex(true, MutexName, out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show(
                "关屏已经在运行了，看右下角托盘图标（Alt+0 可以把界面叫出来）。",
                "关屏",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var ctx = new TrayContext();
        Application.Run(ctx);

        GC.KeepAlive(mutex);
    }
}