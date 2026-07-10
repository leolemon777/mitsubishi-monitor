using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MitsubishiMonitor.Demo.Models;
using MitsubishiMonitor.Demo.Services;

namespace MitsubishiMonitor.Demo.ViewModels
{
    /// <summary>
    /// 设备筛选项（下拉框用：null = 全部设备）
    /// </summary>
    public class DeviceFilterItem
    {
        public int? DeviceId { get; set; }
        public string DisplayName { get; set; } = "";
        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// 日志查询页 ViewModel：
    /// - 设备下拉（全部 / 1~4）
    /// - 时间范围（今日 / 本周 / 本月 / 全部 / 自定义）
    /// - Tab 切换"操作日志 / 温度日志"
    /// - 客户端做关键字 + 类型过滤，DB 查询只在设备/时间变化时跑，避免每输入一个字符就查库
    /// </summary>
    public partial class LogQueryViewModel : ObservableObject, IDisposable
    {
        private readonly DeviceManagerService _deviceManager;
        private readonly ExcelExportService _excelService = new();
        private bool _disposed;
        private const int MaxOperationRows = 5000;
        private const int MaxTemperatureRows = 5000;

        /// <summary>
        /// 当前进行中的 LoadAsync 调度令牌：每次切设备/时间范围都换新 token，
        /// 老的查询返回时直接丢弃结果，避免快速切换导致结果错乱。
        /// </summary>
        private CancellationTokenSource _loadCts;

        public ObservableCollection<DeviceFilterItem> DeviceOptions { get; } = new();
        public ObservableCollection<string> TimeRanges { get; } =
            new() { "今日", "本周", "本月", "全部" };
        public ObservableCollection<string> LogTypes { get; } =
            new() { "全部", "X", "Y", "M" };

        [ObservableProperty] private DeviceFilterItem _selectedDevice;
        [ObservableProperty] private string _selectedTimeRange = "今日";
        [ObservableProperty] private DateTime _filterStartDate = DateTime.Today;
        [ObservableProperty] private DateTime _filterEndDate = DateTime.Now;
        [ObservableProperty] private string _searchText = "";
        [ObservableProperty] private string _selectedLogType = "全部";

        [ObservableProperty] private bool _isLoading;
        [ObservableProperty] private string _statusText = "就绪";
        [ObservableProperty] private int _operationLogCount;
        [ObservableProperty] private int _temperatureLogCount;

        /// <summary>当前操作日志视图中的行数（筛选后）</summary>
        [ObservableProperty] private int _operationFilteredCount;
        /// <summary>当前温度日志视图中的行数（筛选后）</summary>
        [ObservableProperty] private int _temperatureFilteredCount;
        [ObservableProperty] private bool _showOperationEmptyState;
        [ObservableProperty] private bool _showTemperatureEmptyState;
        [ObservableProperty] private string _operationEmptyMessage = "";
        [ObservableProperty] private string _temperatureEmptyMessage = "";

        public ObservableCollection<OperationLog> OperationLogs { get; } = new();
        public ObservableCollection<TemperatureLog> TemperatureLogs { get; } = new();

        public ICollectionView OperationLogsView { get; }
        public ICollectionView TemperatureLogsView { get; }

        public LogQueryViewModel(DeviceManagerService deviceManager)
        {
            _deviceManager = deviceManager;

            // 设备下拉框：第一项"全部"，其余按 Device 列表排序
            DeviceOptions.Add(new DeviceFilterItem { DeviceId = null, DisplayName = "全部设备" });
            foreach (var d in _deviceManager.Devices)
            {
                DeviceOptions.Add(new DeviceFilterItem
                {
                    DeviceId = d.Id,
                    DisplayName = d.Name
                });
            }
            _selectedDevice = DeviceOptions[0];

            // 客户端过滤视图：按 SearchText / SelectedLogType 实时过滤
            OperationLogsView = CollectionViewSource.GetDefaultView(OperationLogs);
            OperationLogsView.Filter = OperationLogFilter;

            TemperatureLogsView = CollectionViewSource.GetDefaultView(TemperatureLogs);
            TemperatureLogsView.Filter = TemperatureLogFilter;

            // 默认进来加载"今日"
            _ = LoadAsync();
        }

        // -------- 过滤器 --------

        private bool OperationLogFilter(object item)
        {
            if (item is not OperationLog log) return false;

            if (SelectedLogType != "全部" && log.LogType != SelectedLogType)
                return false;

            var kw = SearchText?.Trim();
            if (string.IsNullOrEmpty(kw)) return true;
            kw = kw.ToLowerInvariant();

            return Contains(log.DeviceName, kw)
                || Contains(log.PointAddress, kw)
                || Contains(log.PointLabel, kw)
                || Contains(log.Action, kw)
                || Contains(log.Description, kw)
                || Contains(log.LogType, kw);
        }

