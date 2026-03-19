using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices; // CAD应用程序核心
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.DatabaseServices;   // CAD数据库操作
using Autodesk.AutoCAD.EditorInput;       // 命令行交互
using Autodesk.AutoCAD.Geometry;          // 几何对象（点、线）
using Autodesk.AutoCAD.Runtime;
using process_pipeline.Core;           // 命令特性（关键）
using process_pipeline.Forms;
using process_pipeline.Util;
using process_pipeline.Utils;

namespace process_pipeline.Commands
{
    /// <summary>
    /// 管线相关命令（一个类封装所有管线业务命令）
    /// </summary>
    public class FlowArrowCommands : CadBase  // 继承Core层的基础类，复用上下文
    {
        double maxBufferDistance = 50.0;   // 距离阈值
        double angleTolerance = 30.0;      // 角度偏差阈值（度），e.g., ±45°
        string auxLayerName = "核查辅助线";

        [CommandMethod("MATCHARROW")]
        public void Execute()
        {
            // 1. 筛选箭头
            TypedValue[] arrowFilter = new TypedValue[]
            {
                new TypedValue((int)DxfCode.Start, "INSERT"),
                new TypedValue((int)DxfCode.BlockName, "jt-fy")
            };
            SelectionFilter arrowSelFilter = new SelectionFilter(arrowFilter);
            PromptSelectionResult arrowRes = Ed.SelectAll(arrowSelFilter);

            if (arrowRes.Status != PromptStatus.OK)
            {
                Ed.WriteMessage("\n没有找到箭头块！");
                return;
            }

            SelectionSet arrowSS = arrowRes.Value;

            // 2. 筛选管线（正确使用 <AND> + 两个 <OR>）
            TypedValue[] pipeFilter = new TypedValue[]
            {
                new TypedValue((int)DxfCode.Operator, "<AND"),
                    new TypedValue((int)DxfCode.Operator, "<OR"),
                        new TypedValue((int)DxfCode.Start, "LINE"),
                        new TypedValue((int)DxfCode.Start, "LWPOLYLINE"),
                        new TypedValue((int)DxfCode.Start, "POLYLINE"),
                    new TypedValue((int)DxfCode.Operator, "OR>"),
                    new TypedValue((int)DxfCode.Operator, "<OR"),
                        new TypedValue((int)DxfCode.LayerName, "3-污水管-2025新建"),
                        new TypedValue((int)DxfCode.LayerName, "3-污水管-规划扩建"),
                        new TypedValue((int)DxfCode.LayerName, "3-污水管-现状"),
                        new TypedValue((int)DxfCode.LayerName, "3-污水压力管-规划新建"),
                        new TypedValue((int)DxfCode.LayerName, "3-污水压力管-现状"),
                    new TypedValue((int)DxfCode.Operator, "OR>"),
                new TypedValue((int)DxfCode.Operator, "AND>")
            };
            SelectionFilter pipeSelFilter = new SelectionFilter(pipeFilter);
            PromptSelectionResult pipeRes = Ed.SelectAll(pipeSelFilter);

            if (pipeRes.Status != PromptStatus.OK)
            {
                Ed.WriteMessage("\n没有找到符合条件的管线！");
                return;
            }

            SelectionSet pipeSS = pipeRes.Value;

            // 3. 收集所有箭头的位置（ObjectId → Point3d）
            Dictionary<ObjectId, (Point3d Position, double Rotation)> arrowData = new Dictionary<ObjectId, (Point3d, double)>();
            using (Transaction tr = Db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in arrowSS.GetObjectIds())
                {
                    //BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br != null)
                    {
                        //double rotDeg = (br.Rotation * (180.0 / Math.PI) + 180.0) % 360.0;  //默认正西是起始，弧度转度，模360
                        double rotDeg = Geometry.ArrowAngle(br);
                        arrowData[id] = (br.Position, rotDeg);
                    }
                }
                tr.Commit();
            }

            Ed.WriteMessage($"\n找到箭头: {arrowData.Count} 个，管线: {pipeSS.Count} 条");

            List<ProblemItem> problems = RunChecker(pipeSS, arrowData);

            if (problems.Count == 0)
            {
                Doc.Editor.WriteMessage("\n检查通过，无问题。\n");
                return;
            }

            palCheckArrow.Instance.Show(problems);

            Ed.Regen();
            AcadApp.UpdateScreen();
        }

