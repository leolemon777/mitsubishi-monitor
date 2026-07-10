using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveCharts;
using LiveCharts.Wpf;
using MitsubishiMonitor.Demo.Models;
using MitsubishiMonitor.Demo.Services;

namespace MitsubishiMonitor.Demo.ViewModels
{
    /// <summary>
    /// 设备详情页ViewModel
    /// </summary>
    public partial class DeviceDetailViewModel : ObservableObject, IDisposable
    {
        private readonly ExcelExportService _excelService;
        private readonly DeviceManagerService _deviceManager;
        private readonly IPlcService _plcService;
        private readonly DispatcherTimer _plcUpdateTimer;
        private bool _isDisposed = false;
        private readonly ChartValues<float> _phaseAValues = new();
        private readonly ChartValues<float> _phaseBValues = new();
        private readonly ChartValues<float> _phaseCValues = new();
        private readonly ChartValues<float> _temperatureValuesForVoltageChart = new();

        private readonly Queue<float> _diagnosisTempHistory = new();
        private readonly Queue<float> _diagnosisVoltageHistory = new();
        private readonly Queue<DateTime> _diagnosisSampleTimes = new();
        // 温度真实采样默认每 10 秒一次，保留 60 个点约等于最近 10 分钟。
        private const int VoltageHistoryLimit = 60;
        // 6 个真实温度样本约覆盖 50–60 秒，不再用 1 秒 UI Tick 重复填充相同值。
        private const int DiagnosisWindowSamples = 6;
        private const int PredictionHorizonMinutes = 10;
        private DateTime _lastChartSampleTime;
        private const int DetailQueryLimit = 5000;
        private int _pendingOperationDelta;
        private int _loadVersion;

        [ObservableProperty]
        private Device _currentDevice;

        [ObservableProperty]
        private DateTime _filterStartDate = DateTime.Today;

        [ObservableProperty]
        private DateTime _filterEndDate = DateTime.Now;

        [ObservableProperty]
        private string _selectedTimeRange = "今日";

        [ObservableProperty]
        private int _totalOperationCount;

        [ObservableProperty]
        private int _abnormalCount;

        [ObservableProperty]
        private float _avgTemperature;

        [ObservableProperty]
        private float _maxTemperature;

        [ObservableProperty]
        private float _minTemperature;

        [ObservableProperty]
        private string _phaseAVoltage = "--.- V";

        [ObservableProperty]
        private string _phaseBVoltage = "--.- V";

        [ObservableProperty]
        private string _phaseCVoltage = "--.- V";

        [ObservableProperty]
        private float _temperatureThreshold = 50f;

        [ObservableProperty]
        private bool _isThresholdEditing;

        [ObservableProperty]
        private bool _isAlarm;

        [ObservableProperty]
        private bool _isSsrFault;

        [ObservableProperty]
        private bool _isAlarmAcknowledged;

        [ObservableProperty]
        private string _targetTemperatureDisplay = "--.-°C";

        [ObservableProperty]
        private string _heatingDiagnosis = "加热诊断：数据采集中...";

        [ObservableProperty]
        private string _predictedTemperatureDisplay = "--.- °C";

        [ObservableProperty]
        private PlcStatus _plcStatus;

        [ObservableProperty]
        private PlcConfig _plcConfig;

        /// <summary>
        /// 是否有电压数据（用于界面条件显示）
        /// </summary>
        public bool HasVoltage => PlcConfig?.HasVoltage ?? true;

        /// <summary>
        /// 是否有 C 寄存器数据
        /// </summary>
        public bool HasCRegisters => PlcConfig?.HasCRegisters ?? false;

        /// <summary>
        /// C 寄存器显示项目列表（标签 + 值 + 单位）
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<CRegisterDisplayItem> _cRegisterItems = new();

        /// <summary>
        /// 工艺阶段状态列表（M110-M180 工艺流程指示）
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<ProcessStageItem> _processStageItems = new();

        /// <summary>
        /// 当前活跃工艺阶段描述
        /// </summary>
        [ObservableProperty]
        private string _currentProcessStage = "待机";

        public ObservableCollection<OperationLog> OperationLogs { get; } = new();
        public ObservableCollection<TemperatureLog> TemperatureLogs { get; } = new();

