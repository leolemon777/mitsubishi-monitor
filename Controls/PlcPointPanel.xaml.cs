using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MitsubishiMonitor.Demo.Models;

namespace MitsubishiMonitor.Demo.Controls
{
    /// <summary>
    /// PLC 点位面板控件
    /// 显示所有 X/Y/M 点位的实时状态
    /// </summary>
    public partial class PlcPointPanel : UserControl
    {
        public PlcPointPanel()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        #region 依赖属性

        /// <summary>
        /// PLC 状态数据源
        /// </summary>
        public static readonly DependencyProperty PlcStatusProperty =
            DependencyProperty.Register(nameof(PlcStatus), typeof(PlcStatus), typeof(PlcPointPanel),
                new PropertyMetadata(null, OnPlcStatusChanged));

        /// <summary>
        /// PLC 配置（用于获取点位标签）
        /// </summary>
        public static readonly DependencyProperty PlcConfigProperty =
            DependencyProperty.Register(nameof(PlcConfig), typeof(PlcConfig), typeof(PlcPointPanel),
                new PropertyMetadata(null, OnPlcConfigChanged));

        public PlcStatus PlcStatus
        {
            get => (PlcStatus)GetValue(PlcStatusProperty);
            set => SetValue(PlcStatusProperty, value);
        }

        public PlcConfig PlcConfig
        {
            get => (PlcConfig)GetValue(PlcConfigProperty);
            set => SetValue(PlcConfigProperty, value);
        }

        #endregion

        #region 私有字段

        private PlcPointItem[] _xPointItems;
        private PlcPointItem[] _yPointItems;
        private PlcPointItem[] _mPointItems;

        #endregion

        #region 事件处理

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            InitializePointItems();
            UpdateGroupVisibility();
        }

