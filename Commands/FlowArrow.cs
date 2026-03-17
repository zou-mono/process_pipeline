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

                    //if (pipe_handle == "5228") { 
                    //    Ed.WriteMessage($"\n管线 {pipe_handle}正在调试");
                    //}

                    Entity ent = tr.GetObject(pipeId, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;

                    Extents3d ext = ent.GeometricExtents;
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
                            Location = Geometry.RepresentativePoint(ent),
                            Description = "无匹配箭头（管线附近无任何箭头）"
                        });
                        continue;
                    }

                    double minDist = double.MaxValue;
                    ObjectId closestArrow = ObjectId.Null;

                    //double pipeDir = 0.0;
                    double pipeSegAngle = double.MaxValue;
                    double dist = double.MaxValue;
                    double arrowRealAngle = double.MaxValue;
                    double angleDiff = double.MaxValue;
                    double realDiff = double.MaxValue;
                    Point3d ap;
                    Point3d closePoint;

                    // 收集所有“可能匹配”的箭头（方向在阈值内或接近180°）
                    var possibleMatches = new List<(ObjectId Id, double Dist, Point3d closePoint, 
                        double SegAngle, double ArrowRot, bool IsReverse)>();

                    // 对每个候选计算距离
                    foreach (ObjectId aid in candidates) {
                        //pipeDir = Geometry.GetPipeDirection(ent);  // 获取管线局部方向（度）
                        (ap, arrowRealAngle) = arrowData[aid];

                        obj = tr.GetObject(aid, OpenMode.ForRead);
                        string arrow_handle = obj.Handle.ToString(); // 如 "7B2A"

                        // 计算到管线的最短距离（遍历段）
                        (dist, pipeSegAngle, closePoint) = Geometry.GetClosestSegmentInfo(ent, ap);
                        //double dist = Geometry.GetMinDistanceToPipe(ent, ap);

                        if (dist > maxBufferDistance || dist >= minDist) continue;

                        double diff = Math.Abs(arrowRealAngle - pipeSegAngle);
                        diff = Math.Min(diff, 360 - diff);

                        bool isNearSame = diff <= angleTolerance;
                        bool isNearReverse = Math.Abs(diff - 180) <= angleTolerance;

                        // 如果既不接近同向也不接近反向 → 忽略，不算匹配箭头，也**不**放入 problem
                        if (!isNearSame && !isNearReverse) continue;

                        possibleMatches.Add((aid, dist, closePoint, pipeSegAngle, arrowRealAngle, isNearReverse));
                    }

                    // 如果没有一个可能匹配的箭头
                    if (possibleMatches.Count == 0)
                    {
                        problems.Add(new ProblemItem
                        {
                            PipeId = pipe_handle,
                            Location = Geometry.RepresentativePoint(ent),
                            Description = $"无匹配箭头（在{maxBufferDistance}米范围内的箭头方向不符）"
                        });
                        //return problems;
                    }
                    else 
                    {
                        //using (Transaction trDraw = Db.TransactionManager.StartTransaction())
                        //{
                        // 绘制直线
                        BlockTableRecord btr = (BlockTableRecord)tr.GetObject(Db.CurrentSpaceId, OpenMode.ForWrite);
                        //BlockTable bt = tr.GetObject(Db.BlockTableId, OpenMode.ForRead) as BlockTable;
                        //BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                        foreach (var match in possibleMatches)  // 或 possibleMatches，根据你最终筛选
                        {
                            var aid = match.Id;
                            //var (_ap, _arrowRot) = arrowData[aid];
                            closePoint = match.closePoint;
                            var (arrowPt, _) = arrowData[aid];
                            // 获取匹配的最近 segment 中点
                            //var (dist, segAngle, closestSegStart, closestSegEnd) = GetClosestSegmentInfo(ent, ap);  // 需实现这个方法

                            Line auxLine = new Line(arrowPt, closePoint);
                            auxLine.Layer = auxLayerName;
                            auxLine.ColorIndex = 1;  // 红色（可自定义）
                            auxLine.LineWeight = LineWeight.LineWeight018;  // 细线

                            btr.AppendEntity(auxLine);
                            tr.AddNewlyCreatedDBObject(auxLine, true);

                            // 日志（调试用，可删除）
                            Ed.WriteMessage($"\n添加辅助线：{arrowPt} → {closePoint}");
                        }

                        //tr.Commit();
                        //}

                        //Ed.Regen();
                        //AcadApp.UpdateScreen();
                    }

                    int reverseCount = possibleMatches.Count(x => x.IsReverse);
                    int sameCount = possibleMatches.Count - reverseCount;

                    // 如果既有同向又有反向 → 冲突问题
                    if (reverseCount > 0 && sameCount > 0) {
                        problems.Add(new ProblemItem
                        {
                            PipeId = pipe_handle,
                            Location = Geometry.RepresentativePoint(ent),
                            Description = $"与管线关联的多个箭头方向冲突（同向 {sameCount} 个，反向 {reverseCount} 个）"
                        });
                    }

                    // 全部反向 → 自动反转管线
                    if (reverseCount == possibleMatches.Count && reverseCount > 0) {
                        if (!ent.IsDisposed)
                        {
                            Geometry.ReverseLineEntity(ent);
                            Ed.WriteMessage($"\n管线 {ent.Handle} 已自动反转方向（所有匹配箭头均为反向）\n");
                        }
                    }
                    //if (closestArrow != ObjectId.Null) {
                    //    obj = tr.GetObject(closestArrow, OpenMode.ForRead);
                    //    string arrow_handle = obj.Handle.ToString(); // 如 "7B2A"
                    //    associatedCount++;

                    //    if ((180 - realDiff) < angleTolerance)
                    //    {
                    //        problems.Add(new ProblemItem { PipeId = pipe_handle, ArrowId = arrow_handle, Description = "箭头方向和管线绘制方向不一致", Location = Geometry.RepresentativePoint(ent) });
                    //    }
                    //    else 
                    //    {
                    //        Ed.WriteMessage($"\n管线 {pipe_handle} → 箭头 {arrow_handle} (距离 {minDist:F2}, 角度差 {angleDiff}°)");
                    //    }
                    //}
                    //else { 
                    //    Ed.WriteMessage($"\n管线 {pipe_handle}没有匹配到任何箭头！");
                    //    problems.Add(new ProblemItem {PipeId = pipe_handle, Description = "无匹配箭头", Location = Geometry.RepresentativePoint(ent) });
                    //}
                }

                tr.Commit();
            }

            Ed.WriteMessage($"\n\n管线和箭头关联核查完成！共关联 {associatedCount} 条管线");

            return problems;
        } 
    }
}