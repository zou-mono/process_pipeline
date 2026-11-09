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

        private static void EnsurePackUriRegistered()
        {
            if (!UriParser.IsKnownScheme("pack"))
            {
                string _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
            }
            // 触发 System.Windows.Application 静态构造函数以注册 pack:// WebRequest 协议处理器
            var __ = System.Windows.Application.Current;
        }

        public static void RefreshTheme(string explicitThemeName = null) 
        { 
            try
            {
                EnsurePackUriRegistered();
                string themeName = NormalizeThemeName(explicitThemeName ?? CadThemeName());

                // 加载对应的字典 (Dark.xaml 或 Light.xaml)
                var themeDict = new ResourceDictionary { 
                    Source = new Uri($"pack://application:,,,/process_pipeline;component/Themes/{themeName}.xaml", UriKind.Absolute) 
                };

                _cache.MergedDictionaries.Clear();

                // 合并通用资源 (Common.xaml / Icons.xaml / Styles.xaml)
                var CommonDict = new ResourceDictionary { 
                    Source = new Uri("pack://application:,,,/process_pipeline;component/Themes/Common.xaml", UriKind.Absolute) 
                };
                
                var IconsDict = new ResourceDictionary { 
                    Source = new Uri("pack://application:,,,/process_pipeline;component/Themes/Icons.xaml", UriKind.Absolute) 
                };

                var StylesDict = new ResourceDictionary { 
                    Source = new Uri("pack://application:,,,/process_pipeline;component/Themes/Styles.xaml", UriKind.Absolute) 
                };

                _cache.MergedDictionaries.Add(CommonDict);
                _cache.MergedDictionaries.Add(IconsDict);
                _cache.MergedDictionaries.Add(StylesDict);
                _cache.MergedDictionaries.Add(themeDict);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CadThemes.RefreshTheme] 资源加载失败: {ex.Message}");
            }
        }

       // 【核心】：根据 CAD 主题动态设置 WPF 颜色（支持 Dark / Light 双主题）
        /// <summary>
        /// 为指定的 WPF 控件或窗体应用 AutoCAD 当前的主题配色（COLORTHEME: 0=Dark, 1=Light）
        /// </summary>
        /// <param name="element">WPF的 Window 或 UserControl</param>
        /// <param name="explicitThemeName">可选显式指定主题名称（"Dark" 或 "Light"），为空则自动读取 CAD COLORTHEME 系统变量</param>
        public static void ApplyCadTheme(FrameworkElement element, string explicitThemeName = null)
        {
            if (element == null) return;

            try
            {
                EnsurePackUriRegistered();

                // 1. 获取 CAD 的主题变量：0 是深色(Dark)，1 是浅色(Light)
                string themeName = NormalizeThemeName(explicitThemeName ?? CadThemeName());
                RefreshTheme(themeName);

                // 2. 构造资源字典的 URI
                string uriPath = $"pack://application:,,,/process_pipeline;component/Themes/{themeName}.xaml";
                var newThemeDict = new ResourceDictionary 
                { 
                    Source = new Uri(uriPath, UriKind.Absolute) 
                };

                // 3. 优雅地替换旧主题
                var dictionaries = element.Resources.MergedDictionaries;
                for (int i = dictionaries.Count - 1; i >= 0; i--)
                {
                    string src = dictionaries[i]?.Source?.OriginalString;
                    if (!string.IsNullOrEmpty(src) &&
                        (src.IndexOf("Dark", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         src.IndexOf("Light", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        dictionaries.RemoveAt(i);
                    }
                }

                dictionaries.Add(newThemeDict);
            }
            catch (Exception ex)
            {
                // 容错处理：如果获取系统变量失败，可以在这里记录日志
                AcadApp.DocumentManager?.MdiActiveDocument?.Editor?.WriteMessage($"\n主题加载失败: {ex.Message}");
            }
        }

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
            try
            {
                object themeVar = AcadApp.GetSystemVariable("COLORTHEME");
                string themeName = (themeVar?.ToString() == "0") ? "Dark" : "Light";
                return themeName;
            }
            catch
            {
                return "Dark";
            }
        }

        private static string NormalizeThemeName(string themeName)
        {
            if (string.Equals(themeName, "Light", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(themeName, "1", StringComparison.OrdinalIgnoreCase))
            {
                return "Light";
            }
            return "Dark";
        }
    }
}
