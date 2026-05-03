using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MitsubishiMonitor.Demo.Converters
{
    /// <summary>
    /// 布尔值反转转换器
    /// </summary>
    public class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool boolValue = value is bool b && !b;

            // 支持参数格式 "TrueValue:FalseValue"
            if (parameter != null)
            {
                string param = parameter.ToString() ?? "";
                string[] parts = param.Split(':');
                if (parts.Length == 2)
                {
                    return boolValue ? parts[0] : parts[1];
                }
            }

            return boolValue;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool boolValue && !boolValue;
        }
    }

    /// <summary>
    /// 布尔值转可见性转换器（同时支持整数：0为Collapsed，非0为Visible）
    /// </summary>
    public class BooleanToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool boolValue = false;

            if (value is bool b)
            {
                boolValue = b;
            }
            else if (value is int i)
            {
                boolValue = i > 0;
            }
            else if (value is double d)
            {
                boolValue = d > 0;
            }
            else if (value is float f)
            {
                boolValue = f > 0;
            }
            else if (value != null)
            {
                // 尝试解析为数字
                if (int.TryParse(value.ToString(), out int parsedInt))
                {
                    boolValue = parsedInt > 0;
                }
            }

            // 支持参数 "Invert" 来反转逻辑
            if (parameter?.ToString() == "Invert")
            {
                boolValue = !boolValue;
            }

            if (Invert)
                boolValue = !boolValue;

            return boolValue ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is Visibility v && v == Visibility.Visible;
        }
    }

    /// <summary>
    /// 反向布尔值转可见性转换器（true 时 Collapsed，false 时 Visible）
    /// </summary>
    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return boolValue ? Visibility.Collapsed : Visibility.Visible;
            }
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is Visibility v && v != Visibility.Visible;
        }
    }
}
