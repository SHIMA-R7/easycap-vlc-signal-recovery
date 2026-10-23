using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
[assembly: AssemblyTitle("EasyCAP Launcher")]
[assembly: AssemblyVersion("1.0.0.0")]
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string folder = AppDomain.CurrentDomain.BaseDirectory;
        string script = Path.Combine(folder, "Capture-EasyCAP.ps1");
        try
        {
            if (!File.Exists(script)) throw new FileNotFoundException("Capture-EasyCAP.ps1 が見つかりません。EXEをEasy_CAPtureフォルダーに置いてください。", script);
            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(powershell)) throw new FileNotFoundException("Windows PowerShell が見つかりません。", powershell);
            if (args.Length == 1 && args[0] == "--check") return 0;
            var info = new ProcessStartInfo(powershell);
            string extra = " -Preview";
            if (Path.GetFileNameWithoutExtension(Application.ExecutablePath).Equals("Record-EasyCAP", StringComparison.OrdinalIgnoreCase) || (args.Length == 1 && args[0] == "--record"))
            {
                string captures = Path.Combine(folder, "recordings");
                Directory.CreateDirectory(captures);
                string output = Path.Combine(captures, "EasyCAP-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".mkv");
                extra += " -RecordingPath \"" + output + "\"";
            }
            info.Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\"" + extra;
            info.WorkingDirectory = folder;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardError = true;
            info.StandardErrorEncoding = Encoding.UTF8;
            using (var process = Process.Start(info))
            {
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new Exception("EasyCAPを起動できませんでした。接続と音声入力を確認してください。\r\n\r\n" + error);
            }
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "EasyCAP", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}