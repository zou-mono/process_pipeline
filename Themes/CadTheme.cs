using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace process_pipeline.Forms
{
    public static class CadThemes
    {
        // 建议使用 pack URI 绝对路径，这是 WPF 最稳健的路径格式
        private static readonly string ResourcePath = "pack://application:,,,/process_pipeline;component/Forms/App.xaml";
        private static readonly ResourceDictionary _cache;

        static CadThemes()
        {
            try
            {
                _cache = new ResourceDictionary
                {
                    Source = new Uri(ResourcePath, UriKind.Absolute)
                };
            }
            catch (Exception ex)
            {
                // 如果路径不对，这里会抛错，方便你第一时间定位问题
                System.Diagnostics.Debug.WriteLine($"[Res] 资源加载失败: {ex.Message}");
                _cache = new ResourceDictionary(); 
            }
        }

       // 【核心】：根据 CAD 主题动态设置 WPF 颜色
        /// <summary>
        /// 为指定的 WPF 控件或窗体应用 AutoCAD 当前的主题配色
        /// </summary>
        /// <param name="element">WPF的 Window 或 UserControl</param>
        public static void ApplyCadTheme(FrameworkElement element)
        {
            if (element == null) return;

            try
            {
                // 1. 获取 CAD 的主题变量：0 是深色(Dark)，1 是浅色(Light)
                object themeVar = AcadApp.GetSystemVariable("COLORTHEME");
                bool isDarkTheme = themeVar != null && themeVar.ToString() == "0";

                // 2. 创建新的资源字典
                ResourceDictionary themeDict = new ResourceDictionary();

                if (isDarkTheme)
                {
                    // --- CAD 深色主题配色 ---
                    // 面板底色 (稍浅的深灰)
                    themeDict.Add("PanelBackground", new SolidColorBrush((Color)ColorConverter.ConvertFromString("#383838")));
                    // 表头背景色 (类似图层管理器的表头)
                    themeDict.Add("HeaderBackground", new SolidColorBrush((Color)ColorConverter.ConvertFromString("#45464A")));
                    // 数据行背景 (主背景，较深)
                    themeDict.Add("RowBackground", new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2D30")));
                    // 交替行背景 (稍微亮一点点，增加层次感)
                    themeDict.Add("AltRowBackground", new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333337")));
                    // 字体颜色 (CAD标准浅灰白)
                    themeDict.Add("TextForeground", new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DFDFDF")));
                    // 网格线颜色 (非常关键！调亮一点才能看清，类似 #555555)
                    themeDict.Add("GridLineColor", new SolidColorBrush((Color)ColorConverter.ConvertFromString("#555555")));
                    // 选中行背景 (CAD 经典的选中蓝)
                    themeDict.Add("SelectedRowBackground", new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E5E96")));
                    // 选中行文字
                    themeDict.Add("SelectedTextForeground", new SolidColorBrush(Colors.White));
                }
                else
                {
                    // --- CAD 浅色主题配色 ---
                    themeDict.Add("PanelBackground", new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F0F0F0")));
                    themeDict.Add("HeaderBackground", new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0E0E0")));
                    themeDict.Add("RowBackground", new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF")));
                    themeDict.Add("AltRowBackground", new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F8F8F8")));
                    themeDict.Add("TextForeground", new SolidColorBrush((Color)ColorConverter.ConvertFromString("#111111")));
                    themeDict.Add("GridLineColor", new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC")));
                    themeDict.Add("SelectedRowBackground", new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCE8FF")));
                    themeDict.Add("SelectedTextForeground", new SolidColorBrush(Colors.Black));
                }

                // 3. 清空旧的动态资源，防止多次调用导致内存泄漏或重复
                element.Resources.MergedDictionaries.Clear();

                // 4. 将生成的颜色字典注入到目标控件中
                element.Resources.MergedDictionaries.Add(themeDict);
            }
            catch (Exception ex)
            {
                // 容错处理：如果获取系统变量失败，可以在这里记录日志
                AcadApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\n主题加载失败: {ex.Message}");
            }
        }



        // 辅助方法：安全地从资源字典获取对象
// 泛型获取，找不到返回 null
        //public static T GetResource<T>(string key) where T : class 
        //    => System.Windows.Application.Current?.TryFindResource(key) as T;
        /// <summary>
        /// 获取资源字典中的对象
        /// </summary>
        public static T GetResource<T>(string key)
        {
            if (string.IsNullOrEmpty(key)) return default;

            if (_cache.Contains(key))
            {
                var obj = _cache[key];
                if (obj is T result) return result;
            }
            
            return default;
        }
    }
}
