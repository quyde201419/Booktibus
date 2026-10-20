using System.Windows;
using System.Windows.Threading;
using Booktibus.Helpers;
using Booktibus.Services;

namespace Booktibus
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
            base.OnStartup(e);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            AppDiagnosticLogger.LogException("App", "DispatcherUnhandledException", e.Exception);

            string title = LocalizationService.IsVietnamese
                ? "Booktibus — Thông báo Hệ thống"
                : "Booktibus — System Notice";

            string message = LocalizationService.IsVietnamese
                ? "Đã xảy ra lỗi ngoài dự kiến khi xử lý tác vụ. Hệ thống đã ghi nhận chi tiết kỹ thuật an toàn và giữ nguyên phiên làm việc của bạn.\n\nChi tiết: " +
                  AppDiagnosticLogger.SanitizeTechnicalText(e.Exception.Message)
                : "An unexpected error occurred while processing the operation. Technical details have been safely logged and your workspace remains active.\n\nDetails: " +
                  AppDiagnosticLogger.SanitizeTechnicalText(e.Exception.Message);

            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }

        private static void OnCurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                AppDiagnosticLogger.LogException("App", "CurrentDomainUnhandledException", ex);
            }
        }
    }
}

