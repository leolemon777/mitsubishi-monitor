using System.Windows;
using MitsubishiMonitor.Demo.ViewModels;
using MitsubishiMonitor.Demo.Views;

namespace MitsubishiMonitor.Demo
{
    public partial class App : Application
    {
        /// <summary>
        /// 应用退出时兜底清理：即使主窗口被异常关闭，也确保 PLC 断开、日志缓冲刷写、三色灯熄灭。
        /// </summary>
        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                if (MainWindow is MainWindow mw && mw.DataContext is DeviceListViewModel vm)
                {
                    vm.Dispose();
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] OnExit 清理异常: {ex.Message}");
            }
            base.OnExit(e);
        }
    }
}
