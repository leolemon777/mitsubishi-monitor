using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using MitsubishiMonitor.Demo.Models;
using MitsubishiMonitor.Demo.Services;
using SkiaSharp;

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
        private readonly TemperatureChartWindow _chart = new();
        private ObservableCollection<double?> _phaseAValues => _chart.PhaseA;
        private ObservableCollection<double?> _phaseBValues => _chart.PhaseB;
        private ObservableCollection<double?> _phaseCValues => _chart.PhaseC;
        private ObservableCollection<double?> _temperatureValuesForVoltageChart => _chart.Temperatures;

        private readonly Queue<float> _diagnosisTempHistory = new();
        private readonly Queue<float> _diagnosisVoltageHistory = new();
        private readonly Queue<DateTime> _diagnosisSampleTimes = new();
        // 温度真实采样默认每 10 秒一次，保留 60 个点约等于最近 10 分钟。
        // 6 个真实温度样本约覆盖 50–60 秒，不再用 1 秒 UI Tick 重复填充相同值。
        private const int DiagnosisWindowSamples = 6;
        private const int PredictionHorizonMinutes = 10;
        private DateTime _lastChartSampleTime;
        private long _diagnosisGeneration;
        private int _pendingOperationDelta;
        private int _loadVersion;
        private CancellationTokenSource _loadCts;
        private CancellationTokenSource _exportCts;

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
        private ISeries[] _combinedSeries = Array.Empty<ISeries>();

        [ObservableProperty]
        private string[] _timeLabels = Array.Empty<string>();

        [ObservableProperty]
        private Axis[] _xAxes = Array.Empty<Axis>();

        [ObservableProperty]
        private Axis[] _combinedYAxes = Array.Empty<Axis>();

        [ObservableProperty]
        private Axis[] _temperatureYAxes = Array.Empty<Axis>();

        /// <summary>
        /// 温度数据（直接绑定到图表）
        /// </summary>
        public ObservableCollection<double?> TemperatureValues => _temperatureValuesForVoltageChart;

        /// <summary>
        /// A相电压数据
        /// </summary>
        public ObservableCollection<double?> PhaseAValues => _phaseAValues;

        /// <summary>
        /// B相电压数据
        /// </summary>
        public ObservableCollection<double?> PhaseBValues => _phaseBValues;

        /// <summary>
        /// C相电压数据
        /// </summary>
        public ObservableCollection<double?> PhaseCValues => _phaseCValues;

        public DeviceDetailViewModel(Device device, DeviceManagerService deviceManager)
            : this(device, deviceManager, loadHistory: true, startTimer: true)
        {
        }

        internal DeviceDetailViewModel(Device device, DeviceManagerService deviceManager, bool loadHistory, bool startTimer)
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

            // 先显示当前状态。现场模式继续从数据库加载真实历史；
            // 视频演示模式只使用本次启动的内存数据，避免历史演示记录污染计数。
            InitializeDisplayData();
            if (loadHistory && !App.IsDemoVideoMode)
                _ = LoadDataAsync();
            if (startTimer) _plcUpdateTimer.Start();
            UpdatePhaseVoltages();
        }

        private void InitializeCharts()
        {
            var series = new List<ISeries>
            {
                new LineSeries<double?>
                {
                    Name = "温度 (°C)",
                    Values = _temperatureValuesForVoltageChart,
                    GeometrySize = 0,
                    Stroke = new SolidColorPaint(new SKColor(240, 136, 62)) { StrokeThickness = 2.5f },
                    Fill = null,
                    ScalesYAt = 0
                }
            };

            // 仅在有电压数据时添加电压曲线
            if (HasVoltage)
            {
                series.Add(new LineSeries<double?>
                {
                    Name = "A相电压",
                    Values = _phaseAValues,
                    GeometrySize = 0,
                    Stroke = new SolidColorPaint(new SKColor(245, 183, 59)) { StrokeThickness = 1.5f },
                    Fill = null,
                    ScalesYAt = 1
                });
                series.Add(new LineSeries<double?>
                {
                    Name = "B相电压",
                    Values = _phaseBValues,
                    GeometrySize = 0,
                    Stroke = new SolidColorPaint(new SKColor(76, 175, 80)) { StrokeThickness = 1.5f },
                    Fill = null,
                    ScalesYAt = 1
                });
                series.Add(new LineSeries<double?>
                {
                    Name = "C相电压",
                    Values = _phaseCValues,
                    GeometrySize = 0,
                    Stroke = new SolidColorPaint(new SKColor(88, 166, 255)) { StrokeThickness = 1.5f },
                    Fill = null,
                    ScalesYAt = 1
                });
            }

            var labelPaint = new SolidColorPaint(new SKColor(176, 186, 196));
            var gridPaint = new SolidColorPaint(new SKColor(48, 54, 61)) { StrokeThickness = 1 };
            XAxes = new[]
            {
                new Axis
                {
                    Name = "时间",
                    Labels = TimeLabels,
                    TextSize = 12,
                    LabelsPaint = labelPaint,
                    NamePaint = labelPaint,
                    SeparatorsPaint = gridPaint,
                    MinStep = 1
                }
            };
            TemperatureYAxes = new[]
            {
                CreateAxis("温度(°C)", new SKColor(240, 136, 62), AxisPosition.Start)
            };
            CombinedYAxes = new[]
            {
                CreateAxis("温度(°C)", new SKColor(240, 136, 62), AxisPosition.Start),
                CreateAxis("电压(V)", new SKColor(88, 166, 255), AxisPosition.End)
            };
            CombinedSeries = series.ToArray();
            TimeLabels = Array.Empty<string>();
        }

        private static Axis CreateAxis(string name, SKColor color, AxisPosition position)
        {
            var paint = new SolidColorPaint(color);
            return new Axis
            {
                Name = name,
                Position = position,
                TextSize = 12,
                Labeler = value => value.ToString("F1"),
                LabelsPaint = paint,
                NamePaint = paint,
                SeparatorsPaint = new SolidColorPaint(new SKColor(48, 54, 61)) { StrokeThickness = 1 }
            };
        }

        partial void OnTimeLabelsChanged(string[] value)
        {
            if (XAxes.Length > 0)
                XAxes[0].Labels = value ?? Array.Empty<string>();
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
            _chart.FollowLive = range == "今日";
            var now = DateTime.Now;

            switch (range)
            {
                case "今日":
                    FilterStartDate = now.Date;
                    FilterEndDate = now;
                    break;
                case "本周":
                    var dayOfWeek = (int)now.DayOfWeek;
                    if (dayOfWeek == 0) dayOfWeek = 7;
                    FilterStartDate = now.Date.AddDays(-(dayOfWeek - 1));
                    FilterEndDate = now;
                    break;
                case "本月":
                    FilterStartDate = new DateTime(now.Year, now.Month, 1);
                    FilterEndDate = now;
                    break;
                case "全部":
                    FilterStartDate = new DateTime(2000, 1, 1);
                    FilterEndDate = now;
                    break;
            }

            await LoadDataAsync();
        }

        [RelayCommand]
        private async Task RefreshAsync()
        {
            if (_chart.FollowLive) FilterEndDate = DateTime.Now;
            await LoadDataAsync();
        }

        [RelayCommand]
        private async Task ExportToExcelAsync()
        {
            string filePath;
            var exportSource = new CancellationTokenSource();
            var previousExport = Interlocked.Exchange(ref _exportCts, exportSource);
            previousExport?.Cancel();
            previousExport?.Dispose();
            var cancellationToken = exportSource.Token;

            try
            {
                var device = CurrentDevice;
                var startTime = FilterStartDate;
                var endTime = FilterEndDate;
                using var dataService = new DataService();
                await dataService.InitializeAsync(cancellationToken);
                var exportData = await BoundedLogExportLoader.LoadAsync(
                    dataService,
                    device.Id,
                    startTime,
                    endTime,
                    includeTemperature: true,
                    includeOperation: true,
                    cancellationToken);
                var tempLogs = exportData.TemperatureLogs;
                var opLogs = exportData.OperationLogs;

                if (!tempLogs.Any() && !opLogs.Any())
                {
                    var result = MessageBox.Show(
                        $"数据库中暂无 [{device.Name}] 的数据可导出。\n\n" +
                        "是否要导出空模板？",
                        "暂无数据",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        filePath = await _excelService.ExportDeviceReadablePackageAsync(
                            device,
                            new List<TemperatureLog>(),
                            new List<OperationLog>(),
                            cancellationToken);
                        MessageBox.Show($"空模板导出成功!\n\n工控机可直接打开:\n{filePath}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    return;
                }

                filePath = await _excelService.ExportDeviceReadablePackageAsync(
                    device, tempLogs, opLogs, cancellationToken);

                MessageBox.Show(
                    $"导出成功!\n\n设备: {device.Name}\n温度记录: {tempLogs.Count} 条\n操作日志: {opLogs.Count} 条\n\n工控机可直接打开:\n{filePath}",
                    "导出成功",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                // 页面关闭或新的导出替换本次任务。
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void SaveTemperatureThreshold()
        {
            try
            {
                if (!float.IsFinite(TemperatureThreshold) ||
                    TemperatureThreshold < 0 || TemperatureThreshold > 500)
                {
                    MessageBox.Show("温度阈值必须是 0-500°C 之间的有限数值", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (CurrentDevice == null || _plcService?.Config == null)
                    throw new InvalidOperationException("当前设备配置不可用");

                // 先原子落盘，成功后才切换运行内存，避免保存失败却在本进程中悄悄生效。
                AppConfig.SaveDeviceThreshold(CurrentDevice.Id - 1, TemperatureThreshold);
                _plcService.Config.TemperatureThreshold = TemperatureThreshold;

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
            _deviceManager?.AcknowledgeAlarm();
            System.Diagnostics.Debug.WriteLine("[报警] 已确认并消音；报警条件未恢复前仍保持显示");
        }

        private async Task LoadDataAsync()
        {
            var loadVersion = Interlocked.Increment(ref _loadVersion);
            var loadSource = new CancellationTokenSource();
            var previousLoad = Interlocked.Exchange(ref _loadCts, loadSource);
            previousLoad?.Cancel();
            previousLoad?.Dispose();
            var cancellationToken = loadSource.Token;
            var deviceId = CurrentDevice.Id;
            var startTime = FilterStartDate;
            var endTime = FilterEndDate;

            try
            {
                System.Diagnostics.Debug.WriteLine("[DeviceDetailViewModel] LoadDataAsync 开始执行");

                using var dataService = new DataService();
                await dataService.InitializeAsync(cancellationToken);

                var operationCountTask = dataService.GetOperationLogCountAsync(
                    deviceId, startTime, endTime, cancellationToken);
                var statisticsTask = dataService.GetTemperatureStatisticsAsync(
                    deviceId, startTime, endTime, cancellationToken);
                var recentTemperatureTask = dataService.GetTemperatureLogsPagedAsync(
                    deviceId, startTime, endTime, 0, 50, cancellationToken);
                await Task.WhenAll(operationCountTask, statisticsTask, recentTemperatureTask);

                var totalOperationCount = await operationCountTask;
                var statistics = await statisticsTask;
                var tempLogs = await recentTemperatureTask;

                if (_isDisposed || loadVersion != Volatile.Read(ref _loadVersion))
                    return;

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (_isDisposed || loadVersion != Volatile.Read(ref _loadVersion))
                        return;

                    TotalOperationCount = totalOperationCount;
                    AbnormalCount = statistics.AbnormalCount;
                    AvgTemperature = statistics.Count == 0 ? 0 : statistics.Average;
                    MaxTemperature = statistics.Count == 0 ? 0 : statistics.Maximum;
                    MinTemperature = statistics.Count == 0 ? 0 : statistics.Minimum;

                    _chart.ReplaceHistory(tempLogs, endTime);
                    TimeLabels = _chart.Labels;
                    System.Diagnostics.Debug.WriteLine(
                        $"[DeviceDetailViewModel] 历史统计 {statistics.Count} 条，图表 {_chart.Temperatures.Count} 条");
                }, DispatcherPriority.Background);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 时间范围切换或页面关闭时的正常取消。
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载数据失败: {ex.Message}\n{ex.StackTrace}");
            }
        }

        internal void UpdatePhaseVoltages()
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
            var hasFreshAuxiliary = AuxiliaryTelemetry.IsFresh(status, PlcConfig.TemperatureInterval, DateTime.Now);
            var hasFreshPrimary = status.IsConnected && status.LastTemperatureSampleTime != default &&
                status.TemperatureQuality == TemperatureSampleQuality.Valid &&
                !(_plcService is MitsubishiPlcService service && service.IsTemperatureSampleDelayed(out _));
            TargetTemperatureDisplay = hasFreshAuxiliary
                ? $"{status.TargetTemperature:F1}°C"
                : "--.-°C";

            // 报警状态更新
            if (!status.IsAlarm)
                IsAlarmAcknowledged = false;
            IsAlarm = status.IsAlarm;
            IsSsrFault = hasFreshAuxiliary && status.IsSsrFault;

            // 电压文本（轻量 string 更新，不触发图表）
            if (HasVoltage)
            {
                PhaseAVoltage = hasFreshAuxiliary ? $"{status.ThermocoupleA:F3} V" : "--.- V";
                PhaseBVoltage = hasFreshAuxiliary ? $"{status.ThermocoupleB:F3} V" : "--.- V";
                PhaseCVoltage = hasFreshAuxiliary ? $"{status.ThermocoupleC:F3} V" : "--.- V";
                if (!hasFreshAuxiliary || !hasFreshPrimary ||
                    _diagnosisGeneration != status.LastTemperatureConnectionGeneration)
                {
                    _diagnosisGeneration = status.LastTemperatureConnectionGeneration;
                    _diagnosisTempHistory.Clear();
                    _diagnosisVoltageHistory.Clear();
                    _diagnosisSampleTimes.Clear();
                    HeatingDiagnosis = "加热诊断：数据缺失或过期，等待有效采样。";
                    PredictedTemperatureDisplay = "--.- °C";
                }
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
            var sampleHasAuxiliary = AuxiliaryTelemetry.IsFresh(status, PlcConfig.TemperatureInterval, sampleTime);
            _chart.AppendLive(new TemperatureLog
            {
                RecordTime = sampleTime, Temperature = currentTemp, TargetTemperature = status.TargetTemperature,
                ThermocoupleA = status.ThermocoupleA, ThermocoupleB = status.ThermocoupleB, ThermocoupleC = status.ThermocoupleC,
                AuxiliarySampleTime = status.LastAuxiliarySampleTime == default ? null : status.LastAuxiliarySampleTime,
                HasFreshAuxiliaryData = sampleHasAuxiliary
            });
            TimeLabels = _chart.Labels;

            // 电压曲线写入 + 诊断计算
            if (HasVoltage)
            {
                if (!hasFreshPrimary || !hasFreshAuxiliary || !sampleHasAuxiliary) return;

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
            try { _loadCts?.Cancel(); _loadCts?.Dispose(); } catch { }
            try { _exportCts?.Cancel(); _exportCts?.Dispose(); } catch { }

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
