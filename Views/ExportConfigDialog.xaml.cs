using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MitsubishiMonitor.Demo.Models;
using MitsubishiMonitor.Demo.Services;

namespace MitsubishiMonitor.Demo.Views
{
    /// <summary>
    /// 数据导出配置对话框
    /// </summary>
    public partial class ExportConfigDialog : Window
    {
        private readonly Device _device;
        private CancellationTokenSource _exportCts;

        public ExportConfigDialog(Device device)
        {
            InitializeComponent();
            _device = device;
            StartDatePicker.SelectedDate = DateTime.Today;
            EndDatePicker.SelectedDate = DateTime.Now;
        }

        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            var start = (StartDatePicker.SelectedDate ?? DateTime.Today).Date;
            var selectedEnd = EndDatePicker.SelectedDate ?? DateTime.Today;
            // DatePicker 只保留日期。结束日若为今天则截止当前时刻，否则包含整天。
            var end = selectedEnd.Date == DateTime.Today
                ? DateTime.Now
                : selectedEnd.Date.AddDays(1).AddTicks(-1);

            if (start > end)
            {
                MessageBox.Show("开始时间不能晚于结束时间", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var exportTemp = ChkTemp.IsChecked == true;
            var exportOp = ChkOp.IsChecked == true;

            if (!exportTemp && !exportOp)
            {
                MessageBox.Show("请至少选择一种数据类型", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                IsEnabled = false;
                var exportSource = new CancellationTokenSource();
                var previousExport = Interlocked.Exchange(ref _exportCts, exportSource);
                previousExport?.Cancel();
                previousExport?.Dispose();
                var cancellationToken = exportSource.Token;

                using var dataService = new DataService();
                await dataService.InitializeAsync(cancellationToken);
                var exportData = await BoundedLogExportLoader.LoadAsync(
                    dataService,
                    _device.Id,
                    start,
                    end,
                    exportTemp,
                    exportOp,
                    cancellationToken);

                var excelService = new ExcelExportService();
                var filePath = await excelService.ExportDeviceDataAsync(
                    _device,
                    exportData.TemperatureLogs,
                    exportData.OperationLogs,
                    cancellationToken: cancellationToken);

                MessageBox.Show($"导出成功！\n\n文件：{filePath}", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
                Close();
            }
            catch (OperationCanceledException)
            {
                // 对话框关闭时正常取消。
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsEnabled = true;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            try { _exportCts?.Cancel(); _exportCts?.Dispose(); } catch { }
            base.OnClosed(e);
        }
    }
}
