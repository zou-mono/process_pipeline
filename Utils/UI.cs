using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;
using System.Globalization;
using System.Windows.Data;
using process_pipeline.Themes;

namespace process_pipeline.Utils
{
    public class VisualTree
    {
        /// <summary>
        /// 从点击事件的源向上查找指定类型的视觉树父级
        /// </summary>
        /// <typeparam name="T">要查找的控件类型 (如 DataGridRowHeader, DataGridCell)</typeparam>
        /// <param name="originalSource">事件参数中的 e.OriginalSource</param>
        /// <returns>找到的控件实例，若未找到则返回 null</returns>
        public static T GetClickedElement<T>(object originalSource) where T : DependencyObject
        {
            DependencyObject dep = originalSource as DependencyObject;
            while (dep != null && !(dep is T))
            {
                dep = VisualTreeHelper.GetParent(dep);
            }
            return dep as T;
        }

        /// <summary>
        /// 一次性获取点击位置关联的行头和行对象
        /// </summary>
        public static (DataGridRowHeader Header, DataGridRow Row) GetRowContext(object originalSource)
        {
            DependencyObject dep = originalSource as DependencyObject;
            DataGridRowHeader header = null;

            while (dep != null)
            {
                // 如果还没找到 Header，看看当前层级是不是 Header
                if (header == null && dep is DataGridRowHeader h)
                {
                    header = h;
                }

                // 继续向上，如果碰到了 Row，说明 Header 和 Row 的关系已经明确，直接返回
                if (dep is DataGridRow row)
                {
                    return (header, row);
                }

                // 向上爬一层
                dep = VisualTreeHelper.GetParent(dep);
            }

            return (null, null);
        }

        /// <summary>
        /// 通用的向上查找父级方法
        /// </summary>
        public static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parentObject = VisualTreeHelper.GetParent(child);
            if (parentObject == null) return null;
            if (parentObject is T parent) return parent;
            return FindVisualParent<T>(parentObject);
        }
    }

    public static class MenuIconHelper
    {
        // 用于指定图标键，如 "Copy"、"ZoomToExtent"
        public static readonly DependencyProperty IconKeyProperty =
            DependencyProperty.RegisterAttached(
                "IconKey",
                typeof(string),
                typeof(MenuIconHelper),
                new PropertyMetadata(null, OnIconKeyChanged));

        public static void SetIconKey(DependencyObject element, string value)
            => element.SetValue(IconKeyProperty, value);

        public static string GetIconKey(DependencyObject element)
            => (string)element.GetValue(IconKeyProperty);

        private static void OnIconKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            //if (d is not MenuItem menuItem) return;
            MenuItem menuItem = d as MenuItem;
            if (menuItem == null) return;

            menuItem.Icon = null;

            var iconKey = e.NewValue as string;
            if (string.IsNullOrWhiteSpace(iconKey)) return;

            var image = new Image
            {
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform
            };

            image.SetValue(IconPresenter.IconKeyProperty, iconKey);
    
            // 从 App 资源直接获取 Style
            //image.Style = FindStyle(menuItem, "Style.MenuIcon.Image");
            // 3. 【核心修复】动态查找 Style
            // 不要直接用 Application.Current.FindResource，因为它可能在 DLL 环境下失效
            // 尝试从 menuItem 自身开始向上查找资源（这能找到 UserControl 里 Merge 的资源）
            var style = FindResource<Style>(menuItem, "Style.MenuIcon.Image");
            if (style != null)
            {
                image.Style = style;
            }
            else
            {
                // 3. 【核心修复】如果现在找不到，说明还没进视觉树，等加载后再找
                RoutedEventHandler loadedHandler = null;
                loadedHandler = (s, arg) =>
                {
                    menuItem.Loaded -= loadedHandler; // 只执行一次
                    var retryStyle = FindResource<Style>(menuItem, "Style.MenuIcon.Image");
                    if (retryStyle != null)
                    {
                        image.Style = retryStyle;
                    }
                };
                menuItem.Loaded += loadedHandler;
            }

            menuItem.Icon = image;
        }

        /// <summary>
        /// 智能资源查找器：优先从控件上下文找，最后从全局找
        /// </summary>
        /// <typeparam name="T">资源类型</typeparam>
        /// <param name="element">起始查找的控件</param>
        /// <param name="resourceKey">资源键名</param>
        /// <returns>找到的资源，找不到则返回 default(T)</returns>
        private static T FindResource<T>(FrameworkElement element, object resourceKey)
        {
            // 1. 尝试从控件自身的资源链向上查找 (能找到 UserControl.Resources 里的 MergedDictionaries)
            if (element != null)
            {
                var resource = element.TryFindResource(resourceKey);
                if (resource is T result)
                {
                    return result;
                }
            }

            // 2. 尝试从 Application.Current 全局资源找 (最后的兜底)
            if (Application.Current != null)
            {
                var appResource = Application.Current.TryFindResource(resourceKey);
                if (appResource is T appResult)
                {
                    return appResult;
                }
            }

            // 3. 调试输出（建议在开发阶段保留，方便定位资源缺失）
            System.Diagnostics.Debug.WriteLine($"[Resource Error] 找不到资源: {resourceKey}");
    
            return default;
        }
    }

    /// <summary>
    /// 给 Image 存储 IconKey（用于样式里拼资源键）
    /// </summary>
    public static class IconPresenter
    {
        public static readonly DependencyProperty IconKeyProperty =
            DependencyProperty.RegisterAttached(
                "IconKey",
                typeof(string),
                typeof(IconPresenter),
                new PropertyMetadata(null));

        public static void SetIconKey(DependencyObject element, string value)
            => element.SetValue(IconKeyProperty, value);

        public static string GetIconKey(DependencyObject element)
            => (string)element.GetValue(IconKeyProperty);
    }

    public class IconResourceKeyConverter : IMultiValueConverter
    {
        // values[0] = IconKey (如 "Copy")
        // values[1] = IsEnabled (bool)
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            //if (values.Length < 2 || values[0] == null || values[0] == DependencyProperty.UnsetValue)
            //{
            //    return null;
            //}

            //var key = values[0] as string;
            //if (string.IsNullOrWhiteSpace(key))
            //    return null;

            //var isEnabled = true;
            //if (values.Length > 1 && values[1] is bool b)
            //    isEnabled = b;

            //return isEnabled
            //    ? $"Icon.{key}.Enabled"
            //    : $"Icon.{key}.Disabled";
            if (values.Length < 3 || values[0] == null || values[1] == null) return null;

            string key = values[1].ToString();
            bool isEnabled = (bool)values[2];

            string resKey = isEnabled ? $"Icon.{key}.Enabled" : $"Icon.{key}.Disabled";

            return CadThemes.GetResource<DrawingImage>(resKey);

            // 【核心改动】不要用 Application.Current！
            // 用控件自己的 FindResource，它会顺着 XAML 树向上找，
            // 绝对能找到 Icons.xaml 里的资源。
            //return element.FindResource(resKey);
            //return element.FindResource(resKey) ?? Application.Current.Resources[resKey];
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
