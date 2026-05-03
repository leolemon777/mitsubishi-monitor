using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
        private const int VoltageHistoryLimit = 60;
        private readonly ChartValues<float> _phaseAValues = new();
        private readonly ChartValues<float> _phaseBValues = new();
        private readonly ChartValues<float> _phaseCValues = new();
        private readonly ChartValues<float> _temperatureValuesForVoltageChart = new();

        private readonly Queue<float> _diagnosisTempHistory = new();
        private readonly Queue<float> _diagnosisVoltageHistory = new();
        private const int DiagnosisWindowSeconds = 60;
        private const int PredictionHorizonMinutes = 10;

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

            // 加载历史数据（mock 初始值 + 从数据库拉取真实日志）
            LoadMockData();
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

        private void LoadMockData()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[DeviceDetailViewModel] LoadMockData 开始执行");

                // 统计数据初始值
                TotalOperationCount = CurrentDevice.TodayOperationCount;
                AbnormalCount = CurrentDevice.HasAlert ? 1 : 0;
                AvgTemperature = CurrentDevice.CurrentTemperature;
                MaxTemperature = CurrentDevice.CurrentTemperature + 5.0f;
                MinTemperature = CurrentDevice.CurrentTemperature - 5.0f;

                // 生成模拟的温度曲线数据
                var random = new Random();
                var tempValues = new ChartValues<float>();
                var labels = new string[20];

                for (int i = 0; i < 20; i++)
                {
                    var baseTemp = CurrentDevice.CurrentTemperature;
                    tempValues.Add(baseTemp + (float)(random.NextDouble() * 10 - 5));
                    labels[i] = DateTime.Now.AddMinutes(-20 + i).ToString("HH:mm");
                }

                // 温度模拟数据写入 CombinedSeries 的第一条线（温度）
                if (CombinedSeries != null && CombinedSeries.Count > 0)
                {
                    _temperatureValuesForVoltageChart.Clear();
                    foreach (var v in tempValues) _temperatureValuesForVoltageChart.Add(v);
                    System.Diagnostics.Debug.WriteLine($"[DeviceDetailViewModel] 温度数据已写入图表, Count={tempValues.Count}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[DeviceDetailViewModel] CombinedSeries 为空或 Count=0");
                }

                TimeLabels = labels;
                System.Diagnostics.Debug.WriteLine($"[DeviceDetailViewModel] TimeLabels 设置完成, Length={labels.Length}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载模拟数据失败: {ex.Message}");
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
                        filePath = await _excelService.ExportDeviceDataAsync(CurrentDevice, new List<TemperatureLog>(), new List<OperationLog>());
                        MessageBox.Show($"空模板导出成功!\n\n文件位置:\n{filePath}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    return;
                }

                filePath = await _excelService.ExportDeviceDataAsync(CurrentDevice, tempLogs, opLogs);

                MessageBox.Show(
                    $"导出成功!\n\n设备: {CurrentDevice.Name}\n温度记录: {tempLogs.Count} 条\n操作日志: {opLogs.Count} 条\n\n文件位置:\n{filePath}",
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

                // 更新PLC配置
                if (_plcService?.Config != null)
                {
                    _plcService.Config.TemperatureThreshold = TemperatureThreshold;
                }

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
            try
            {
                System.Diagnostics.Debug.WriteLine("[DeviceDetailViewModel] LoadDataAsync 开始执行");

                var cache = CacheService.Instance;
                var opLogsKey = CacheService.GetOperationLogsKey(CurrentDevice.Id, FilterStartDate, FilterEndDate);
                var tempLogsKey = CacheService.GetTemperatureLogsKey(CurrentDevice.Id, FilterStartDate, FilterEndDate);

                using var dataService = new DataService();
                await dataService.InitializeAsync();

                var logs = await cache.GetOrLoadAsync(opLogsKey, async () =>
                    await dataService.GetOperationLogsByDeviceAsync(CurrentDevice.Id, FilterStartDate, FilterEndDate),
                    TimeSpan.FromMinutes(2));

                System.Diagnostics.Debug.WriteLine($"[DeviceDetailViewModel] 查询到操作日志 {logs?.Count ?? 0} 条");

                // 详情页不再展示日志列表（已迁移到独立的"日志查询"页），这里只更新计数
                if (logs != null)
                {
                    TotalOperationCount = logs.Count;
                }

                var tempLogs = await cache.GetOrLoadAsync(tempLogsKey, async () =>
                    await dataService.GetTemperatureLogsByDeviceAsync(CurrentDevice.Id, FilterStartDate, FilterEndDate),
                    TimeSpan.FromMinutes(2));

                System.Diagnostics.Debug.WriteLine($"[DeviceDetailViewModel] 查询到温度日志 {tempLogs?.Count ?? 0} 条");

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

                    Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        _temperatureValuesForVoltageChart.Clear();
                        foreach (var v in tempValues) _temperatureValuesForVoltageChart.Add(v);

                        TimeLabels = labels.ToArray();
                        System.Diagnostics.Debug.WriteLine($"[DeviceDetailViewModel] 温度数据已更新, Count={tempValues.Count}, Labels={labels.Count}");
                    });
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

            // 温度曲线始终更新
            var currentTemp = status.Temperature;
            _temperatureValuesForVoltageChart.Add(currentTemp);
            if (_temperatureValuesForVoltageChart.Count > VoltageHistoryLimit)
                _temperatureValuesForVoltageChart.RemoveAt(0);

            // 仅在有电压数据时更新电压显示和诊断
            if (HasVoltage)
            {
                PhaseAVoltage = $"{status.ThermocoupleA:F3} V";
                PhaseBVoltage = $"{status.ThermocoupleB:F3} V";
                PhaseCVoltage = $"{status.ThermocoupleC:F3} V";

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

                if (_diagnosisTempHistory.Count > DiagnosisWindowSeconds) _diagnosisTempHistory.Dequeue();
                if (_diagnosisVoltageHistory.Count > DiagnosisWindowSeconds) _diagnosisVoltageHistory.Dequeue();

                if (_diagnosisTempHistory.Count < DiagnosisWindowSeconds)
                {
                    HeatingDiagnosis = "加热诊断：数据采集中...";
                    PredictedTemperatureDisplay = "--.- °C";
                    return;
                }

                var oldestTemp = _diagnosisTempHistory.Peek();
                var deltaTemp = currentTemp - oldestTemp;
                var avgVoltageWindow = _diagnosisVoltageHistory.Average();

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

                var predictionFactor = (PredictionHorizonMinutes * 60f) / DiagnosisWindowSeconds;
                PredictedTemperatureDisplay = $"{currentTemp + deltaTemp * predictionFactor:F1} °C";
            }

            // 更新 C 寄存器显示
            if (HasCRegisters && status.CValues != null && status.CValues.Count > 0)
            {
                UpdateCRegisterDisplay(status);
            }

            // 更新工艺阶段状态（所有有工艺定义或M点标签的设备）
            if (PlcConfig?.MPointLabels != null)
            {
                UpdateProcessStages(status);
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
            try
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    TotalOperationCount++;
                });
            }
            catch { }
        }

        /// <summary>
        /// 释放资源，取消订阅事件
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            // 停止并释放定时器
            if (_plcUpdateTimer != null)
            {
                _plcUpdateTimer.Stop();
                _plcUpdateTimer.Tick -= (s, e) => UpdatePhaseVoltages();
            }

            // 取消订阅 PLC 事件
            if (_plcService != null)
            {
                _plcService.StateChanged -= OnPlcStateChanged;
            }

            System.Diagnostics.Debug.WriteLine($"[DeviceDetailViewModel] 资源已释放");
        }
    }
}