        private bool TemperatureLogFilter(object item)
        {
            if (item is not TemperatureLog log) return false;

            var kw = SearchText?.Trim();
            if (string.IsNullOrEmpty(kw)) return true;
            kw = kw.ToLowerInvariant();

            return Contains(log.DeviceName, kw)
                || log.Temperature.ToString("F1").Contains(kw);
        }

        private static bool Contains(string s, string kw)
            => !string.IsNullOrEmpty(s) && s.ToLowerInvariant().Contains(kw);

        // -------- 属性变化触发 --------

        partial void OnSelectedDeviceChanged(DeviceFilterItem value)
        {
            _ = LoadAsync();
        }

        partial void OnSearchTextChanged(string value)
        {
            // 仅触发客户端过滤，不重查库
            OperationLogsView.Refresh();
            TemperatureLogsView.Refresh();
            UpdateStatusText();
        }

        partial void OnSelectedLogTypeChanged(string value)
        {
            OperationLogsView.Refresh();
            UpdateStatusText();
        }

        partial void OnFilterStartDateChanged(DateTime value)
        {
            // 自定义时间会触发；常规四档由 ChangeTimeRangeAsync 设置后再调 LoadAsync
        }

        partial void OnFilterEndDateChanged(DateTime value)
        {
        }

        // -------- 命令 --------

        [RelayCommand]
        private async Task ChangeTimeRangeAsync(string range)
        {
            SelectedTimeRange = range ?? "今日";
            var now = DateTime.Now;

            switch (SelectedTimeRange)
            {
                case "今日":
                    FilterStartDate = now.Date;
                    FilterEndDate = now;
                    break;
                case "本周":
                    var dayOfWeek = (int)now.DayOfWeek;
                    if (dayOfWeek == 0) dayOfWeek = 7; // 把周日当作每周最后一天，便于"本周一开始"
                    FilterStartDate = now.Date.AddDays(-(dayOfWeek - 1));
                    FilterEndDate = now;
                    break;
                case "本月":
                    FilterStartDate = new DateTime(now.Year, now.Month, 1);
                    FilterEndDate = now;
                    break;
                case "全部":
                    var confirm = MessageBox.Show(
                        "选择\"全部\"会拉取数据库内的全部历史记录，可能耗时数秒到数十秒。\n\n是否继续？",
                        "提示", MessageBoxButton.OKCancel, MessageBoxImage.Information);
                    if (confirm != MessageBoxResult.OK)
                    {
                        // 取消则回退到"今日"
                        SelectedTimeRange = "今日";
                        FilterStartDate = now.Date;
                        FilterEndDate = now;
                    }
                    else
                    {
                        FilterStartDate = new DateTime(2000, 1, 1);
                        FilterEndDate = now;
                    }
                    break;
            }

            await LoadAsync();
        }

        [RelayCommand]
        private async Task RefreshAsync() => await LoadAsync();

