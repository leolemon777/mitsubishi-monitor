using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using MitsubishiMonitor.Demo.Models;

namespace MitsubishiMonitor.Demo.Controls
{
    /// <summary>
    /// 操作日志查看器用户控件
    /// 支持类型筛选、关键字搜索、颜色标记
    /// </summary>
    public partial class OperationLogViewer : UserControl
    {
        private ICollectionView _filteredView;

        public OperationLogViewer()
        {
            InitializeComponent();
        }

        #region 依赖属性

        public static readonly DependencyProperty LogsProperty =
            DependencyProperty.Register(nameof(Logs), typeof(ObservableCollection<OperationLog>), typeof(OperationLogViewer),
                new PropertyMetadata(null, OnLogsChanged));

        public static readonly DependencyProperty SearchTextProperty =
            DependencyProperty.Register(nameof(SearchText), typeof(string), typeof(OperationLogViewer),
                new PropertyMetadata(string.Empty, OnFilterChanged));

        public static readonly DependencyProperty SelectedLogTypeProperty =
            DependencyProperty.Register(nameof(SelectedLogType), typeof(string), typeof(OperationLogViewer),
                new PropertyMetadata("全部", OnFilterChanged));

        #endregion

        #region 属性

        public ObservableCollection<OperationLog> Logs
        {
            get => (ObservableCollection<OperationLog>)GetValue(LogsProperty);
            set => SetValue(LogsProperty, value);
        }

        public string SearchText
        {
            get => (string)GetValue(SearchTextProperty);
            set => SetValue(SearchTextProperty, value);
        }

        public string SelectedLogType
        {
            get => (string)GetValue(SelectedLogTypeProperty);
            set => SetValue(SelectedLogTypeProperty, value);
        }

        #endregion

        #region 回调

        private static void OnLogsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is OperationLogViewer viewer)
                viewer.SetupCollectionView();
        }

        private static void OnFilterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is OperationLogViewer viewer)
                viewer.ApplyFilter();
        }

        #endregion

        #region 筛选逻辑

        private void SetupCollectionView()
        {
            if (Logs == null) return;
            _filteredView = CollectionViewSource.GetDefaultView(Logs);
            _filteredView.Filter = FilterLogItem;
            LogList.ItemsSource = _filteredView;
        }

        private void ApplyFilter()
        {
            _filteredView?.Refresh();
        }

        private bool FilterLogItem(object item)
        {
            if (item is not OperationLog log) return false;

            // 类型筛选
            var type = SelectedLogType ?? "全部";
            if (type != "全部" && log.LogType != type)
                return false;

            // 关键字搜索
            var keyword = SearchText?.Trim();
            if (!string.IsNullOrEmpty(keyword))
            {
                var kw = keyword.ToLower();
                return (log.Description?.ToLower().Contains(kw) == true) ||
                       (log.PointAddress?.ToLower().Contains(kw) == true) ||
                       (log.Action?.ToLower().Contains(kw) == true) ||
                       (log.LogType?.ToLower().Contains(kw) == true);
            }

            return true;
        }

        #endregion

        #region 事件处理

        private void TypeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TypeFilter.SelectedItem is ComboBoxItem item)
            {
                SelectedLogType = item.Content?.ToString() ?? "全部";
            }
        }

        private void ClearSearch_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = string.Empty;
            SearchText = string.Empty;
        }

        #endregion
    }
}
