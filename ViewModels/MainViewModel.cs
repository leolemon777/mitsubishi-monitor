using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveCharts;
using LiveCharts.Wpf;
using MitsubishiMonitor.Demo.Models;
using MitsubishiMonitor.Demo.Services;

namespace MitsubishiMonitor.Demo.ViewModels
{
    /// <summary>
    /// 点位标签模型
    /// </summary>
    public class PointLabel : ObservableObject
    {
        private bool _isOn;

        public string Address { get; set; }
        public string Description { get; set; }
        public string OnColor { get; set; }

        public bool IsOn
        {
            get => _isOn;
            set
            {
                _isOn = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// 主窗口ViewModel
    /// </summary>
    public partial class MainViewModel : ObservableObject
    {
        private readonly IPlcService _plcService;
        private readonly IDataService _dataService;
        private readonly ExcelExportService _excelService;
        private readonly LogBufferService _logBuffer;
        private readonly System.Timers.Timer _temperatureLogTimer;
        private readonly System.Timers.Timer _cleanupTimer;
        private readonly Queue<float> _temperatureHistory = new();

        [ObservableProperty]
        private PlcConfig _plcConfig;

        [ObservableProperty]
        private PlcStatus _plcStatus;

        [ObservableProperty]
        private string _connectionStatus = "未连接";

        [ObservableProperty]
        private bool _isConnected;

        [ObservableProperty]
        private string _runningTime = "00:00:00";

        [ObservableProperty]
        private string _lastUpdateTime = "--:--:--";

        [ObservableProperty]
        private float _currentTemperature;

        [ObservableProperty]
        private string _temperatureDisplay = "--.-°C";

        [ObservableProperty]
        private string _temperatureStats = "最低: -- | 最高: -- | 平均: --";

        [ObservableProperty]
        private bool _isTemperatureAbnormal;

        [ObservableProperty]
        private string _abnormalMessage = "";

        [ObservableProperty]
        private bool _isSimulationMode;

        [ObservableProperty]
        private string _thermocoupleADisplay = "--.- V";

        [ObservableProperty]
        private string _thermocoupleBDisplay = "--.- V";

        [ObservableProperty]
        private string _thermocoupleCDisplay = "--.- V";

        public ObservableCollection<OperationLog> OperationLogs { get; } = new();
        public ObservableCollection<PointLabel> XPointLabels { get; } = new();
        public ObservableCollection<PointLabel> YPointLabels { get; } = new();

        public SeriesCollection TemperatureSeries { get; set; }

        public string[] TimeLabels { get; set; }

        private DateTime _connectTime = DateTime.MinValue;
        private Timer _uiUpdateTimer;

        public MainViewModel()
        {
            _plcService = new MitsubishiPlcService();
            _dataService = new DataService();
            _excelService = new ExcelExportService();
            _logBuffer = new LogBufferService();

            _plcConfig = _plcService.Config;
            _plcStatus = _plcService.CurrentStatus;

            // 初始化X点标签
            InitializeXPointLabels();
            InitializeYPointLabels();

            // 订阅事件
            _plcService.ConnectionStateChanged += OnConnectionStateChanged;
            _plcService.StateChanged += OnStateChanged;

            // 初始化温度曲线图
            InitializeChart();

            // 温度记录定时器 (10秒)
            _temperatureLogTimer = new System.Timers.Timer(10000);
            _temperatureLogTimer.Elapsed += OnTemperatureLogTimerElapsed;
            _temperatureLogTimer.AutoReset = true;

            // 数据清理定时器 (每小时执行一次)
            _cleanupTimer = new System.Timers.Timer(3600000);
            _cleanupTimer.Elapsed += async (s, e) => await _dataService.CleanOldDataAsync();
            _cleanupTimer.AutoReset = true;
            _cleanupTimer.Start();

            // UI更新定时器 (更新运行时间等)
            _uiUpdateTimer = new System.Timers.Timer(1000);
            _uiUpdateTimer.Elapsed += UpdateUiInfo;
            _uiUpdateTimer.AutoReset = true;
            _uiUpdateTimer.Start();

            // 初始化数据库，完成后加载历史数据
            Task.Run(async () =>
            {
                await _dataService.InitializeAsync();
                await LoadHistoricalDataAsync();
            });
        }

        /// <summary>
        /// 异步加载历史日志和温度数据（后台线程执行，不阻塞 UI）
        /// </summary>
        private async Task LoadHistoricalDataAsync()
        {
            try
            {
                // 并行加载操作日志和温度记录
                var logsTask = _dataService.GetRecentOperationLogsAsync(50);
                var tempsTask = _dataService.GetRecentTemperatureLogsAsync(100);
                await Task.WhenAll(logsTask, tempsTask);

                var logs = logsTask.Result;
                var temps = tempsTask.Result;

                // 异步推送到 UI 线程，不阻塞后台
                App.Current?.Dispatcher.BeginInvoke(() =>
                {
                    // 加载操作日志
                    foreach (var log in logs)
                    {
                        OperationLogs.Add(log);
                    }

                    // 加载温度曲线历史
                    foreach (var temp in temps)
                    {
                        _temperatureHistory.Enqueue(temp.Temperature);
                    }

                    if (TemperatureSeries.FirstOrDefault() is LineSeries lineSeries)
                    {
                        lineSeries.Values.Clear();
                        foreach (var t in _temperatureHistory)
                        {
                            lineSeries.Values.Add(t);
                        }
                    }

                    UpdateTemperatureStats();
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainVM] 加载历史数据异常: {ex.Message}");
            }
        }

        private void InitializeXPointLabels()
        {
            // 按照现场点表定义 X000～X011
            var xLabels = new[]
            {
                new PointLabel { Address = "X000", Description = "急停按钮" },
                new PointLabel { Address = "X001", Description = "启动按钮" },
                new PointLabel { Address = "X002", Description = "停止按钮" },
                new PointLabel { Address = "X003", Description = "反应槽低液位" },
                new PointLabel { Address = "X004", Description = "反应槽中液位" },
                new PointLabel { Address = "X005", Description = "反应槽高液位" },
                new PointLabel { Address = "X006", Description = "反应槽极限液位" },
                new PointLabel { Address = "X007", Description = "暂存槽低液位" },
                new PointLabel { Address = "X010", Description = "暂存槽高液位" },
                new PointLabel { Address = "X011", Description = "暂存槽极限液位" },
            };

            foreach (var label in xLabels)
                XPointLabels.Add(label);
        }

        private void InitializeYPointLabels()
        {
            // 按照现场点表定义 Y000～Y017
            var yLabels = new[]
            {
                new PointLabel { Address = "Y000", Description = "水泵开启", OnColor = "#2196F3" },
                new PointLabel { Address = "Y001", Description = "反应槽进水", OnColor = "#4CAF50" },
                new PointLabel { Address = "Y002", Description = "储存槽进水", OnColor = "#4CAF50" },
                new PointLabel { Address = "Y003", Description = "反应槽进水循环", OnColor = "#FF9800" },
                new PointLabel { Address = "Y004", Description = "反应槽出水循环", OnColor = "#FF9800" },
                new PointLabel { Address = "Y005", Description = "储存槽进水循环", OnColor = "#FF9800" },
                new PointLabel { Address = "Y006", Description = "储存槽出水循环", OnColor = "#FF9800" },
                new PointLabel { Address = "Y007", Description = "储存槽排水", OnColor = "#FF9800" },
                new PointLabel { Address = "Y014", Description = "三色灯黄灯", OnColor = "#FFC107" },
                new PointLabel { Address = "Y015", Description = "三色灯绿灯", OnColor = "#4CAF50" },
                new PointLabel { Address = "Y016", Description = "三色灯红灯", OnColor = "#F44336" },
                new PointLabel { Address = "Y017", Description = "PID输出", OnColor = "#9C27B0" },
            };

            foreach (var label in yLabels)
                YPointLabels.Add(label);
        }

        private void InitializeChart()
        {
            TemperatureSeries = new SeriesCollection
            {
                new LineSeries
                {
                    Title = "温度",
                    Values = new ChartValues<float>(),
                    PointGeometry = null,
                    Stroke = new SolidColorBrush(Color.FromRgb(240, 136, 62)),
                    StrokeThickness = 2,
                    Fill = System.Windows.Media.Brushes.Transparent
                }
            };

            // 初始化空标签
            TimeLabels = new string[100];
            for (int i = 0; i < 100; i++)
            {
                TimeLabels[i] = "";
            }
        }

        #region Commands

        [RelayCommand]
        private async Task ConnectAsync()
        {
            var success = await _plcService.ConnectAsync();
            if (success)
            {
                _connectTime = DateTime.Now;
                _temperatureLogTimer.Start();
                _plcService.StartAcquisition();
            }
            else
            {
                MessageBox.Show(
                    $"连接PLC失败!\n\n请检查:\n1. IP地址: {PlcConfig.IpAddress}\n2. 端口: {PlcConfig.Port}\n3. 网络连接\n4. ENET-ADP模块配置",
                    "连接失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void Disconnect()
        {
            _plcService.StopAcquisition();
            _plcService.Disconnect();
            _temperatureLogTimer.Stop();
            _connectTime = DateTime.MinValue;
        }

        [RelayCommand]
        private async Task ExportToExcelAsync()
        {
            try
            {
                var tempLogs = await _dataService.GetRecentTemperatureLogsAsync(1000);
                var opLogs = await _dataService.GetAllOperationLogsAsync();
                string filePath;

                if (!tempLogs.Any() && !opLogs.Any())
                {
                    var result = MessageBox.Show(
                        "数据库中暂无数据可导出。\n\n" +
                        "温度数据每10秒自动记录一次，操作日志在IO点状态变化时记录。\n\n" +
                        "是否要导出空模板？",
                        "暂无数据",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        // 导出空模板
                        filePath = await _excelService.ExportAllAsync(new List<TemperatureLog>(), new List<OperationLog>());
                        MessageBox.Show($"空模板导出成功!\n\n文件位置:\n{filePath}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    return;
                }

                filePath = await _excelService.ExportAllAsync(tempLogs.ToList(), opLogs.ToList());

                MessageBox.Show(
                    $"导出成功!\n\n温度记录: {tempLogs.Count} 条\n操作日志: {opLogs.Count} 条\n\n文件位置:\n{filePath}",
                    "导出成功",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败: {ex.Message}\n\n详细错误:\n{ex}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Event Handlers

        private void OnConnectionStateChanged(object sender, bool isConnected)
        {
            IsConnected = isConnected;
            ConnectionStatus = isConnected ? "● 已连接" : "○ 未连接";
        }

        private void OnStateChanged(object sender, StateChangeEvent evt)
        {
            // 记录到缓冲队列（批量写入数据库）
            var log = OperationLog.FromChangeEvent(evt);
            _logBuffer.EnqueueOperationLog(log);

            // 添加到UI集合
            App.Current.Dispatcher.BeginInvoke(() =>
            {
                OperationLogs.Insert(0, log);
                if (OperationLogs.Count > 100)
                {
                    OperationLogs.RemoveAt(OperationLogs.Count - 1);
                }
            });
        }

        private async void OnTemperatureLogTimerElapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                var temp = _plcService.CurrentStatus.Temperature;
                var thermoA = _plcService.CurrentStatus.ThermocoupleA;
                var thermoB = _plcService.CurrentStatus.ThermocoupleB;
                var thermoC = _plcService.CurrentStatus.ThermocoupleC;

                var log = new TemperatureLog
                {
                    DeviceId = 1,
                    Temperature = temp,
                    ThermocoupleA = thermoA,
                    ThermocoupleB = thermoB,
                    ThermocoupleC = thermoC,
                    RecordTime = DateTime.Now,
                    IsAbnormal = temp > PlcConfig.TemperatureThreshold,
                    Threshold = PlcConfig.TemperatureThreshold
                };

                _logBuffer.EnqueueTemperatureLog(log);

                // 更新温度历史 (用于曲线图)
                App.Current.Dispatcher.BeginInvoke(() =>
                {
                    _temperatureHistory.Enqueue(temp);
                    if (_temperatureHistory.Count > 100)
                    {
                        _temperatureHistory.Dequeue();
                    }

                    // 更新曲线图数据
                    if (TemperatureSeries.FirstOrDefault() is LineSeries lineSeries)
                    {
                        lineSeries.Values.Clear();
                        foreach (var t in _temperatureHistory)
                        {
                            lineSeries.Values.Add(t);
                        }
                    }

                    // 更新统计信息
                    UpdateTemperatureStats();
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"温度记录异常: {ex.Message}");
            }
        }

        private async void UpdateTemperatureStats()
        {
            var (min, max, avg) = await _dataService.GetTemperatureStatsAsync(DateTime.Now.AddHours(-1));
            TemperatureStats = $"最低: {min:F1}°C | 最高: {max:F1}°C | 平均: {avg:F1}°C";
        }

        private void UpdateUiInfo(object sender, ElapsedEventArgs e)
        {
            // 更新运行时间
            if (_connectTime != DateTime.MinValue)
            {
                var span = DateTime.Now - _connectTime;
                RunningTime = $"{span.Hours:D2}:{span.Minutes:D2}:{span.Seconds:D2}";
            }

            // 更新最后更新时间
            if (_plcService.CurrentStatus.LastUpdateTime != DateTime.MinValue)
            {
                LastUpdateTime = _plcService.CurrentStatus.LastUpdateTime.ToString("HH:mm:ss");
            }

            // 更新温度显示
            CurrentTemperature = _plcService.CurrentStatus.Temperature;
            TemperatureDisplay = $"{CurrentTemperature:F1}°C";

            // 更新热电偶电压显示
            var thermoA = _plcService.CurrentStatus.ThermocoupleA;
            var thermoB = _plcService.CurrentStatus.ThermocoupleB;
            var thermoC = _plcService.CurrentStatus.ThermocoupleC;
            ThermocoupleADisplay = $"{thermoA:F3} V";
            ThermocoupleBDisplay = $"{thermoB:F3} V";
            ThermocoupleCDisplay = $"{thermoC:F3} V";

            // 更新X点标签状态
            for (int i = 0; i < Math.Min(XPointLabels.Count, PlcStatus.X.Length); i++)
            {
                XPointLabels[i].IsOn = PlcStatus.X[i];
            }

            // 更新Y点标签状态
            for (int i = 0; i < Math.Min(YPointLabels.Count, PlcStatus.Y.Length); i++)
            {
                // Y点有间隙(Y0-Y7, Y14-Y17)
                int yIndex = i < 8 ? i : i + 6;
                if (yIndex < PlcStatus.Y.Length)
                {
                    YPointLabels[i].IsOn = PlcStatus.Y[yIndex];
                }
            }

            // 检查温度异常
            IsTemperatureAbnormal = CurrentTemperature > PlcConfig.TemperatureThreshold;
            AbnormalMessage = IsTemperatureAbnormal ? $"⚠ 温度异常! 超过阈值 {PlcConfig.TemperatureThreshold}°C" : "";
        }

        #endregion
    }
}