        [RelayCommand]
        private async Task ExportAsync()
        {
            try
            {
                if (OperationLogs.Count == 0 && TemperatureLogs.Count == 0)
                {
                    MessageBox.Show("当前没有数据可导出，请先查询。", "提示",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var deviceLabel = SelectedDevice?.DeviceId.HasValue == true
                    ? SelectedDevice.DisplayName
                    : "全部设备";

                var path = await _excelService.ExportLogsReadablePackageAsync(
                    deviceLabel,
                    FilterStartDate, FilterEndDate,
                    TemperatureLogs.ToList(),
                    OperationLogs.ToList());

                MessageBox.Show(
                    $"导出成功！\n\n范围: {deviceLabel} / {FilterStartDate:yyyy-MM-dd HH:mm} ~ {FilterEndDate:yyyy-MM-dd HH:mm}\n操作日志: {OperationLogs.Count} 条\n温度日志: {TemperatureLogs.Count} 条\n\n工控机可直接打开:\n{path}",
                    "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败:\n{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // -------- 加载逻辑 --------

        private async Task LoadAsync()
        {
            // 取消上一次未完成的查询
            _loadCts?.Cancel();
            _loadCts = new CancellationTokenSource();
            var ct = _loadCts.Token;

            var appliedToUi = false;
            var loadFaulted = false;

            try
            {
                IsLoading = true;
                await RunOnUiAsync(UpdateStatusText);

                using var ds = new DataService();
                await ds.InitializeAsync();
                ct.ThrowIfCancellationRequested();

                List<OperationLog> opList;
                List<TemperatureLog> tempList;

                if (SelectedDevice?.DeviceId is int devId)
                {
                    opList = await ds.GetOperationLogsByDevicePagedAsync(devId, FilterStartDate, FilterEndDate, 0, MaxOperationRows);
                    tempList = await ds.GetTemperatureLogsByDevicePagedAsync(devId, FilterStartDate, FilterEndDate, 0, MaxTemperatureRows);
                }
                else
                {
                    // 全部设备：把每台设备查询并合并（不在 DataService 里多写一个方法以减少改动面）
                    opList = new List<OperationLog>();
                    tempList = new List<TemperatureLog>();
                    var devices = _deviceManager.Devices.ToList();
                    var opLimitPerDevice = Math.Max(1, MaxOperationRows / Math.Max(1, devices.Count));
                    var tempLimitPerDevice = Math.Max(1, MaxTemperatureRows / Math.Max(1, devices.Count));

                    foreach (var d in devices)
                    {
                        ct.ThrowIfCancellationRequested();
                        opList.AddRange(
                            await ds.GetOperationLogsByDevicePagedAsync(d.Id, FilterStartDate, FilterEndDate, 0, opLimitPerDevice));
                        tempList.AddRange(
                            await ds.GetTemperatureLogsByDevicePagedAsync(d.Id, FilterStartDate, FilterEndDate, 0, tempLimitPerDevice));
                    }

                    // 合并后按时间倒/正序排列
                    opList = opList.OrderByDescending(l => l.LogTime).ToList();
                    tempList = tempList.OrderBy(l => l.RecordTime).ToList();
                }

                ct.ThrowIfCancellationRequested();

                // 写回 ObservableCollection 必须在 UI 线程
                await RunOnUiAsync(() =>
                {
                    OperationLogs.Clear();
                    foreach (var o in opList) OperationLogs.Add(o);

                    TemperatureLogs.Clear();
                    foreach (var t in tempList) TemperatureLogs.Add(t);

                    OperationLogCount = OperationLogs.Count;
                    TemperatureLogCount = TemperatureLogs.Count;

                    OperationLogsView.Refresh();
                    TemperatureLogsView.Refresh();

                    appliedToUi = true;
                    IsLoading = false;
                    UpdateStatusText();
                });
            }
            catch (OperationCanceledException)
            {
                // 用户切换太快，正常忽略
            }
            catch (Exception ex)
            {
                loadFaulted = true;
                System.Diagnostics.Debug.WriteLine($"[LogQuery] 查询失败: {ex.Message}");
                await RunOnUiAsync(() =>
                {
                    StatusText = $"查询失败: {ex.Message}";
                    ShowOperationEmptyState = false;
                    ShowTemperatureEmptyState = false;
                });
            }
            finally
            {
                await RunOnUiAsync(() =>
                {
                    IsLoading = false;
                    if (!appliedToUi && !loadFaulted)
                        UpdateStatusText();
                });
            }
        }

        private static Task RunOnUiAsync(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }

            return dispatcher.InvokeAsync(action).Task;
        }

        private void UpdateStatusText()
        {
            int opShown = OperationLogsView.Cast<object>().Count();
            int tempShown = TemperatureLogsView.Cast<object>().Count();
            OperationFilteredCount = opShown;
            TemperatureFilteredCount = tempShown;

            if (IsLoading)
            {
                ShowOperationEmptyState = false;
                ShowTemperatureEmptyState = false;
                StatusText = "查询中…";
                return;
            }

            // 命中单次加载上限时明确提示，否则用户会误以为看到的就是全部记录
            var truncated = OperationLogs.Count >= MaxOperationRows || TemperatureLogs.Count >= MaxTemperatureRows;
            StatusText =
                $"操作日志 {opShown}/{OperationLogs.Count} 条 · 温度日志 {tempShown}/{TemperatureLogs.Count} 条" +
                (truncated ? $"（已达单次加载上限 {MaxOperationRows} 条，可缩小时间范围查看更早记录）" : "") +
                (string.IsNullOrEmpty(SearchText) && SelectedLogType == "全部" ? "" : "（已筛选）");

            ShowOperationEmptyState = opShown == 0;
            OperationEmptyMessage = OperationLogs.Count == 0
                ? "所选时间范围内暂无操作日志。"
                : "无匹配结果，可尝试清空搜索或调整「类型」筛选。";

            ShowTemperatureEmptyState = tempShown == 0;
            TemperatureEmptyMessage = TemperatureLogs.Count == 0
                ? "所选时间范围内暂无温度记录。"
                : "无匹配结果，可尝试清空搜索框。";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _loadCts?.Cancel(); _loadCts?.Dispose(); } catch { }
        }
    }
}
