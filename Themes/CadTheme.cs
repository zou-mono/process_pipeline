using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace process_pipeline.Themes
{
    public static class CadThemes
    {
        // 建议使用 pack URI 绝对路径，这是 WPF 最稳健的路径格式
        //private static readonly string ResourcePath = "pack://application:,,,/process_pipeline;component/Forms/App.xaml";
        private static readonly ResourceDictionary _cache = new ResourceDictionary();

        static CadThemes()
        {
            try
            {
                RefreshTheme();
            }
            catch (Exception ex)
            {
                // 如果路径不对，这里会抛错，方便你第一时间定位问题
                System.Diagnostics.Debug.WriteLine($"[Res] 资源加载失败: {ex.Message}");
                _cache = new ResourceDictionary(); 
            }
        }

        public static void RefreshTheme() 
        { 
            // 1. 检测 AutoCAD 当前主题 (假设你已有检测逻辑)
            //bool isDark = CheckIfAutoCADIsDark(); 
            string themeName = CadThemeName();
            //string themeFile = $"/process_pipeline;component/Themes/{themeName}.xaml";

            // 2. 加载对应的字典
            var themeDict = new ResourceDictionary { 
                Source = new Uri($"/process_pipeline;component/Themes/{themeName}.xaml", UriKind.RelativeOrAbsolute) 
            };

            _cache.MergedDictionaries.Clear();

            // 3. 合并通用资源 (如 Icons.xaml)
            var CommonDict = new ResourceDictionary { 
                Source = new Uri("/process_pipeline;component/Themes/Common.xaml", UriKind.RelativeOrAbsolute) 
            };
            
            var IconsDict = new ResourceDictionary { 
                Source = new Uri("/process_pipeline;component/Themes/Icons.xaml", UriKind.RelativeOrAbsolute) 
            };

            var StylesDict = new ResourceDictionary { 
                Source = new Uri("/process_pipeline;component/Themes/Styles.xaml", UriKind.RelativeOrAbsolute) 
            };

            _cache.MergedDictionaries.Add(CommonDict);
            _cache.MergedDictionaries.Add(IconsDict);
            _cache.MergedDictionaries.Add(StylesDict);
            _cache.MergedDictionaries.Add(themeDict);
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
                //object themeVar = AcadApp.GetSystemVariable("COLORTHEME");
                //string themeName = (themeVar?.ToString() == "0") ? "Dark" : "Light";
                string themeName = CadThemeName();

                // 2. 构造资源字典的 URI
                string uriPath = $"/process_pipeline;component/Themes/{themeName}.xaml";
                var newThemeDict = new ResourceDictionary 
                { 
                    Source = new Uri(uriPath, UriKind.RelativeOrAbsolute) 
                };

                // 3. 优雅地替换旧主题
                // 建议：如果你的 MergedDictionaries 里还有别的字典（如 Icons），
                // 最好通过判断 Source 来移除旧主题，而不是 Clear() 全部。
                var dictionaries = element.Resources.MergedDictionaries;
                for (int i = dictionaries.Count - 1; i >= 0; i--)
                {
                    if (dictionaries[i].Source.OriginalString.Contains("Dark") || 
                        dictionaries[i].Source.OriginalString.Contains("Light"))
                    {
                        dictionaries.RemoveAt(i);
                    }
                }

                dictionaries.Add(newThemeDict);
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

        public static string CadThemeName() 
        { 
            object themeVar = AcadApp.GetSystemVariable("COLORTHEME");
            string themeName = (themeVar?.ToString() == "0") ? "Dark" : "Light";

            return themeName;
        }
    }
}
