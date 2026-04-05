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
                //minDist = arrowPoint.DistanceTo(bestClosestPoint);
                minDist = arrowPoint.Distance2dTo(bestClosestPoint);
            }
            else if (ent is Polyline pl)
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
                    //double dist = arrowPoint.DistanceTo(poc.Point);
                    double dist = arrowPoint.Distance2dTo(poc.Point);

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


        /// <summary>
        /// 忽略 Z 轴，计算两个 3D 点在 XY 平面上的距离
        /// </summary>
        public static double Distance2dTo(this Point3d startPoint, Point3d endPoint)
        {
            return new Point2d(startPoint.X, startPoint.Y)
                    .GetDistanceTo(new Point2d(endPoint.X, endPoint.Y));
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
        /// 判断两个实体的包围盒是否相交（支持设置容差外扩）
        /// 纯 2D (X, Y) 平面计算，忽略 Z 轴高程差异
        /// </summary>
        /// <param name="ext1">第一个包围盒（例如：箭头的包围盒）</param>
        /// <param name="ext2">第二个包围盒（例如：管线的包围盒）</param>
        /// <param name="tolerance">容差（将 ext1 向外扩张的距离，默认为 0）</param>
        /// <returns>如果相交或包含，返回 true；否则返回 false</returns>
        public static bool IsIntersection2D(Entity ent1, Entity ent2, double tolerance = 0.0)
        {
            Extents3d ext1 = ent1.GeometricExtents;
            Extents3d ext2 = ent2.GeometricExtents;

            // 1. 计算 ext1（箭头）外扩后的边界
            double minX1 = ext1.MinPoint.X - tolerance;
            double minY1 = ext1.MinPoint.Y - tolerance;
            double maxX1 = ext1.MaxPoint.X + tolerance;
            double maxY1 = ext1.MaxPoint.Y + tolerance;

            // 2. 获取 ext2（管线）的边界
            double minX2 = ext2.MinPoint.X;
            double minY2 = ext2.MinPoint.Y;
            double maxX2 = ext2.MaxPoint.X;
            double maxY2 = ext2.MaxPoint.Y;

            // 3. 经典的 AABB 碰撞检测算法 (分离轴定理的简化版)
            // 如果 ext1 的右边界 < ext2 的左边界，说明 ext1 在 ext2 左侧，不可能相交
            // 如果 ext1 的左边界 > ext2 的右边界，说明 ext1 在 ext2 右侧，不可能相交
            // 同理判断上下边界。
            // 只有当这四个“不相交”的条件都不满足时，它们才是相交（或包含）的。
        
            bool isOutside = (maxX1 < minX2) || // 1在2左边
                             (minX1 > maxX2) || // 1在2右边
                             (maxY1 < minY2) || // 1在2下边
                             (minY1 > maxY2);   // 1在2上边

            return !isOutside; // 如果没有在外面，那就是相交或包含
        }

        /// <summary>
        /// 普适方法 2：点 vs 包围盒 (方法重载)
        /// 判断一个点是否与目标包围盒（外扩容差后）相交/包含
        /// </summary>
        /// <param name="pt">目标点（比如箭头的插入点、管线的端点等）</param>
        /// <param name="ext">目标包围盒（比如管线的包围盒）</param>
        /// <param name="tolerance">搜索半径/容差</param>
        public static bool IsIntersection2D(Point3d pt, Entity ent, double tolerance = 0.0)
        {
            try
            {
                // 1. 获取实体的包围盒（加上 try-catch 防止特殊实体报错）
                Extents3d ext = ent.GeometricExtents;

                // 2. 按照你的思路：直接把实体的包围盒向外扩张 tolerance
                double minX = ext.MinPoint.X - tolerance;
                double minY = ext.MinPoint.Y - tolerance;
                double maxX = ext.MaxPoint.X + tolerance;
                double maxY = ext.MaxPoint.Y + tolerance;

                // 3. 直接判断点 pt 的 X 和 Y 是否落在这个扩张后的矩形范围内
                bool isInside = (pt.X >= minX && pt.X <= maxX) &&
                                (pt.Y >= minY && pt.Y <= maxY);

                return isInside;
            }
            catch
            {
                // 如果实体没有有效的包围盒（获取 GeometricExtents 失败），直接认为不相交
                return false;
            }
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
