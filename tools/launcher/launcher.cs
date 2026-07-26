using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// Tiny zero-dependency launcher placed at the zip root so recipients see a single
// obvious "实时翻译.exe" instead of hunting through 700 files in app\.
// Compiled with the inbox .NET Framework csc (present on every Windows 10/11):
//   %windir%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
//     /target:winexe /codepage:65001 /win32icon:app.ico /out:实时翻译.exe launcher.cs
static class Launcher
{
    [STAThread]
    static void Main()
    {
        string root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        string exe = Path.Combine(root, "app", "LiveTranslate.exe");
        if (File.Exists(exe))
        {
            ProcessStartInfo info = new ProcessStartInfo(exe);
            info.WorkingDirectory = Path.Combine(root, "app");
            info.UseShellExecute = true;
            Process.Start(info);
        }
        else
        {
            MessageBox.Show(
                "未找到 app\\LiveTranslate.exe。\r\n请先把压缩包完整解压到一个文件夹，再运行本程序。",
                "实时翻译",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }
}
