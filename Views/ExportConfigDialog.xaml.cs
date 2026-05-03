using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        public ExportConfigDialog(Device device)
        {
            InitializeComponent();
            _device = device;
            StartDatePicker.SelectedDate = DateTime.Today;
            EndDatePicker.SelectedDate = DateTime.Now;
        }

        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            var start = StartDatePicker.SelectedDate ?? DateTime.Today;
            var end = EndDatePicker.SelectedDate ?? DateTime.Now;

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

                using var dataService = new DataService();
                await dataService.InitializeAsync();

                var tempLogs = exportTemp
                    ? await dataService.GetTemperatureLogsByDeviceAsync(_device.Id, start, end)
                    : new List<TemperatureLog>();

                var opLogs = exportOp
                    ? await dataService.GetOperationLogsByDeviceAsync(_device.Id, start, end)
                    : new List<OperationLog>();

                var excelService = new ExcelExportService();
                var filePath = await excelService.ExportDeviceDataAsync(_device, tempLogs, opLogs);

                MessageBox.Show($"导出成功！\n\n文件：{filePath}", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
                Close();
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
    }
}
