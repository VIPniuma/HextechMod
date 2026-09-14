using System;
using System.Windows;

namespace PeakModder.HextechConfigurator;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 兜底：任何 UI 线程上的未处理异常都弹窗而不是默默闪退（曾经「上传服务器」时崩过）。
        Application.Current.DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(
                e.Exception.Message + "\n\n" + e.Exception.StackTrace,
                "海克斯平衡配置器出错了",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            e.Handled = true;
        };

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Run(new MainWindow());
    }
}
