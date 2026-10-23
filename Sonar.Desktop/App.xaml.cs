using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace Sonar;

public partial class App : Application
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 独立的任务栏身份，避免与旧版本 / 旧固定项共用图标缓存。
        SetCurrentProcessExplicitAppUserModelID("Sonar.Desktop.0.3");

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var log = Path.Combine(AppPaths.DataDir, "error.log");
            Directory.CreateDirectory(AppPaths.DataDir);
            File.AppendAllText(log,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\n\n");
        }
        catch
        {
            // 日志失败时不再抛出，避免掩盖原始异常
        }

        MessageBox.Show(
            "程序遇到未处理的异常：\n\n" + e.Exception.Message +
            "\n\n详细信息已写入 ~/.sonar/error.log",
            "声呐 Sonar", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
