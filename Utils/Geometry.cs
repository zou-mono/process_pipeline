using Autodesk.AutoCAD.ApplicationServices;
using System.Data.OleDb;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace process_pipeline.Utils
{
    public static class Geometry
    {
        // 获取管线整体方向（度）：用首尾点向量
        public static double GetPipeDirection(Entity ent)
        {
            Point3d start, end;
            if (ent is Line line)
            {
                start = line.StartPoint;
                end = line.EndPoint;
            }
            else if (ent is Autodesk.AutoCAD.DatabaseServices.Polyline pl)
            {
                start = pl.GetPoint3dAt(0);
                end = pl.GetPoint3dAt(pl.NumberOfVertices - 1);
            }
            else
            {
                return 0.0;  // 默认或抛异常
            }

            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            return (360 + Math.Atan2(dy, dx) * (180.0 / Math.PI)) % 360.0;
        }

        //矢量的角度，从正东开始，范围【0.360】
        public static double VectorAngle(Vector3d vec) {
            double SegAngle = 0.0;
            if (!vec.IsZeroLength())
            {
                double angleRad = Math.Atan2(vec.Y, vec.X);
                SegAngle = (angleRad * (180.0 / Math.PI) + 360.0) % 360.0; //确保角度在0-360之间
            }

            return SegAngle;
        }

        public static double ArrowAngle(BlockReference br) { 
            return (br.Rotation * (180.0 / Math.PI) + 180.0) % 360.0;
        }

        // 返回 (最小距离, 最近段的方向(度), 是否找到有效段)
        public static (double minDist, double segmentDirectionDeg, Point3d closestPoint) GetClosestSegmentInfo(Entity ent, Point3d arrowPoint)
        {
            double minDist = double.MaxValue;
            double bestSegAngle = double.MaxValue;
            Point3d bestClosestPoint = Point3d.Origin;

            if (ent is Line line)
            {
                Vector3d vec = line.EndPoint - line.StartPoint;
                bestSegAngle = VectorAngle(vec);
                bestClosestPoint = new LineSegment3d(line.StartPoint, line.EndPoint).GetClosestPointTo(arrowPoint).Point;
                minDist = arrowPoint.DistanceTo(bestClosestPoint);
            }
            else if (ent is Autodesk.AutoCAD.DatabaseServices.Polyline pl)
            {
                // 先用整条 Polyline 快速得到最近点和参数值
                //Curve3d curve3d = pl.GetGeCurve();
                //PointOnCurve3d globalPoc = curve3d.GetClosestPointTo(arrowPoint);  // false = 不延伸
                //minDist = arrowPoint.DistanceTo(globalPoc.Point);
                //Point3d globalClosest = pl.GetClosestPointTo(arrowPoint, false);

                //// 通过 globalPoc.Parameter 估算大概在哪一段
                //double param = globalPoc.Parameter;  // 参数值通常是 [0, NumberOfVertices-1] 范围
                //int subCurveIndex;
                //double localParam = globalPoc.GlobalToLocalParameter(param, out subCurveIndex);

                //int approxSegmentIndex = (int)Math.Floor(param);

                //// 然后只检查 approxSegmentIndex 及其相邻段（通常 1~3 段就够）
                //int startCheck = Math.Max(0, approxSegmentIndex - 1);
                //int endCheck = Math.Min(pl.NumberOfVertices - 2, approxSegmentIndex + 1);

                //for (int i = startCheck; i <= endCheck; i++)
                //{
                //    LineSegment3d seg = pl.GetLineSegmentAt(i);
                //    PointOnCurve3d poc = seg.GetClosestPointTo(arrowPoint);
                //    double dist = arrowPoint.DistanceTo(poc.Point);

                //    if (dist < minDist)
                //    {
                //        minDist = dist;
                //        Vector3d vec = seg.EndPoint - seg.StartPoint;
                //        double segAngleRad = Math.Atan2(vec.Y, vec.X);
                //        bestSegAngle = (segAngleRad * (180.0 / Math.PI) + 360.0) % 360.0;
                //    }
                //}

                for (int i = 0; i < pl.NumberOfVertices - 1; i++)
                {
                    LineSegment3d seg = pl.GetLineSegmentAt(i);
                    PointOnCurve3d poc = seg.GetClosestPointTo(arrowPoint);
                    double dist = arrowPoint.DistanceTo(poc.Point);

                    if (dist < minDist)
                    {
                        minDist = dist;

                        // 计算这一段的方向
                        Vector3d vec = seg.EndPoint - seg.StartPoint;
                        bestSegAngle = VectorAngle(vec);
                        bestClosestPoint = poc.Point;
                        //bestSegAngle = SegAngle * (180.0 / Math.PI) % 360.0;
                    }
                }
            }

            return (minDist, bestSegAngle, bestClosestPoint);
        }


        // 计算点到管线的最短距离（遍历所有段）
        public static double GetMinDistanceToPipe(Entity ent, Point3d point)
        {
            double minDist = double.MaxValue;

            if (ent is Line line)
            {
                LineSegment3d seg = new LineSegment3d(line.StartPoint, line.EndPoint);
                Point3d closest = seg.GetClosestPointTo(point).Point;
                minDist = point.DistanceTo(closest);
            }
            else if (ent is Autodesk.AutoCAD.DatabaseServices.Polyline pl)
            {
                for (int i = 0; i < pl.NumberOfVertices - 1; i++)
                {
                    LineSegment3d seg = pl.GetLineSegmentAt(i);
                    Point3d closest = seg.GetClosestPointTo(point).Point;
                    double dist = point.DistanceTo(closest);
                    if (dist < minDist) minDist = dist;
                }
            }
            // 可扩展其他类型

            return minDist;
        }

        public static void ZoomToExtents(this Editor ed, Extents3d ext, double margin = 1.15)
        {
            bool isValidExtents = !(ext.MinPoint == ext.MaxPoint);
            
            if (!isValidExtents) return;
            var view = ed.GetCurrentView();
            view.CenterPoint = new Point2d(
                (ext.MinPoint.X + ext.MaxPoint.X)/2,
                (ext.MinPoint.Y + ext.MaxPoint.Y)/2);
            view.Width  = (ext.MaxPoint.X - ext.MinPoint.X) * margin;
            view.Height = (ext.MaxPoint.Y - ext.MinPoint.Y) * margin;
            if (view.Width  < 1e-6) view.Width  = 1;
            if (view.Height < 1e-6) view.Height = 1;
            ed.SetCurrentView(view);
        }

        public static Point3d RepresentativePoint(Entity entity)
        {
            if (entity is BlockReference br) return br.Position;
            if (entity is Line line) 
                return new Point3d(
                    (line.StartPoint.X + line.EndPoint.X) / 2,
                    (line.StartPoint.Y + line.EndPoint.Y) / 2,
                    (line.StartPoint.Z + line.EndPoint.Z) / 2
            );
            if (entity is Polyline pl && pl.NumberOfVertices > 0) return pl.GetPoint3dAt(0);
            if (entity is DBText txt) return txt.Position;
            if (entity is MText mtxt) return mtxt.Location;
            if (entity is Circle c) return c.Center;
            if (entity is Arc a) return a.Center;

            // 兜底：包围盒中心
            var ext = entity.GeometricExtents;
            return new Point3d(
                (ext.MinPoint.X + ext.MaxPoint.X) / 2,
                (ext.MinPoint.Y + ext.MaxPoint.Y) / 2,
                (ext.MinPoint.Z + ext.MaxPoint.Z) / 2
            );
        }

        /// <summary>
        /// 反转直线或多段线（支持 Line、Polyline、LWPolyline）
        /// </summary>
        /// <param name="entity">要反转的实体（必须是 Line 或 Polyline 派生类）</param>
        /// <returns>是否成功反转</returns>
        public static bool ReverseLineEntity(Entity entity)
        {
            if (entity == null || entity.IsDisposed) return false;

            using (Transaction tr = entity.Database.TransactionManager.StartTransaction())
            {
                // 以写模式打开实体
                Entity originalEntity = tr.GetObject(entity.ObjectId, OpenMode.ForWrite) as Entity;
                if (originalEntity == null)
                {
                    tr.Commit();
                    return false;
                }

                if (originalEntity is Line originalLine)
                {
                    // Line：直接交换起点和终点
                    Point3d temp = originalLine.StartPoint;
                    originalLine.StartPoint = originalLine.EndPoint;
                    originalLine.EndPoint = temp;
                    tr.Commit();
                    return true;
                }
                else if (originalEntity is Polyline originalPline)  // 支持 Polyline 和 LWPOLYLINE
                {
                    int numVerts = originalPline.NumberOfVertices;
                    if (numVerts < 2) 
                    {
                        tr.Commit();
                        return false;
                    }

                    // 步骤1: 克隆原始 Polyline（自动复制所有属性：线型、线宽、颜色、PlotStyleName、XData 等）
                    Polyline clonedPline = originalEntity.Clone() as Polyline;
                    if (clonedPline == null)
                    {
                        tr.Commit();
                        return false;
                    }

                    // 步骤2: 收集原始顶点数据（位置、bulge、宽度）
                    var vertexData = new List<(Point2d Pt, double Bulge, double StartWidth, double EndWidth)>();
                    for (int i = 0; i < numVerts; i++)
                    {
                        Point3d p3d = clonedPline.GetPoint3dAt(i);  // 保留 Z 坐标
                        Point2d pt = new Point2d(p3d.X, p3d.Y);
                        double bulge = clonedPline.GetBulgeAt(i);
                        double startWidth = clonedPline.GetStartWidthAt(i);
                        double endWidth = clonedPline.GetEndWidthAt(i);
                        vertexData.Add((pt, bulge, startWidth, endWidth));
                    }

                    // 步骤3: 安全清空 - 移除到最小有效顶点数（避免 eDegenerateGeometry）
                    int minVerts = clonedPline.Closed ? 3 : 2;  // 闭合至少3，开放至少2
                    while (clonedPline.NumberOfVertices > minVerts)
                    {
                        clonedPline.RemoveVertexAt(clonedPline.NumberOfVertices - 1);  // 从末尾移除，安全
                    }

                    // 步骤4: 添加反转后的顶点（交换宽度，反转 bulge 符号）
                    for (int i = vertexData.Count - 1; i >= 0; i--)
                    {
                        var (pt, bulge, startWidth, endWidth) = vertexData[i];
                        clonedPline.AddVertexAt(clonedPline.NumberOfVertices, pt, -bulge, endWidth, startWidth);  // 支持变宽度
                    }

                    // 步骤5: 移除剩余的原始顶点（现在实体有足够顶点，不会退化）
                    for (int i = minVerts - 1; i >= 0; i--)
                    {
                        clonedPline.RemoveVertexAt(i);
                    }

                    // 步骤6: 恢复闭合状态（如果需要，其他属性已由 Clone 复制）
                    clonedPline.Closed = originalPline.Closed;

                    // 步骤7: 替换原始实体
                    BlockTableRecord btr = tr.GetObject(originalPline.BlockId, OpenMode.ForWrite) as BlockTableRecord;
                    btr.AppendEntity(clonedPline);
                    tr.AddNewlyCreatedDBObject(clonedPline, true);
                    originalPline.Erase();

                    tr.Commit();
                    return true;
                }

                tr.Abort();
                return false;
            }
        }
    }
}