        /// <summary>
        /// 合并图表：温度 + A/B/C 三相电压（双Y轴）
        /// </summary>
        [ObservableProperty]
        private SeriesCollection _combinedSeries;

        [ObservableProperty]
        private string[] _timeLabels = Array.Empty<string>();

        /// <summary>
        /// 温度数据（直接绑定到图表）
        /// </summary>
        public ChartValues<float> TemperatureValues => _temperatureValuesForVoltageChart;

        /// <summary>
        /// A相电压数据
        /// </summary>
        public ChartValues<float> PhaseAValues => _phaseAValues;

        /// <summary>
        /// B相电压数据
        /// </summary>
        public ChartValues<float> PhaseBValues => _phaseBValues;

        /// <summary>
        /// C相电压数据
        /// </summary>
        public ChartValues<float> PhaseCValues => _phaseCValues;

        public DeviceDetailViewModel(Device device, DeviceManagerService deviceManager)
        {
            _currentDevice = device;
            _deviceManager = deviceManager;
            _excelService = new ExcelExportService();
            _plcService = deviceManager.GetPlcService(device.Id);

            // 从PLC配置加载温度阈值
            if (_plcService?.Config != null)
            {
                TemperatureThreshold = _plcService.Config.TemperatureThreshold;
            }

            // 初始化 PlcStatus 和 PlcConfig 用于点位面板绑定
            if (_plcService != null)
            {
                PlcStatus = _plcService.CurrentStatus;
                PlcConfig = _plcService.Config;
            }

            // 订阅IO点变化，实时更新日志列表
            if (_plcService != null)
            {
                _plcService.StateChanged += OnPlcStateChanged;
            }

            _plcUpdateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _plcUpdateTimer.Tick += (s, e) => UpdatePhaseVoltages();
            _plcUpdateTimer.Start();
            UpdatePhaseVoltages();

            // 初始化寄存器显示（先用 0 值占位，连接后实时更新）
            if (HasCRegisters)
            {
                var items = new ObservableCollection<CRegisterDisplayItem>();
                foreach (var reg in PlcConfig.CRegisters)
                {
                    items.Add(new CRegisterDisplayItem
                    {
                        Label = reg.Label,
                        Value = 0,
                        Unit = reg.Unit,
                        Address = reg.Address
                    });
                }
                CRegisterItems = items;
            }

            // 初始化工艺流程显示（先全部待机，连接后实时更新）
            if (PlcConfig?.HasProcessStages == true)
            {
                var stageItems = new ObservableCollection<ProcessStageItem>();
                foreach (var stage in PlcConfig.ProcessStages)
                {
                    stageItems.Add(new ProcessStageItem
                    {
                        StageName = $"{stage.Icon} {stage.Name}",
                        Address = stage.Address,
                        IsActive = false
                    });
                }
                ProcessStageItems = stageItems;
                CurrentProcessStage = "待机";
            }

            // 初始化图表
            InitializeCharts();

            // 先显示空状态，再从数据库加载真实历史；生产监控界面不能生成随机温度数据。
            InitializeDisplayData();
            _ = LoadDataAsync();
        }

        private void InitializeCharts()
        {
            var series = new SeriesCollection
            {
                new LineSeries
                {
                    Title = "温度 (°C)",
                    Values = _temperatureValuesForVoltageChart,
                    PointGeometry = null,
                    Stroke = new SolidColorBrush(Color.FromRgb(240, 136, 62)),
                    StrokeThickness = 2.5,
                    Fill = System.Windows.Media.Brushes.Transparent,
                    ScalesYAt = 0
                }
            };

            // 仅在有电压数据时添加电压曲线
            if (HasVoltage)
            {
                series.Add(new LineSeries
                {
                    Title = "A相电压",
                    Values = _phaseAValues,
                    PointGeometry = null,
                    Stroke = new SolidColorBrush(Color.FromRgb(245, 183, 59)),
                    StrokeThickness = 1.5,
                    Fill = System.Windows.Media.Brushes.Transparent,
                    ScalesYAt = 1
                });
                series.Add(new LineSeries
                {
                    Title = "B相电压",
                    Values = _phaseBValues,
                    PointGeometry = null,
                    Stroke = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                    StrokeThickness = 1.5,
                    Fill = System.Windows.Media.Brushes.Transparent,
                    ScalesYAt = 1
                });
                series.Add(new LineSeries
                {
                    Title = "C相电压",
                    Values = _phaseCValues,
                    PointGeometry = null,
                    Stroke = new SolidColorBrush(Color.FromRgb(88, 166, 255)),
                    StrokeThickness = 1.5,
                    Fill = System.Windows.Media.Brushes.Transparent,
                    ScalesYAt = 1
                });
            }

            CombinedSeries = series;
            TimeLabels = Array.Empty<string>();
        }

