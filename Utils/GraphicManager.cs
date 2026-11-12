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
        /// 当前活动瞬态图形数量（供诊断与自动化测试断言）。
        /// </summary>
        public static int ActiveTransientCount => _tempGraphics.Count;

        /// <summary>
        /// 将一个内存中的图形（如 Line）作为临时图形显示在屏幕最顶层
        /// </summary>
        public static void AddGraphic(Drawable drawable)
        {
            if (drawable == null) return;

            try
            {
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
            catch
            {
                try { drawable.Dispose(); } catch { }
            }
        }

        public static void ClearAuxiliaryGraphics()
        {
            if (_tempGraphics.Count == 0) return;

            foreach (var drawable in _tempGraphics)
            {
                try
                {
                    // 从屏幕上抹除这个瞬态图形
                    TransientManager.CurrentTransientManager.EraseTransient(
                        drawable, 
                        new IntegerCollection()
                    );
                }
                catch
                {
                }

                try
                {
                    // 释放非托管内存
                    drawable.Dispose();
                }
                catch
                {
                }
            }

            _tempGraphics.Clear();
        }

        public static void DrawAuxiliaryLines(List<MatchItem> possibleMatches)
        {
            // 1. 每次画新线之前，先把旧的临时线清掉
            ClearAuxiliaryGraphics();
            AppendAuxiliaryMatchLines(possibleMatches);
        }

        /// <summary>
        /// 追加绘制候选配对的红色瞬态辅助连线与锚定圆圈（当属性块插入点恰好落在直管段上导致距离 &lt; 0.05m 时，自动绘制半径 1.2m 的醒目红色定位圆圈）。
        /// </summary>
        public static void AppendAuxiliaryMatchLines(IEnumerable<MatchItem> possibleMatches)
        {
            if (possibleMatches == null) return;

            foreach (MatchItem match in possibleMatches)
            {
                Point3d arrowPt = match.Position;      // 附属要素（块/文字/箭头）位置
                Point3d closePoint = match.closePoint; // 管线最近投影点
                double dist = arrowPt.DistanceTo(closePoint);

                if (dist >= 0.05)
                {
                    // 在内存中创建红色瞬态连线与管线落点小圆圈（绝不加入数据库）
                    Line auxLine = new Line(arrowPt, closePoint)
                    {
                        ColorIndex = 1, // 1 = 红色
                        LineWeight = LineWeight.LineWeight030
                    };
                    AddGraphic(auxLine);

                    Circle anchorCircle = new Circle(closePoint, Vector3d.ZAxis, 0.8)
                    {
                        ColorIndex = 1,
                        LineWeight = LineWeight.LineWeight030
                    };
                    AddGraphic(anchorCircle);
                }
                else
                {
                    // 当属性块恰好位于直管段中点（距离 < 0.05m，如 B8、CE 等）时，零长度 Line 不可见，绘制半径 1.2m 的醒目红色锚定圆圈
                    Circle onPipeCircle = new Circle(closePoint, Vector3d.ZAxis, 1.2)
                    {
                        ColorIndex = 1,
                        LineWeight = LineWeight.LineWeight030
                    };
                    AddGraphic(onPipeCircle);
                }
            }
        }

        /// <summary>
        /// 针对核查面板选中的一行或多行问题记录，在图面最顶层（DirectTopmost）绘制轻量级瞬态视觉高亮辅助图形（方案 B）：
        /// 1. 目标管线（TargetObjectId）使用纯内存 Line 原语绘制为加粗洋红色（ACI=6，Magenta）；
        /// 2. 配对属性块/文字标注/流向箭头（RelatedObjectIds 及 PossibleMatches）提取其几何包围盒（GeometricExtents），
        ///    在属性块外围绘制同色（洋红色 ACI=6）矩形外框，不克隆块内子文字，兼顾视觉辨识度与多选流畅度；
        /// 3. 配对连线与落点锚定圈（PossibleMatches）绘制为红色（ACI=1，Red）辅助图形，切换行时自动擦除恢复原状。
        /// </summary>
        public static void DrawIssueHighlights(Autodesk.AutoCAD.DatabaseServices.Database db, IEnumerable<CheckIssueItem> issues)
        {
            ClearAuxiliaryGraphics();
            if (db == null || issues == null) return;

            var targetIssues = issues.Where(i => i != null).ToList();
            if (targetIssues.Count == 0) return;

            const short HighlightMagentaColor = 6; // 6 = 洋红色 (Magenta)，使目标管线与配对属性块外框保持同色高亮
            var highlightedEntities = new HashSet<ObjectId>();

            try
            {
                using (OpenCloseTransaction tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    foreach (CheckIssueItem issue in targetIssues)
                    {
                        // 1. 高亮主目标图元（洋红色 ACI=6）
                        if (!issue.TargetObjectId.IsNull && issue.TargetObjectId.IsValid && !issue.TargetObjectId.IsErased &&
                            highlightedEntities.Add(issue.TargetObjectId))
                        {
                            if (tr.GetObject(issue.TargetObjectId, OpenMode.ForRead, false) is Entity targetEnt)
                            {
                                AddTransientEntityDrawables(targetEnt, HighlightMagentaColor, LineWeight.LineWeight050);
                            }
                        }

                        // 2. 收集并高亮关联图元（配对的 ws 属性块、文字标注、流向箭头绘制同色洋红色 ACI=6 矩形包围盒外框）
                        var relIds = new List<ObjectId>();
                        if (issue.RelatedObjectIds != null)
                        {
                            relIds.AddRange(issue.RelatedObjectIds);
                        }
                        if (issue.PossibleMatches != null)
                        {
                            foreach (MatchItem m in issue.PossibleMatches)
                            {
                                if (!m.Id.IsNull)
                                    relIds.Add(m.Id);
                            }
                        }

                        foreach (ObjectId relId in relIds)
                        {
                            if (relId.IsNull || !relId.IsValid || relId.IsErased || !highlightedEntities.Add(relId))
                                continue;

                            if (tr.GetObject(relId, OpenMode.ForRead, false) is Entity relEnt)
                            {
                                AddTransientEntityDrawables(relEnt, HighlightMagentaColor, LineWeight.LineWeight050);
                            }
                        }

                        // 3. 绘制红色配对辅助连线与管线落点锚定圆圈
                        if (issue.PossibleMatches != null && issue.PossibleMatches.Count > 0)
                        {
                            AppendAuxiliaryMatchLines(issue.PossibleMatches);
                        }
                    }
                }
            }
            catch
            {
                // 忽略瞬态图形构建异常
            }
        }

        /// <summary>
        /// 在内存中创建不依赖数据库 LayerId / TextStyleId 的纯几何 Line 原语加入 TransientManager：
        /// - 针对管线（Polyline / Line）：按各管段顶点生成洋红色加粗 Line 原语；
        /// - 针对属性块/文字（BlockReference / DBText / MText）：按方案 B 生成包围盒（GeometricExtents）同色洋红色矩形外框。
        /// </summary>
        private static void AddTransientEntityDrawables(Entity ent, short colorIndex, LineWeight lineWeight)
        {
            if (ent == null) return;

            try
            {
                if (ent is Autodesk.AutoCAD.DatabaseServices.Polyline pl && pl.NumberOfVertices >= 2)
                {
                    int segCount = pl.Closed ? pl.NumberOfVertices : pl.NumberOfVertices - 1;
                    for (int i = 0; i < segCount; i++)
                    {
                        SegmentType segType = pl.GetSegmentType(i);
                        if (segType == SegmentType.Line)
                        {
                            LineSegment3d seg = pl.GetLineSegmentAt(i);
                            AddGraphic(new Line(seg.StartPoint, seg.EndPoint)
                            {
                                ColorIndex = colorIndex,
                                LineWeight = lineWeight
                            });
                        }
                        else if (segType == SegmentType.Arc)
                        {
                            CircularArc3d arc = pl.GetArcSegmentAt(i);
                            PointOnCurve3d[] samplePts = arc.GetSamplePoints(13);
                            for (int k = 0; k < samplePts.Length - 1; k++)
                            {
                                AddGraphic(new Line(samplePts[k].Point, samplePts[k + 1].Point)
                                {
                                    ColorIndex = colorIndex,
                                    LineWeight = lineWeight
                                });
                            }
                        }
                    }
                    return;
                }

                if (ent is Line ln)
                {
                    AddGraphic(new Line(ln.StartPoint, ln.EndPoint)
                    {
                        ColorIndex = colorIndex,
                        LineWeight = lineWeight
                    });
                    return;
                }

                if (ent is BlockReference || ent is DBText || ent is MText)
                {
                    AddTransientBoundingBox(ent, colorIndex, lineWeight);
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// 方案 B：提取属性块或文字标注的几何外包框（GeometricExtents），在内存中绘制 4 条纯几何 Line 组成的同色矩形高亮外框。
        /// </summary>
        private static void AddTransientBoundingBox(Entity ent, short colorIndex, LineWeight lineWeight)
        {
            if (ent == null) return;

            double minX, minY, maxX, maxY, z = 0.0;
            try
            {
                Extents3d ext = ent.GeometricExtents;
                minX = ext.MinPoint.X;
                minY = ext.MinPoint.Y;
                maxX = ext.MaxPoint.X;
                maxY = ext.MaxPoint.Y;
                z = ext.MinPoint.Z;
            }
            catch
            {
                Point3d fallbackPt = Point3d.Origin;
                if (ent is BlockReference br) fallbackPt = br.Position;
                else if (ent is DBText txt) fallbackPt = txt.Position;
                else if (ent is MText mtxt) fallbackPt = mtxt.Location;

                minX = fallbackPt.X - 1.5;
                maxX = fallbackPt.X + 1.5;
                minY = fallbackPt.Y - 1.5;
                maxY = fallbackPt.Y + 1.5;
                z = fallbackPt.Z;
            }

            double span = Math.Max(maxX - minX, maxY - minY);
            double pad = Math.Max(0.4, Math.Min(1.5, span * 0.03));

            Point3d p1 = new Point3d(minX - pad, minY - pad, z);
            Point3d p2 = new Point3d(maxX + pad, minY - pad, z);
            Point3d p3 = new Point3d(maxX + pad, maxY + pad, z);
            Point3d p4 = new Point3d(minX - pad, maxY + pad, z);

            AddGraphic(new Line(p1, p2) { ColorIndex = colorIndex, LineWeight = lineWeight });
            AddGraphic(new Line(p2, p3) { ColorIndex = colorIndex, LineWeight = lineWeight });
            AddGraphic(new Line(p3, p4) { ColorIndex = colorIndex, LineWeight = lineWeight });
            AddGraphic(new Line(p4, p1) { ColorIndex = colorIndex, LineWeight = lineWeight });
        }

        public static void RefreshDataGrid(Document doc, List<ObjectId> idsToProcess)
        {
            GraphicManager.ClearAuxiliaryGraphics();

            if (doc?.Database == null || idsToProcess == null || idsToProcess.Count == 0)
                return;

            List<ObjectId> validIds = idsToProcess
                .Where(id => !id.IsNull && id.Database == doc.Database)
                .Distinct()
                .ToList();
            if (validIds.Count == 0)
                return;

            // Commit 3.3 & 5.4：所有核查命令（MATCHPIPE / MATCHARROW 等）已统一收拢至主核查面板 palCheckPipe，
            // 当面板可见时，复用当前文档会话的已激活规则执行局部增量重算与单行就地刷新
            if (palCheckPipe.Instance.IsVisible)
            {
                var session = DocumentSessionManager.GetOrCreateSession(doc.Database);
                var activeRules = new List<ICheckRule>();
                if (session?.ActiveRules != null && session.ActiveRules.Count > 0)
                {
                    activeRules.AddRange(session.ActiveRules.Values.Where(r => r != null));
                }
                else if (session?.ActiveRule != null)
                {
                    activeRules.Add(session.ActiveRule);
                }

                // 若当前文档尚未执行过任何核查规则，静默跳过后台增量重算，防止凭空伪造 FlowArrowService 弹窗报错
                if (activeRules.Count == 0)
                    return;

                foreach (ICheckRule activeRule in activeRules)
                {
                    var service = new CheckPipeService(doc.Database, doc.Editor, activeRule);
                    service.Run(activeRule.RuleName ?? CadConfig.TaskNames.FlowArrow, true, validIds);
                }

                foreach (ObjectId oid in validIds)
                {
                    var issuesForOid = session != null
                        ? session.GetIssuesByObjectId(oid)
                        : (palCheckPipe.Instance.CurrentIssues.TryGetValue(oid, out var singleIssue) && singleIssue != null
                            ? (IReadOnlyList<CheckIssueItem>)new[] { singleIssue }
                            : Array.Empty<CheckIssueItem>());

                    foreach (CheckIssueItem issue in issuesForOid)
                    {
                        if (issue != null &&
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
