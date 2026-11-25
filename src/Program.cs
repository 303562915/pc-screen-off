using System;
using System.Windows.Forms;

namespace ScreenOff;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var ctx = new TrayContext();
        Application.Run(ctx);
    }
}
