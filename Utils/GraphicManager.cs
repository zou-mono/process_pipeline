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
using Autodesk.AutoCAD.ApplicationServices;
using process_pipeline.Commands;
using process_pipeline.Core;
using process_pipeline.Forms;
using process_pipeline.Models;

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

        public static void RefreshDataGrid(Document doc, List<ObjectId> idsToProcess)
        {
            GraphicManager.ClearAuxiliaryGraphics();

            // Commit 3.3 & 5.4：所有核查命令（MATCHPIPE / MATCHARROW 等）已统一收拢至主核查窗口 palCheckPipe，
            // 当面板可见时，复用当前文档会话的 ActiveRule 执行局部增量重算与单行就地刷新
            if (palCheckPipe.Instance.IsVisible)
            {
                var session = DocumentSessionManager.GetOrCreateSession(doc.Database);
                if (session?.ActiveRules != null && session.ActiveRules.Count > 1)
                {
                    foreach (ICheckRule activeRule in session.ActiveRules.Values.ToList())
                    {
                        if (activeRule == null) continue;
                        var service = new CheckPipeService(doc.Database, doc.Editor, activeRule);
                        service.Run(activeRule.RuleName ?? CadConfig.TaskNames.FlowArrow, true, idsToProcess);
                    }
                }
                else
                {
                    var service = new CheckPipeService(doc.Database, doc.Editor, session?.ActiveRule);
                    service.Run(CadConfig.TaskNames.FlowArrow, true, idsToProcess);
                }

                foreach (ObjectId oid in idsToProcess)
                {
                    var issuesForOid = session != null
                        ? session.GetIssuesByObjectId(oid)
                        : (palCheckPipe.Instance.CurrentIssues.TryGetValue(oid, out var singleIssue) && singleIssue != null
                            ? (IReadOnlyList<CheckIssueItem>)new[] { singleIssue }
                            : Array.Empty<CheckIssueItem>());

                    foreach (CheckIssueItem issue in issuesForOid)
                    {
                        if (issue != null &&
                            issue.Type == ProblemType.OneToMany &&
                            issue.Status != IssueReviewStatus.Resolved &&
                            issue.PossibleMatches != null &&
                            issue.PossibleMatches.Count > 0)
                        {
                            GraphicManager.DrawAuxiliaryLines(issue.PossibleMatches);
                            break;
                        }
                    }
                }
            }
        }

        public static List<ObjectId> CloneObjectIds(
            IEnumerable<ObjectId> ids)
        {
            if (ids == null)
                return new List<ObjectId>();

            return ids
                .Where(x => !x.IsNull)
                .Distinct()
                .ToList();
        }

        public static List<string> CloneHandles(
            IEnumerable<string> handles)
        {
            if (handles == null)
                return new List<string>();

            return handles
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
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
