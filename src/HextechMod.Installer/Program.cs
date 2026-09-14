using System;
using System.Net;
using System.Threading.Tasks;
using System.Windows;

namespace PeakModder.HextechInstaller;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Thunderstore 只收 TLS 1.2 以上，.NET Framework 默认不一定开。
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Report(e.Exception);
            e.SetObserved();
        };

        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose,
        };

        app.DispatcherUnhandledException += (_, e) =>
        {
            Report(e.Exception);
            e.Handled = true;
        };

        // 启动加载页做在主窗口内部（MainWindow 的 loading overlay），这里保持最简单的启动流程。
        app.Run(new MainWindow());
    }

    /// <summary>兜底：任何没被接住的异常都弹个框，别让窗口无声无息地消失。</summary>
    private static void Report(Exception? exception)
    {
        if (exception == null)
        {
            return;
        }

        MessageBox.Show(
            exception.Message,
            "小王同学模组安装器",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
