using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MitsubishiMonitor.Demo.Converters
{
    /// <summary>
    /// 布尔值转ON/OFF文本转换器
    /// </summary>
    public class BoolToOnOffConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool boolValue && boolValue ? "ON" : "OFF";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 布尔值转中文开关转换器
    /// </summary>
    public class BoolToChineseConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool boolValue && boolValue ? "开" : "关";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 布尔值转连接状态颜色转换器
    /// </summary>
    public class BoolToStatusColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return boolValue
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 200, 83))  // 绿色
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(158, 158, 158)); // 灰色
            }
            return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(158, 158, 158));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 布尔值转指示灯颜色转换器
    /// </summary>
    public class BoolToIndicatorColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return boolValue
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 200, 83))  // 亮绿
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(66, 66, 66));  // 深灰
            }
            return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(66, 66, 66));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 温度值转颜色转换器
    /// </summary>
    public class TemperatureToColorConverter : IValueConverter
    {
        public float Threshold { get; set; } = 50f;
        public float WarningThreshold { get; set; } = 80f;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is float temp)
            {
                if (temp >= WarningThreshold)
                    return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(211, 47, 47)); // 红色
                if (temp >= Threshold)
                    return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 171, 0)); // 橙色
                return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 188, 212));    // 青色
            }
            return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 188, 212));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 日志类型转图标转换器
    /// </summary>
    public class LogTypeToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value?.ToString() switch
            {
                "X" => "▶",
                "Y" => "◀",
                "TEMP" => "⚠",
                _ => "•"
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 数量转可见性转换器（0=Collapsed，>0=Visible）
    /// </summary>
    public class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int count) return count > 0 ? Visibility.Visible : Visibility.Collapsed;
            return Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    /// <summary>
    /// 时间转相对时间转换器
    /// </summary>
    public class TimeToRelativeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is DateTime dt)
            {
                var span = DateTime.Now - dt;
                if (span.TotalSeconds < 60)
                    return $"{span.Seconds}秒前";
                if (span.TotalMinutes < 60)
                    return $"{span.Minutes}分钟前";
                if (span.TotalHours < 24)
                    return $"{span.Hours}小时前";
                return dt.ToString("MM-dd HH:mm");
            }
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
