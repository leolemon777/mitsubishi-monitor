using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace MitsubishiMonitor.Demo.Views
{
    /// <summary>
    /// 系统设置对话框（数据库路径配置）
    /// </summary>
    public partial class SettingsDialog : Window
    {
        public SettingsDialog()
        {
            InitializeComponent();
            LoadCurrentSettings();
        }

        /// <summary>
        /// 加载当前配置到界面
        /// </summary>
        private void LoadCurrentSettings()
        {
            // 显示 config.json 中已保存的路径（空字符串表示使用默认路径）
            DbPathTextBox.Text = AppConfig.SavedDatabasePath ?? "";
            CurrentPathText.Text = AppConfig.DatabasePath;
        }

        /// <summary>
        /// 浏览按钮：弹出文件保存对话框选择数据库文件位置
        /// </summary>
        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "选择数据库保存位置",
                Filter = "SQLite数据库|*.db|所有文件|*.*",
                FileName = "Monitor.db",
                DefaultExt = ".db"
            };

            // 若当前已有路径，从该路径所在目录打开
            var currentPath = DbPathTextBox.Text.Trim();
            if (!string.IsNullOrEmpty(currentPath))
            {
                try
                {
                    dialog.InitialDirectory = System.IO.Path.GetDirectoryName(currentPath);
                }
                catch { /* 路径非法时忽略 */ }
            }

            if (dialog.ShowDialog() == true)
            {
                DbPathTextBox.Text = dialog.FileName;
            }
        }

        /// <summary>
        /// 保存按钮：将路径写入 config.json，提示重启
        /// </summary>
        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var newPath = DbPathTextBox.Text.Trim();

            // 非空时校验路径格式是否合法
            if (!string.IsNullOrEmpty(newPath))
            {
                try
                {
                    // GetFullPath 会在路径格式非法时抛异常
                    var fullPath = System.IO.Path.GetFullPath(newPath);
                    if (!fullPath.EndsWith(".db", System.StringComparison.OrdinalIgnoreCase))
                    {
                        MessageBox.Show("路径必须以 .db 结尾，例如：E:\\MonitorData\\Monitor.db",
                            "格式错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
                catch
                {
                    MessageBox.Show("路径格式不正确，请重新输入。\n示例：E:\\MonitorData\\Monitor.db",
                        "路径无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            // 写入 config.json
            AppConfig.SaveDatabasePath(newPath);

            MessageBox.Show(
                string.IsNullOrEmpty(newPath)
                    ? "已恢复默认路径，重启程序后生效。"
                    : $"设置已保存：\n{newPath}\n\n重启程序后生效。",
                "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
