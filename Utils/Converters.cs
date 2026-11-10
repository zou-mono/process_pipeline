using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows;

namespace process_pipeline.Utils
{
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is Visibility v && v == Visibility.Visible;
    }

    /// <summary>
    /// 仿 ArcGIS 10.5 双行悬停提示（ToolTip）首行标题提取转换器：
    /// 自动从 ToolTip 文本中提取第一行（或首个冒号/破折号前）的简短功能名称作为粗体标题。
    /// </summary>
    public class ArcGisToolTipHeaderConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            SplitToolTipText(value as string, out string header, out _);
            return header;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;

        internal static void SplitToolTipText(string raw, out string header, out string body)
        {
            header = string.Empty;
            body = string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
                return;

            string normalized = raw.Replace("\r\n", "\n").Trim();
            int newlineIdx = normalized.IndexOf('\n');
            if (newlineIdx >= 0)
            {
                header = normalized.Substring(0, newlineIdx).Trim();
                body = normalized.Substring(newlineIdx + 1).Trim();
                return;
            }

            int colonIdx = normalized.IndexOf('：');
            if (colonIdx > 0 && colonIdx < normalized.Length - 1)
            {
                header = normalized.Substring(0, colonIdx).Trim();
                body = normalized.Substring(colonIdx + 1).Trim();
                return;
            }

            int dashIdx = normalized.IndexOf(" — ", StringComparison.Ordinal);
            if (dashIdx > 0 && dashIdx < normalized.Length - 3)
            {
                header = normalized.Substring(0, dashIdx).Trim();
                body = normalized.Substring(dashIdx + 3).Trim();
                return;
            }

            header = normalized;
        }
    }

    /// <summary>
    /// 仿 ArcGIS 10.5 双行悬停提示（ToolTip）第二行功能描述提取转换器：
    /// 自动从 ToolTip 文本中提取第二行起的功能详细说明，配合固定宽度自动折行展示。
    /// </summary>
    public class ArcGisToolTipBodyConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            ArcGisToolTipHeaderConverter.SplitToolTipText(value as string, out _, out string body);
            return body;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