        private static void OnPlcStatusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PlcPointPanel panel)
            {
                if (e.OldValue is PlcStatus oldStatus)
                {
                    oldStatus.PropertyChanged -= panel.OnPlcStatusPropertyChanged;
                }

                if (e.NewValue is PlcStatus newStatus)
                {
                    newStatus.PropertyChanged += panel.OnPlcStatusPropertyChanged;
                    panel.UpdateAllPoints();
                }
            }
        }

        private static void OnPlcConfigChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PlcPointPanel panel && panel.IsLoaded)
            {
                panel.InitializePointItems();
            }
        }

        private void OnPlcStatusPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // 当 PlcStatus 的属性变化时，更新对应的点位
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (e.PropertyName == nameof(PlcStatus.X))
                {
                    UpdateXPoints();
                }
                else if (e.PropertyName == nameof(PlcStatus.Y))
                {
                    UpdateYPoints();
                }
                else if (e.PropertyName == nameof(PlcStatus.M))
                {
                    UpdateMPoints();
                }
            }));
        }

        private void OnGroupChanged(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded)
            {
                return;
            }

            UpdateGroupVisibility();
        }

        #endregion

        #region 初始化方法

        private void InitializePointItems()
        {
            if (PlcConfig == null) return;

            // 初始化 X 点位
            InitializeXPoints();

            // 初始化 Y 点位
            InitializeYPoints();

            // 初始化 M 点位
            InitializeMPoints();

            // 更新所有点位状态
            UpdateAllPoints();
        }

        private void InitializeXPoints()
        {
            XPointsContainer.Items.Clear();
            int count = PlcConfig?.XCount ?? 12;
            _xPointItems = new PlcPointItem[count];

            for (int i = 0; i < count; i++)
            {
                var address = PlcConfig?.GetXAddress(i) ?? $"X{i}";
                var label = PlcConfig?.GetXLabel(i) ?? address;

                var item = new PlcPointItem
                {
                    Address = address,
                    Label = label,
                    PointType = "X",
                    OnColor = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                    OffColor = new SolidColorBrush(Color.FromRgb(66, 66, 66))
                };

                _xPointItems[i] = item;
                XPointsContainer.Items.Add(item);
            }
        }

        private void InitializeYPoints()
        {
            YPointsContainer.Items.Clear();
            int count = PlcConfig?.YCount ?? 16;
            _yPointItems = new PlcPointItem[count];

            for (int i = 0; i < count; i++)
            {
                var address = PlcConfig?.GetYAddress(i) ?? $"Y{i}";
                var label = PlcConfig?.GetYLabel(i) ?? address;

                var item = new PlcPointItem
                {
                    Address = address,
                    Label = label,
                    PointType = "Y",
                    OnColor = new SolidColorBrush(Color.FromRgb(33, 150, 243)),
                    OffColor = new SolidColorBrush(Color.FromRgb(66, 66, 66))
                };

                _yPointItems[i] = item;
                YPointsContainer.Items.Add(item);
            }
        }

        private void InitializeMPoints()
        {
            MPointsContainer.Items.Clear();
            int count = PlcConfig?.ActualMCount ?? 10;
            _mPointItems = new PlcPointItem[count];

            for (int i = 0; i < count; i++)
            {
                var address = PlcConfig?.GetMAddress(i) ?? $"M{i}";
                var label = PlcConfig?.GetMLabel(i) ?? address;

                var item = new PlcPointItem
                {
                    Address = address,
                    Label = label,
                    PointType = "M",
                    OnColor = new SolidColorBrush(Color.FromRgb(255, 152, 0)),
                    OffColor = new SolidColorBrush(Color.FromRgb(66, 66, 66))
                };

                _mPointItems[i] = item;
                MPointsContainer.Items.Add(item);
            }
        }

        #endregion

        #region 更新方法

        private void UpdateAllPoints()
        {
            UpdateXPoints();
            UpdateYPoints();
            UpdateMPoints();
        }

        private void UpdateXPoints()
        {
            if (PlcStatus == null || _xPointItems == null) return;

            for (int i = 0; i < Math.Min(PlcStatus.X.Length, _xPointItems.Length); i++)
            {
                if (_xPointItems[i] != null)
                {
                    _xPointItems[i].IsOn = PlcStatus.X[i];
                }
            }
        }

        private void UpdateYPoints()
        {
            if (PlcStatus == null || _yPointItems == null) return;

            for (int i = 0; i < Math.Min(PlcStatus.Y.Length, _yPointItems.Length); i++)
            {
                if (_yPointItems[i] != null)
                {
                    _yPointItems[i].IsOn = PlcStatus.Y[i];
                }
            }
        }

        private void UpdateMPoints()
        {
            if (PlcStatus == null || _mPointItems == null) return;

            for (int i = 0; i < Math.Min(PlcStatus.M.Length, _mPointItems.Length); i++)
            {
                if (_mPointItems[i] != null)
                {
                    _mPointItems[i].IsOn = PlcStatus.M[i];
                }
            }
        }

        private void UpdateGroupVisibility()
        {
            if (RadioAll == null || RadioX == null || RadioY == null || RadioM == null ||
                XPointsGroup == null || YPointsGroup == null || MPointsGroup == null)
            {
                return;
            }

            if (RadioAll.IsChecked == true)
            {
                XPointsGroup.Visibility = Visibility.Visible;
                YPointsGroup.Visibility = Visibility.Visible;
                MPointsGroup.Visibility = Visibility.Visible;
            }
            else if (RadioX.IsChecked == true)
            {
                XPointsGroup.Visibility = Visibility.Visible;
                YPointsGroup.Visibility = Visibility.Collapsed;
                MPointsGroup.Visibility = Visibility.Collapsed;
            }
            else if (RadioY.IsChecked == true)
            {
                XPointsGroup.Visibility = Visibility.Collapsed;
                YPointsGroup.Visibility = Visibility.Visible;
                MPointsGroup.Visibility = Visibility.Collapsed;
            }
            else if (RadioM.IsChecked == true)
            {
                XPointsGroup.Visibility = Visibility.Collapsed;
                YPointsGroup.Visibility = Visibility.Collapsed;
                MPointsGroup.Visibility = Visibility.Visible;
            }
        }

        #endregion
    }
}