        private void InitializeDisplayData()
        {
            try
            {
                TotalOperationCount = CurrentDevice.TodayOperationCount;
                AbnormalCount = CurrentDevice.HasAlert ? 1 : 0;
                if (CurrentDevice.HasTemperatureSample)
                {
                    AvgTemperature = CurrentDevice.CurrentTemperature;
                    MaxTemperature = CurrentDevice.CurrentTemperature;
                    MinTemperature = CurrentDevice.CurrentTemperature;
                }
                else
                {
                    AvgTemperature = 0;
                    MaxTemperature = 0;
                    MinTemperature = 0;
                }

                _temperatureValuesForVoltageChart.Clear();
                TimeLabels = Array.Empty<string>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"初始化详情显示失败: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task ChangeTimeRangeAsync(string range)
        {
            SelectedTimeRange = range;
            var now = DateTime.Now;

            switch (range)
            {
                case "今日":
                    FilterStartDate = now.Date;
                    FilterEndDate = now;
                    break;
                case "本周":
                    var dayOfWeek = (int)now.DayOfWeek;
                    FilterStartDate = now.Date.AddDays(-dayOfWeek);
                    FilterEndDate = now;
                    break;
                case "本月":
                    FilterStartDate = new DateTime(now.Year, now.Month, 1);
                    FilterEndDate = now;
                    break;
                case "全部":
                    FilterStartDate = DateTime.MinValue.AddDays(1);
                    FilterEndDate = now;
                    break;
            }

            await LoadDataAsync();
        }

        [RelayCommand]
        private async Task RefreshAsync()
        {
            await LoadDataAsync();
        }

        [RelayCommand]
        private async Task ExportToExcelAsync()
        {
            string filePath;
            try
            {
                // 先加载数据
                await LoadDataAsync();

                // 获取当前显示的数据（直接查库，不依赖 UI 集合）
                var tempLogs = await GetTemperatureLogsAsync();
                var opLogs = await GetOperationLogsAsync();

                if (!tempLogs.Any() && !opLogs.Any())
                {
                    var result = MessageBox.Show(
                        $"数据库中暂无 [{CurrentDevice.Name}] 的数据可导出。\n\n" +
                        "是否要导出空模板？",
                        "暂无数据",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        filePath = await _excelService.ExportDeviceReadablePackageAsync(CurrentDevice, new List<TemperatureLog>(), new List<OperationLog>());
                        MessageBox.Show($"空模板导出成功!\n\n工控机可直接打开:\n{filePath}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    return;
                }

                filePath = await _excelService.ExportDeviceReadablePackageAsync(CurrentDevice, tempLogs, opLogs);

                MessageBox.Show(
                    $"导出成功!\n\n设备: {CurrentDevice.Name}\n温度记录: {tempLogs.Count} 条\n操作日志: {opLogs.Count} 条\n\n工控机可直接打开:\n{filePath}",
                    "导出成功",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败: {ex.Message}\n\n详细错误:\n{ex}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void SaveTemperatureThreshold()
        {
            try
            {
                if (TemperatureThreshold < 0 || TemperatureThreshold > 200)
                {
                    MessageBox.Show("温度阈值必须在 0-200°C 之间", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 更新PLC配置（内存）
                if (_plcService?.Config != null)
                {
                    _plcService.Config.TemperatureThreshold = TemperatureThreshold;
                }

                // 持久化到 config.json（deviceIndex = CurrentDevice.Id - 1）
                if (CurrentDevice != null)
                {
                    AppConfig.SaveDeviceThreshold(CurrentDevice.Id - 1, TemperatureThreshold);
                }

                // 同步更新 PlcStatus.IsAlarm，避免 UI 状态依赖 10s 温度采集线程
                if (_plcService?.CurrentStatus != null)
                {
                    float currentTemp = _plcService.CurrentStatus.Temperature;
                    _plcService.CurrentStatus.IsAlarm = currentTemp > TemperatureThreshold;
                }

                // 强制刷新三色灯，重置状态缓存避免被防重入跳过（丢到后台线程执行，不卡 UI）
                _ = Task.Run(() => _deviceManager.ForceUpdateTowerLight());

                IsThresholdEditing = false;

                MessageBox.Show(
                    $"温度报警阈值已更新为 {TemperatureThreshold}°C\n\n" +
                    "当温度超过此阈值时，系统将自动标记为异常并记录。",
                    "设置成功",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void CancelThresholdEdit()
        {
            // 恢复原值
            if (_plcService?.Config != null)
            {
                TemperatureThreshold = _plcService.Config.TemperatureThreshold;
            }
            IsThresholdEditing = false;
        }

        [RelayCommand]
        private void StartThresholdEdit()
        {
            IsThresholdEditing = true;
        }

        [RelayCommand]
        private void AcknowledgeAlarm()
        {
            IsAlarmAcknowledged = true;
            IsAlarm = false;
            System.Diagnostics.Debug.WriteLine("[报警] 已手动复位报警");
        }

        private async Task<List<TemperatureLog>> GetTemperatureLogsAsync()
        {
            try
            {
                using var dataService = new DataService();
                await dataService.InitializeAsync();
                return await dataService.GetTemperatureLogsByDeviceAsync(CurrentDevice.Id, FilterStartDate, FilterEndDate);
            }
            catch
            {
                return new List<TemperatureLog>();
            }
        }

        private async Task<List<OperationLog>> GetOperationLogsAsync()
        {
            try
            {
                using var dataService = new DataService();
                await dataService.InitializeAsync();
                return await dataService.GetOperationLogsByDeviceAsync(CurrentDevice.Id, FilterStartDate, FilterEndDate);
            }
            catch
            {
                return new List<OperationLog>();
            }
        }

        private async Task LoadDataAsync()
        {
            var loadVersion = Interlocked.Increment(ref _loadVersion);
            try
            {
                System.Diagnostics.Debug.WriteLine("[DeviceDetailViewModel] LoadDataAsync 开始执行");

                var cache = CacheService.Instance;
                var opLogsKey = CacheService.GetOperationLogsKey(CurrentDevice.Id, FilterStartDate, FilterEndDate);
                var tempLogsKey = CacheService.GetTemperatureLogsKey(CurrentDevice.Id, FilterStartDate, FilterEndDate);

                using var dataService = new DataService();
                await dataService.InitializeAsync();

                var logs = await cache.GetOrLoadAsync(opLogsKey, async () =>
                    await dataService.GetOperationLogsByDevicePagedAsync(CurrentDevice.Id, FilterStartDate, FilterEndDate, 0, DetailQueryLimit),
                    TimeSpan.FromMinutes(2));
                var totalOperationCount = await dataService.GetOperationLogCountByDeviceAsync(
                    CurrentDevice.Id, FilterStartDate, FilterEndDate);

                System.Diagnostics.Debug.WriteLine($"[DeviceDetailViewModel] 查询到操作日志 {logs?.Count ?? 0} 条");

                // 详情页不再展示日志列表（已迁移到独立的"日志查询"页），这里只更新计数
                var tempLogs = await cache.GetOrLoadAsync(tempLogsKey, async () =>
                    await dataService.GetTemperatureLogsByDevicePagedAsync(CurrentDevice.Id, FilterStartDate, FilterEndDate, 0, DetailQueryLimit),
                    TimeSpan.FromMinutes(2));

                System.Diagnostics.Debug.WriteLine($"[DeviceDetailViewModel] 查询到温度日志 {tempLogs?.Count ?? 0} 条");

                if (_isDisposed || loadVersion != Volatile.Read(ref _loadVersion))
                    return;

                TotalOperationCount = totalOperationCount;

                if (tempLogs != null && tempLogs.Any())
                {
                    AvgTemperature = (float)tempLogs.Average(l => l.Temperature);
                    MaxTemperature = tempLogs.Max(l => l.Temperature);
                    MinTemperature = tempLogs.Min(l => l.Temperature);
                    AbnormalCount = tempLogs.Count(l => l.IsAbnormal);

                    var tempValues = new ChartValues<float>();
                    var labels = new List<string>();

                    foreach (var log in tempLogs.Take(50))
                    {
                        tempValues.Add(log.Temperature);
                        labels.Add(log.RecordTime.ToString("HH:mm"));
                    }

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (_isDisposed || loadVersion != Volatile.Read(ref _loadVersion))
                            return;

                        _temperatureValuesForVoltageChart.Clear();
                        _temperatureValuesForVoltageChart.AddRange(tempValues);

                        TimeLabels = labels.ToArray();
                        System.Diagnostics.Debug.WriteLine($"[DeviceDetailViewModel] 温度数据已更新, Count={tempValues.Count}, Labels={labels.Count}");
                    }, DispatcherPriority.Background);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载数据失败: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void UpdatePhaseVoltages()
        {
            try
            {
            if (_plcService == null)
                return;

            var status = _plcService.CurrentStatus;

            // ── 快速路径（每 1s 执行）：轻量状态刷新，不触发图表重绘 ──
            var opDelta = Interlocked.Exchange(ref _pendingOperationDelta, 0);
            if (opDelta > 0)
                TotalOperationCount += opDelta;

            // 目标温度显示
            if (status.TargetTemperature > 0)
            {
                TargetTemperatureDisplay = $"{status.TargetTemperature:F1}°C";
            }

            // 报警状态更新
            if (!IsAlarmAcknowledged)
            {
                IsAlarm = status.IsAlarm;
                IsSsrFault = status.IsSsrFault;
            }
            else
            {
                if (!status.IsAlarm)
                {
                    IsAlarmAcknowledged = false;
                }
                IsAlarm = status.IsAlarm;
                IsSsrFault = status.IsSsrFault;
            }

            // 电压文本（轻量 string 更新，不触发图表）
            if (HasVoltage)
            {
                PhaseAVoltage = $"{status.ThermocoupleA:F3} V";
                PhaseBVoltage = $"{status.ThermocoupleB:F3} V";
                PhaseCVoltage = $"{status.ThermocoupleC:F3} V";
            }

            // 更新 C 寄存器显示（原地更新，不重建集合）
            if (HasCRegisters && status.CValues != null && status.CValues.Count > 0)
            {
                UpdateCRegisterDisplay(status);
            }

            // 更新工艺阶段状态
            if (PlcConfig?.MPointLabels != null)
            {
                UpdateProcessStages(status);
            }

            // ── 慢速路径：只在 PLC 真正完成一轮新温度采样时更新图表 ──
            var sampleTime = status.LastTemperatureSampleTime;
            if (sampleTime == default || sampleTime == _lastChartSampleTime)
                return;
            _lastChartSampleTime = sampleTime;

            var currentTemp = status.Temperature;

            // 温度曲线写入
            _temperatureValuesForVoltageChart.Add(currentTemp);
            if (_temperatureValuesForVoltageChart.Count > VoltageHistoryLimit)
                _temperatureValuesForVoltageChart.RemoveAt(0);

            // 电压曲线写入 + 诊断计算
            if (HasVoltage)
            {
                _phaseAValues.Add(status.ThermocoupleA);
                _phaseBValues.Add(status.ThermocoupleB);
                _phaseCValues.Add(status.ThermocoupleC);

                if (_phaseAValues.Count > VoltageHistoryLimit) _phaseAValues.RemoveAt(0);
                if (_phaseBValues.Count > VoltageHistoryLimit) _phaseBValues.RemoveAt(0);
                if (_phaseCValues.Count > VoltageHistoryLimit) _phaseCValues.RemoveAt(0);

                // 加热效率诊断
                var avgVoltageNow = (status.ThermocoupleA + status.ThermocoupleB + status.ThermocoupleC) / 3f;
                _diagnosisTempHistory.Enqueue(currentTemp);
                _diagnosisVoltageHistory.Enqueue(avgVoltageNow);
                _diagnosisSampleTimes.Enqueue(sampleTime);

                if (_diagnosisTempHistory.Count > DiagnosisWindowSamples) _diagnosisTempHistory.Dequeue();
                if (_diagnosisVoltageHistory.Count > DiagnosisWindowSamples) _diagnosisVoltageHistory.Dequeue();
                if (_diagnosisSampleTimes.Count > DiagnosisWindowSamples) _diagnosisSampleTimes.Dequeue();

                if (_diagnosisTempHistory.Count < DiagnosisWindowSamples)
                {
                    HeatingDiagnosis = "加热诊断：数据采集中...";
                    PredictedTemperatureDisplay = "--.- °C";
                    return;
                }

                var oldestTemp = _diagnosisTempHistory.Peek();
                var deltaTemp = currentTemp - oldestTemp;
                var avgVoltageWindow = _diagnosisVoltageHistory.Average();
                var actualWindowSeconds = Math.Max(1, (sampleTime - _diagnosisSampleTimes.Peek()).TotalSeconds);

                if (avgVoltageWindow < 0.01f)
                {
                    HeatingDiagnosis = "加热诊断：最近 1 分钟基本未加热。";
                    PredictedTemperatureDisplay = $"{currentTemp:F1} °C";
                    return;
                }

                if (deltaTemp < 0.2f)
                    HeatingDiagnosis = "加热诊断：电压较高但温度几乎不变，建议检查加热棒、液位或温度探头。";
                else if (deltaTemp > 2f)
                    HeatingDiagnosis = "加热诊断：升温较快，加热效率良好。";
                else
                    HeatingDiagnosis = "加热诊断：升温正常。";

                // predictionFactor = 10min 预测窗口 ÷ 实际采样窗口时长
                var predictionFactor = (PredictionHorizonMinutes * 60f) / (float)actualWindowSeconds;
                PredictedTemperatureDisplay = $"{currentTemp + deltaTemp * predictionFactor:F1} °C";
            }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UpdatePhaseVoltages] 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新 C 寄存器显示列表（原地更新属性值，不重建集合）
        /// </summary>
        private void UpdateCRegisterDisplay(PlcStatus status)
        {
            if (PlcConfig?.CRegisters == null) return;

            // 直接更新已存在集合中的项 Value，INPC 会自动通知 UI。
            // 避免每秒重建整个 ObservableCollection 导致界面闪烁和 GC 压力。
            foreach (var item in CRegisterItems)
            {
                if (status.CValues.TryGetValue(item.Address, out var v))
                    item.Value = v;
            }
        }

        /// <summary>
        /// 根据 M 点状态更新工艺阶段显示（原地更新 IsActive，不重建集合）
        /// </summary>
        private void UpdateProcessStages(PlcStatus status)
        {
            if (!PlcConfig.HasProcessStages) return;

            string activeStageName = "待机";

            // 直接遍历已存在的项，按地址查找 M 点状态并原地更新 IsActive
            foreach (var item in ProcessStageItems)
            {
                bool isActive = false;

                if (PlcConfig.MAddressList != null)
                {
                    int idx = PlcConfig.MAddressList.IndexOf(item.Address);
                    isActive = idx >= 0 && idx < status.M.Length && status.M[idx];
                }
                else
                {
                    for (int i = 0; i < PlcConfig.ActualMCount; i++)
                    {
                        if (PlcConfig.GetMAddress(i) == item.Address && i < status.M.Length)
                        {
                            isActive = status.M[i];
                            break;
                        }
                    }
                }

                item.IsActive = isActive; // INPC 自动通知 UI，无需替换集合

                if (isActive)
                    activeStageName = item.StageName;
            }

            CurrentProcessStage = activeStageName;
        }

        /// <summary>
        /// PLC IO 点变化：详情页只增加"今日操作次数"计数显示。
        /// 真实日志已经由 DeviceManagerService 异步写入数据库，
        /// 用户可通过主界面"日志查询"按钮在独立页面查看/导出。
        /// </summary>
        private void OnPlcStateChanged(object sender, StateChangeEvent evt)
        {
            if (_isDisposed) return;
            Interlocked.Increment(ref _pendingOperationDelta);
        }

        /// <summary>
        /// 释放资源，取消订阅事件
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            Interlocked.Increment(ref _loadVersion);

            // 停止定时器（Stop 后不再触发 Tick，无需额外 -= 匿名委托——匿名 lambda 无法匹配取消订阅）
            _plcUpdateTimer?.Stop();

            // 取消订阅 PLC 事件
            if (_plcService != null)
            {
                _plcService.StateChanged -= OnPlcStateChanged;
            }

            System.Diagnostics.Debug.WriteLine($"[DeviceDetailViewModel] 资源已释放");
        }
    }
}
