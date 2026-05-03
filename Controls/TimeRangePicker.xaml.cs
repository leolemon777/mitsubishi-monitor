using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace MitsubishiMonitor.Demo.Controls
{
    /// <summary>
    /// 时间范围选择器：预设按钮 + 自定义日期选择
    /// </summary>
    public partial class TimeRangePicker : System.Windows.Controls.UserControl
    {
        public TimeRangePicker()
        {
            InitializeComponent();
            Presets = new ObservableCollection<string> { "今日", "本周", "本月", "全部", "自定义" };
            SelectedPreset = "今日";
            SelectPresetCommand = new RelayCommand<string>(ApplyPreset);
        }

        #region 依赖属性

        public static readonly DependencyProperty StartDateProperty =
            DependencyProperty.Register(nameof(StartDate), typeof(DateTime), typeof(TimeRangePicker),
                new FrameworkPropertyMetadata(DateTime.Today, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public static readonly DependencyProperty EndDateProperty =
            DependencyProperty.Register(nameof(EndDate), typeof(DateTime), typeof(TimeRangePicker),
                new FrameworkPropertyMetadata(DateTime.Now, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public static readonly DependencyProperty SelectedPresetProperty =
            DependencyProperty.Register(nameof(SelectedPreset), typeof(string), typeof(TimeRangePicker),
                new PropertyMetadata("今日"));

        public static readonly DependencyProperty PresetsProperty =
            DependencyProperty.Register(nameof(Presets), typeof(ObservableCollection<string>), typeof(TimeRangePicker),
                new PropertyMetadata(null));

        public static readonly DependencyProperty IsCustomModeProperty =
            DependencyProperty.Register(nameof(IsCustomMode), typeof(bool), typeof(TimeRangePicker),
                new PropertyMetadata(false));

        public static readonly DependencyProperty SelectPresetCommandProperty =
            DependencyProperty.Register(nameof(SelectPresetCommand), typeof(ICommand), typeof(TimeRangePicker),
                new PropertyMetadata(null));

        #endregion

        #region 属性

        public DateTime StartDate { get => (DateTime)GetValue(StartDateProperty); set => SetValue(StartDateProperty, value); }
        public DateTime EndDate { get => (DateTime)GetValue(EndDateProperty); set => SetValue(EndDateProperty, value); }
        public string SelectedPreset { get => (string)GetValue(SelectedPresetProperty); set => SetValue(SelectedPresetProperty, value); }
        public ObservableCollection<string> Presets { get => (ObservableCollection<string>)GetValue(PresetsProperty); set => SetValue(PresetsProperty, value); }
        public bool IsCustomMode { get => (bool)GetValue(IsCustomModeProperty); set => SetValue(IsCustomModeProperty, value); }
        public ICommand SelectPresetCommand { get => (ICommand)GetValue(SelectPresetCommandProperty); set => SetValue(SelectPresetCommandProperty, value); }

        #endregion

        private void ApplyPreset(string preset)
        {
            SelectedPreset = preset;
            var now = DateTime.Now;
            IsCustomMode = preset == "自定义";

            switch (preset)
            {
                case "今日":
                    StartDate = now.Date;
                    EndDate = now;
                    break;
                case "本周":
                    StartDate = now.Date.AddDays(-(int)now.DayOfWeek);
                    EndDate = now;
                    break;
                case "本月":
                    StartDate = new DateTime(now.Year, now.Month, 1);
                    EndDate = now;
                    break;
                case "全部":
                    StartDate = DateTime.MinValue.AddDays(1);
                    EndDate = now;
                    break;
            }
        }
    }
}
