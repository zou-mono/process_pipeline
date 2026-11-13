using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace process_pipeline.Themes
{
    public static class CadThemes
    {
        private static readonly ResourceDictionary _cache = new ResourceDictionary();
        private static string _lastActiveThemeName = "Dark";

        static CadThemes()
        {
            try
            {
                RefreshTheme();
            }
            catch (Exception ex)
            {
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
            var __ = System.Windows.Application.Current;
        }

        public static void RefreshTheme(string explicitThemeName = null)
        {
            try
            {
                EnsurePackUriRegistered();
                string themeName = NormalizeThemeName(explicitThemeName ?? CadThemeName());
                _lastActiveThemeName = themeName;

                var themeDict = new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/process_pipeline;component/Themes/{themeName}.xaml", UriKind.Absolute)
                };

                if (_cache.MergedDictionaries.Count == 0)
                {
                    var commonDict = new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/process_pipeline;component/Themes/Common.xaml", UriKind.Absolute)
                    };

                    var iconsDict = new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/process_pipeline;component/Themes/Icons.xaml", UriKind.Absolute)
                    };

                    var stylesDict = new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/process_pipeline;component/Themes/Styles.xaml", UriKind.Absolute)
                    };

                    _cache.MergedDictionaries.Add(commonDict);
                    _cache.MergedDictionaries.Add(iconsDict);
                    _cache.MergedDictionaries.Add(stylesDict);
                }
                else
                {
                    for (int i = _cache.MergedDictionaries.Count - 1; i >= 0; i--)
                    {
                        string src = _cache.MergedDictionaries[i]?.Source?.OriginalString;
                        if (!string.IsNullOrEmpty(src) &&
                            (src.IndexOf("Dark.xaml", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             src.IndexOf("Light.xaml", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            _cache.MergedDictionaries.RemoveAt(i);
                        }
                    }
                }

                _cache.MergedDictionaries.Add(themeDict);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CadThemes.RefreshTheme] 资源加载失败: {ex.Message}");
            }
        }

        private static readonly Dictionary<string, ResourceDictionary> _themeDictCache =
            new Dictionary<string, ResourceDictionary>(StringComparer.OrdinalIgnoreCase);
        private static ResourceDictionary _cachedBaseAppDict;

        /// <summary>
        /// 为指定的 WPF 控件或窗体应用 AutoCAD 当前的主题配色与原生图标（COLORTHEME: 0=Dark, 1=Light），
        /// 并同步更新其挂载的所有 ContextMenu（解决 WPF PopupRoot 独立视觉树无法向上查找 UserControl.Resources 的问题）。
        /// </summary>
        public static void ApplyCadTheme(FrameworkElement element, string explicitThemeName = null)
        {
            if (element == null) return;

            try
            {
                EnsurePackUriRegistered();

                string themeName = NormalizeThemeName(explicitThemeName ?? CadThemeName());
                RefreshTheme(themeName);

                ApplyThemeDictionaryToResources(element.Resources, themeName, ensureBaseAppDict: false);

                // 同步所有挂载在当前控件或子控件（如 DataGrid）上的 ContextMenu，避免 PopupRoot 隔离导致配色/图标冲突
                SyncDescendantContextMenus(element, themeName);
            }
            catch (Exception ex)
            {
                AcadApp.DocumentManager?.MdiActiveDocument?.Editor?.WriteMessage($"\n主题加载失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 显式同步指定 ContextMenu 的主题资源字典与 AutoCAD 2019 原生图标（供右键弹出前或初始化时调用）。
        /// 若当前 ContextMenu 已挂载目标主题字典且未发生主题切换，直接 O(1) 短路返回，杜绝右键弹出瞬间重建视觉树样式。
        /// </summary>
        public static void SyncContextMenuTheme(ContextMenu contextMenu, string explicitThemeName = null)
        {
            if (contextMenu == null) return;

            try
            {
                EnsurePackUriRegistered();
                string themeName = NormalizeThemeName(explicitThemeName ?? _lastActiveThemeName ?? CadThemeName());
                ApplyThemeDictionaryToResources(contextMenu.Resources, themeName, ensureBaseAppDict: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CadThemes.SyncContextMenuTheme] {ex.Message}");
            }
        }

        private static ResourceDictionary GetOrCreateThemeDictionary(string themeName)
        {
            if (_themeDictCache.TryGetValue(themeName, out ResourceDictionary cached) && cached != null)
            {
                return cached;
            }

            string uriPath = $"pack://application:,,,/process_pipeline;component/Themes/{themeName}.xaml";
            var dict = new ResourceDictionary
            {
                Source = new Uri(uriPath, UriKind.Absolute)
            };
            _themeDictCache[themeName] = dict;
            return dict;
        }

        private static ResourceDictionary GetOrCreateBaseAppDictionary()
        {
            if (_cachedBaseAppDict != null)
                return _cachedBaseAppDict;

            _cachedBaseAppDict = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/process_pipeline;component/Themes/App.xaml", UriKind.Absolute)
            };
            return _cachedBaseAppDict;
        }

        private static void ApplyThemeDictionaryToResources(
            ResourceDictionary targetResources,
            string themeName,
            bool ensureBaseAppDict)
        {
            if (targetResources == null || string.IsNullOrWhiteSpace(themeName)) return;

            var dictionaries = targetResources.MergedDictionaries;
            string expectedToken = $"{themeName}.xaml";
            string oppositeToken = string.Equals(themeName, "Light", StringComparison.OrdinalIgnoreCase)
                ? "Dark.xaml"
                : "Light.xaml";

            bool hasAppDict = false;
            bool hasExpectedTheme = false;
            bool hasOppositeTheme = false;

            for (int i = 0; i < dictionaries.Count; i++)
            {
                string src = dictionaries[i]?.Source?.OriginalString;
                if (string.IsNullOrEmpty(src)) continue;

                if (src.IndexOf("Themes/App.xaml", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    src.IndexOf("Themes/Common.xaml", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hasAppDict = true;
                }

                if (src.IndexOf(expectedToken, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hasExpectedTheme = true;
                }
                else if (src.IndexOf(oppositeToken, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hasOppositeTheme = true;
                }
            }

            // O(1) 短路：若目标资源集合已挂载当前主题且不存在相反主题（且满足基础字典要求），无需移除重挂，避免触发 WPF 菜单样式树重排
            if (hasExpectedTheme && !hasOppositeTheme && (!ensureBaseAppDict || hasAppDict))
            {
                return;
            }

            if (ensureBaseAppDict && !hasAppDict)
            {
                dictionaries.Insert(0, GetOrCreateBaseAppDictionary());
            }

            for (int i = dictionaries.Count - 1; i >= 0; i--)
            {
                string src = dictionaries[i]?.Source?.OriginalString;
                if (!string.IsNullOrEmpty(src) &&
                    (src.IndexOf("Dark.xaml", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     src.IndexOf("Light.xaml", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    dictionaries.RemoveAt(i);
                }
            }

            dictionaries.Add(GetOrCreateThemeDictionary(themeName));
        }

        private static void SyncDescendantContextMenus(DependencyObject root, string themeName)
        {
            if (root == null) return;

            var visited = new HashSet<DependencyObject>();
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(root);
            visited.Add(root);

            while (queue.Count > 0)
            {
                DependencyObject current = queue.Dequeue();

                if (current is ContextMenu directCm)
                {
                    SyncContextMenuTheme(directCm, themeName);
                }
                else if (current is FrameworkElement fe && fe.ContextMenu != null)
                {
                    SyncContextMenuTheme(fe.ContextMenu, themeName);
                }

                LocalValueEnumerator localEnum = current.GetLocalValueEnumerator();
                while (localEnum.MoveNext())
                {
                    object val = localEnum.Current.Value;
                    if (val is FrameworkElement || val is FrameworkContentElement)
                    {
                        var depPropVal = (DependencyObject)val;
                        if (visited.Add(depPropVal))
                        {
                            queue.Enqueue(depPropVal);
                        }
                    }
                }

                foreach (object logicalChildObj in LogicalTreeHelper.GetChildren(current))
                {
                    if (logicalChildObj is DependencyObject logicalChild && visited.Add(logicalChild))
                    {
                        queue.Enqueue(logicalChild);
                    }
                }

                if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
                {
                    int vCount = VisualTreeHelper.GetChildrenCount(current);
                    for (int i = 0; i < vCount; i++)
                    {
                        DependencyObject vChild = VisualTreeHelper.GetChild(current, i);
                        if (vChild != null && visited.Add(vChild))
                        {
                            queue.Enqueue(vChild);
                        }
                    }
                }
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
