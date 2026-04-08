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
using Autodesk.AutoCAD.Windows;

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