        public List<ProblemItem> RunChecker(SelectionSet pipeSS, Dictionary<ObjectId, (Point3d Position, double Rotation)> arrowData) { 
            var problems = new List<ProblemItem>();

            // 清空辅助线图层
            ClearLayerCommands cl = new ClearLayerCommands();
            cl.ClearLayer(auxLayerName);

            // 确保图层存在
            Utils.Database database = new Utils.Database();
            ObjectId auxlayerID = database.EnsureAuxLayer(auxLayerName);  // 辅助线图层ID

            // 4. 关联处理（遍历管线，找最近匹配箭头）
            int associatedCount = 0;
            using (Transaction tr = Db.TransactionManager.StartTransaction())
            {
                
                foreach (ObjectId pipeId in pipeSS.GetObjectIds())
                {
                    var obj = tr.GetObject(pipeId, OpenMode.ForRead);
                    string pipe_handle = obj.Handle.ToString(); // 如 "7B2A"
                    
                    if (pipe_handle == "5453")
                    {
                        Ed.WriteMessage($"\n管线 {pipe_handle}正在调试");
                    }

                    if (pipeId.IsErased || !pipeId.IsValid) continue;

                    Entity pipe_ent = tr.GetObject(pipeId, OpenMode.ForRead) as Entity;
                    if (pipe_ent == null) continue;

                    Extents3d ext = pipe_ent.GeometricExtents;
                    Point3d min = ext.MinPoint;
                    Point3d max = ext.MaxPoint;

                    // AABB 过滤候选箭头
                    List<ObjectId> candidates = new List<ObjectId>();
                    foreach (var kv in arrowData) {
                        Point3d p = kv.Value.Position;
                        if (p.X >= min.X - maxBufferDistance && p.X <= max.X + maxBufferDistance &&
                            p.Y >= min.Y - maxBufferDistance && p.Y <= max.Y + maxBufferDistance)
                        {
                            candidates.Add(kv.Key);
                        }
                    }

                    if (candidates.Count == 0) { 
                        problems.Add(new ProblemItem
                        {
                            PipeId = pipe_handle,
                            Type = ProblemType.NoAdjacentItems,
                            Level = ProblemLevel.Error,
                            Location = Geometry.RepresentativePoint(pipe_ent),
                            Description = "无匹配箭头（管线附近无任何箭头）"
                        });
                        continue;
                    }

                    double minDist = double.MaxValue;
                    //double pipeDir = 0.0;
                    double pipeSegAngle = double.MaxValue;
                    double dist = double.MaxValue;
                    double arrowRealAngle = double.MaxValue;
                    //double angleDiff = double.MaxValue;
                    //double realDiff = double.MaxValue;
                    Point3d ap;
                    Point3d closePoint;

                    // 收集所有“可能匹配”的箭头（方向在阈值内或接近180°）
                    //var possibleMatches = new List<(ObjectId Id, double Dist, Point3d closePoint, 
                    //    double SegAngle, double ArrowRot, bool IsReverse)>();

                    var possibleMatches = new List<MatchItem>();
                    MatchItem closestArrow = null;
                    
                    // 对每个候选计算距离
                    foreach (ObjectId aid in candidates) {
                        //pipeDir = Geometry.GetPipeDirection(ent);  // 获取管线局部方向（度）
                        (ap, arrowRealAngle) = arrowData[aid];

                        obj = tr.GetObject(aid, OpenMode.ForRead);
                        string arrow_handle = obj.Handle.ToString(); // 如 "7B2A"

                        // 计算到管线的最短距离（遍历段）
                        (dist, pipeSegAngle, closePoint) = Geometry.GetClosestSegmentInfo(pipe_ent, ap);
                        //double dist = Geometry.GetMinDistanceToPipe(ent, ap);

                        if (dist > maxBufferDistance || dist >= minDist) continue;

                        double diff = Math.Abs(arrowRealAngle - pipeSegAngle);
                        diff = Math.Min(diff, 360 - diff);

                        bool isNearSame = diff <= angleTolerance;   // 方向一致
                        bool isNearReverse = Math.Abs(diff - 180) <= angleTolerance;  // 方向相反

                        // 如果既不接近同向也不接近反向 → 忽略，不算匹配箭头，也不放入 problem
                        if (!isNearSame && !isNearReverse) continue;

                        MatchItem matchItem = new MatchItem(aid, dist, closePoint, pipeSegAngle, arrowRealAngle, isNearReverse);
                        possibleMatches.Add(matchItem);

                        if (dist < minDist) {
                            minDist = dist;
                            closestArrow = matchItem;
                        }
                    }

                    // 如果没有一个可能匹配的箭头
                    if (possibleMatches.Count == 0)
                    {
                        problems.Add(new ProblemItem
                        {
                            PipeId = pipe_handle,
                            Type = ProblemType.NoAdjacentItems,
                            Level = ProblemLevel.Error,
                            Location = Geometry.RepresentativePoint(pipe_ent),
                            Description = $"无匹配箭头（在管线{maxBufferDistance}米范围内没有找到符合方向的箭头）"
                        });
                        //return problems;
                    }
                    else {
                        int reverseCount = possibleMatches.Count(x => x.IsReverse);
                        int sameCount = possibleMatches.Count - reverseCount;

                        // 如果既有同向又有反向 → 冲突问题
                        if (reverseCount > 0 && sameCount > 0)
                        {
                            problems.Add(new ProblemItem
                            {
                                PipeId = pipe_handle,
                                Type = ProblemType.DirectionConflict,
                                Level = ProblemLevel.Error,
                                Location = Geometry.RepresentativePoint(pipe_ent),
                                Description = $"与管线关联的多个箭头方向冲突（同向 {sameCount} 个，反向 {reverseCount} 个）"
                            });
                        }
                        else if (reverseCount == possibleMatches.Count && reverseCount > 0) { 
                            if (closestArrow != null && minDist <= maxBufferDistance) {
                                if (closestArrow.IsReverse) { 
                                    problems.Add(new ProblemItem
                                    {
                                        PipeId = pipe_handle,
                                        Type = ProblemType.DirectionConflict,
                                        Level = ProblemLevel.Warning,
                                        Location = Geometry.RepresentativePoint(pipe_ent),
                                        Description = "管线和箭头方向不一致"
                                    });
                                    //Geometry.ReverseLineEntity(pipe_ent);
                                    //Ed.WriteMessage($"\n管线 {pipe_ent.Handle} 已自动反转方向（所有匹配箭头均为反向）\n");
                                    //problems.Add(new ProblemItem
                                    //{
                                    //    PipeId = pipe_handle,
                                    //    Type = ProblemType.OppositeDirection,
                                    //    Location = Geometry.RepresentativePoint(ent),
                                    //    Description = "管线和箭头方向不一致"
                                    //});
                                }
                            }          
                        }                        
                    }
                    
                    //else 
                    //{
                        //BlockTableRecord btr = (BlockTableRecord)tr.GetObject(Db.CurrentSpaceId, OpenMode.ForWrite);

                        //minDist = double.MaxValue;
                        //foreach (var match in possibleMatches)  // 或 possibleMatches，根据你最终筛选
                        //{
                        //    var aid = match.Id;
                        //    closePoint = match.closePoint;

                        //    if (match.Dist < minDist) { 
                                
                        //    }
                        //}
                        //foreach (var match in possibleMatches)  // 或 possibleMatches，根据你最终筛选
                        //{
                        //    var aid = match.Id;
                        //    //var (_ap, _arrowRot) = arrowData[aid];
                        //    closePoint = match.closePoint;
                        //    var (arrowPt, _) = arrowData[aid];

                            //    Line auxLine = new Line(arrowPt, closePoint);
                            //    auxLine.Layer = auxLayerName;
                            //    auxLine.ColorIndex = 1;  // 红色（可自定义）
                            //    auxLine.LineWeight = LineWeight.LineWeight018;  // 细线

                            //    btr.AppendEntity(auxLine);
                            //    tr.AddNewlyCreatedDBObject(auxLine, true);

                            //    // 日志（调试用，可删除）
                            //    Ed.WriteMessage($"\n添加辅助线：{arrowPt} → {closePoint}");
                            //}
                    //}



                    //// 全部反向 → 自动反转管线
                    //if (reverseCount == possibleMatches.Count && reverseCount > 0) {
                    //    if (!ent.IsDisposed)
                    //    {
                    //        Geometry.ReverseLineEntity(ent);
                    //        Ed.WriteMessage($"\n管线 {ent.Handle} 已自动反转方向（所有匹配箭头均为反向）\n");
                    //    }
                    //}
                }

                tr.Commit();
            }

            Ed.WriteMessage($"\n\n管线和箭头关联核查完成！共关联 {associatedCount} 条管线");

            return problems;
        } 
    }
}