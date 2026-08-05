using NetTopologySuite.Geometries;
using process_pipeline.Models;
using process_pipeline.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace process_pipeline.Geometry
{
    /// <summary>
    /// 线段几何计算工具类。
    /// 
    /// 这里故意不直接使用 NTS 的拓扑 Overlay，
    /// 而是自己实现：
    /// 1. 共线判断；
    /// 2. 投影；
    /// 3. 参数区间裁剪；
    /// 4. 参数转实际线段。
    /// 
    /// 原因：
    /// 这样可以更好地保留 CAD 原始对象的从属关系，
    /// 也更容易调试和解释结果。
    /// </summary>
    public static class CadMath
    {
        /// <summary>
        /// 判断两个线段是否近似共线。
        /// 
        /// 判断逻辑：
        /// 1. 方向向量叉积接近 0，说明两线段近似平行；
        /// 2. b.Start 到 a 所在直线距离小于容差，说明两条线落在同一条直线上。
        /// 
        /// 注意：
        /// 这里是“共线”判断，不是“重叠”判断。
        /// 是否重叠要后续通过一维投影区间判断。
        /// </summary>
        public static bool IsCollinear(CadSegment a, CadSegment b, double tolerance)
        {
            double ax = a.End.X - a.Start.X;
            double ay = a.End.Y - a.Start.Y;

            double bx = b.End.X - b.Start.X;
            double by = b.End.Y - b.Start.Y;

            double lenA = Math.Sqrt(ax * ax + ay * ay);
            double lenB = Math.Sqrt(bx * bx + by * by);

            if (lenA <= tolerance || lenB <= tolerance)
                return false;

            // 方向叉积。
            // 对二维向量 u=(x1,y1), v=(x2,y2)，叉积标量为 x1*y2 - y1*x2。
            double crossDir = ax * by - ay * bx;

            // 方向平行判断。
            // 这里使用 lenA * lenB 做尺度归一化。
            double parallelTolerance = tolerance * lenA * lenB;

            if (Math.Abs(crossDir) > parallelTolerance)
                return false;

            // 判断 b.Start 到 a 所在直线的距离。
            double cx = b.Start.X - a.Start.X;
            double cy = b.Start.Y - a.Start.Y;

            double crossPoint = ax * cy - ay * cx;
            double distance = Math.Abs(crossPoint) / lenA;

            return distance <= tolerance;
        }

        /// <summary>
        /// 将目标线段 b 投影到基准线段 a 的参数轴上。
        /// 
        /// a.Start 对应参数 0；
        /// a.End 对应参数 1。
        /// 
        /// 如果返回 [0.2, 0.8]，表示 b 覆盖了 a 的 20% 到 80% 范围。
        /// 
        /// 如果返回 [-0.5, 0.3]，表示 b 从 a 起点之前开始，到 a 的 30% 位置结束。
        /// 后续需要裁剪到 [0,1]。
        /// </summary>
        public static SegmentInterval ProjectSegmentToBase(CadSegment baseSeg, CadSegment targetSeg)
        {
            double dx = baseSeg.End.X - baseSeg.Start.X;
            double dy = baseSeg.End.Y - baseSeg.Start.Y;

            double len2 = dx * dx + dy * dy;

            double t0 = ProjectPointToBaseParameter(baseSeg.Start, dx, dy, len2, targetSeg.Start);
            double t1 = ProjectPointToBaseParameter(baseSeg.Start, dx, dy, len2, targetSeg.End);

            return new SegmentInterval(Math.Min(t0, t1), Math.Max(t0, t1));
        }

        /// <summary>
        /// 将一个点投影到基准线段的参数轴上。
        /// </summary>
        private static double ProjectPointToBaseParameter(
            Coordinate baseStart,
            double dx,
            double dy,
            double len2,
            Coordinate point)
        {
            double px = point.X - baseStart.X;
            double py = point.Y - baseStart.Y;

            return (px * dx + py * dy) / len2;
        }

        /// <summary>
        /// 判断区间是否和 [0,1] 有重叠。
        /// </summary>
        public static bool HasOverlapWithUnitInterval(SegmentInterval interval, double tolerance)
        {
            return interval.End >= 0.0 - tolerance && interval.Start <= 1.0 + tolerance;
        }

        /// <summary>
        /// 将区间裁剪到 [0,1] 内。
        /// </summary>
        public static SegmentInterval ClipToUnitInterval(SegmentInterval interval)
        {
            double s = Math.Max(0.0, interval.Start);
            double e = Math.Min(1.0, interval.End);
            return new SegmentInterval(s, e);
        }

        /// <summary>
        /// 根据基准线段和参数区间生成实际的 CadSegment。
        /// 
        /// 例如：
        /// baseSeg = P0 -> P1
        /// interval = [0.3, 0.6]
        /// 
        /// 则生成 P0-P1 上 30% 到 60% 的子线段。
        /// </summary>
        public static CadSegment CreateSubSegment(CadSegment baseSeg, SegmentInterval interval, GeometryFactory geometryFactory)
        {
            Coordinate s = Interpolate(baseSeg.Start, baseSeg.End, interval.Start);
            Coordinate e = Interpolate(baseSeg.Start, baseSeg.End, interval.End);

            return new CadSegment
            {
                Start = s,
                End = e,
                Geometry = geometryFactory.CreateLineString(new[] { s, e }),
                SourceHandles = GraphicManager.CloneHandles(baseSeg.SourceHandles),
                SourceSegmentIndex = baseSeg.SourceSegmentIndex,
                LayerName = baseSeg.LayerName,
                DatasetName = baseSeg.DatasetName
            };
        }

        /// <summary>
        /// 按参数 t 在线段上插值。
        /// 
        /// t = 0 返回 start；
        /// t = 1 返回 end；
        /// t = 0.5 返回中点。
        /// </summary>
        public static Coordinate Interpolate(Coordinate start, Coordinate end, double t)
        {
            double x = start.X + (end.X - start.X) * t;
            double y = start.Y + (end.Y - start.Y) * t;
            return new Coordinate(x, y);
        }

       public static ProjectionResultOnSegment ProjectPointToSegment(
            Coordinate p,
            Coordinate a,
            Coordinate b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;

            double len2 = dx * dx + dy * dy;

            if (len2 <= 0)
            {
                double d0 = p.Distance(a);

                return new ProjectionResultOnSegment
                {
                    T = 0,
                    Distance = d0,
                    Coordinate = new Coordinate(a)
                };
            }

            double t =
                ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;

            if (t < 0)
                t = 0;

            if (t > 1)
                t = 1;

            Coordinate projected = new Coordinate(
                a.X + t * dx,
                a.Y + t * dy);

            return new ProjectionResultOnSegment
            {
                T = t,
                Distance = p.Distance(projected),
                Coordinate = projected
            };
        }
    }
}
