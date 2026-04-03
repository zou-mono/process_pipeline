using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace process_pipeline.Utils
{    
    public static class GraphicManager
    {
        // 全局静态列表，保存所有当前显示的临时图形
        private static List<Drawable> _tempGraphics = new List<Drawable>();

        /// <summary>
        /// 将一个内存中的图形（如 Line）作为临时图形显示在屏幕最顶层
        /// </summary>
        public static void AddGraphic(Drawable drawable)
        {
            if (drawable == null) return;

            // 调用 CAD 底层 API 渲染到屏幕
            TransientManager.CurrentTransientManager.AddTransient(
                drawable, 
                TransientDrawingMode.DirectTopmost, // 直接画在最顶层，不会被遮挡
                128, 
                new IntegerCollection()
            );

            // 加入全局列表以便后续清理
            _tempGraphics.Add(drawable);
        }

        public static void ClearAuxiliaryGraphics()
        {
            if (_tempGraphics.Count == 0) return;

            foreach (var drawable in _tempGraphics)
            {
                // 从屏幕上抹除这个瞬态图形
                TransientManager.CurrentTransientManager.EraseTransient(
                    drawable, 
                    new IntegerCollection()
                );
        
                // 释放非托管内存
                drawable.Dispose();
            }

            _tempGraphics.Clear();
        }

        public static void DrawAuxiliaryLines(List<MatchItem> possibleMatches)
        {
            // 1. 每次画新线之前，先把旧的临时线清掉
            ClearAuxiliaryGraphics();

            foreach (MatchItem match in possibleMatches)
            {
                Point3d arrowPt = match.Position; // 箭头起点
                Point3d closePoint = match.closePoint; // 箭头终点

                // 2. 直接在内存中 new 一个 Line，【绝对不要】把它加到图纸数据库(btr.AppendEntity)里！
                Line auxLine = new Line(arrowPt, closePoint);
                auxLine.ColorIndex = 1; // 1 = 红色

                // 可选：如果你想让线型变成虚线，可以直接改 Linetype，但通常红色实线就够显眼了
                // auxLine.LineWeight = LineWeight.LineWeight018; 

                AddGraphic(auxLine);

                //// 3. 核心魔法：将这个内存中的 Line 注册为瞬态图形，直接渲染到屏幕顶层
                //TransientManager.CurrentTransientManager.AddTransient(
                //    auxLine, 
                //    TransientDrawingMode.DirectTopmost, // 渲染模式：直接画在最顶层，不被其他图形遮挡
                //    128,                                // 默认子绘制模式
                //    new IntegerCollection()             // 视口ID集合，空的代表所有视口
                //);

                //// 4. 把这根线存起来，方便等会儿删掉
                //_tempGraphics.Add(auxLine);
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
    }

    /// <summary>
/// 统一管理 Unicode 图标（纯字符串，优雅无依赖）
/// </summary>
    public static class IconUnicode
    {
        // 成功/完成
        public const string Success = "\u2714";    // ✅ 
        public const string OK = "\u2713";        // ✓
        
        // 错误/失败
        public const string Error = "\u2716";     // ❌
        public const string Fail = "\u2717";      // ✗
    
        // 警告/提示
        public const string Warning = "\u26A0";   // ⚠️
        public const string Info = "\u2139";      // ℹ️
    
        // 其他常用
        public const string Question = "\u2753";  // ❓
        public const string Loading = "\u2699";   // ⚙️
    }
}
