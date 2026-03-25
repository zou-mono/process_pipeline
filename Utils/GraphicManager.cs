using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
    }
}
