using process_pipeline.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace process_pipeline.Geometry
{
    /// <summary>
    /// 一维区间工具类。
    /// 
    /// 在本算法中，线段覆盖判断最终被转化成一维区间问题：
    /// 
    /// 例如 A 线段为 [0,1]，
    /// B1 覆盖 [0,0.3]，
    /// B2 覆盖 [0.3,0.8]，
    /// B3 覆盖 [0.9,1.0]。
    /// 
    /// 合并覆盖区间后为：
    /// [0,0.8], [0.9,1.0]
    /// 
    /// 则缺失区间为：
    /// [0.8,0.9]
    /// </summary>
    public static class IntervalUtils
    {
        /// <summary>
        /// 合并重叠或几乎相邻的区间。
        /// </summary>
        public static List<SegmentInterval> MergeIntervals(
            IEnumerable<SegmentInterval> intervals,
            double tolerance)
        {
            var sorted = intervals
                .Select(x => x.Normalize())
                .OrderBy(x => x.Start)
                .ToList();

            var result = new List<SegmentInterval>();

            if (sorted.Count == 0)
                return result;

            var current = sorted[0];

            for (int i = 1; i < sorted.Count; i++)
            {
                var next = sorted[i];

                // 如果下一个区间的起点小于当前区间终点 + 容差，
                // 认为它们是连续或重叠的，可以合并。
                if (next.Start <= current.End + tolerance)
                {
                    current.End = Math.Max(current.End, next.End);
                }
                else
                {
                    result.Add(current);
                    current = next;
                }
            }

            result.Add(current);
            return result;
        }

        /// <summary>
        /// 求 [0,1] 中没有被 covered 覆盖的部分。
        /// 
        /// covered 必须已经合并、排序。
        /// </summary>
        public static List<SegmentInterval> GetMissingIntervalsInUnit(
            IList<SegmentInterval> covered,
            double tolerance)
        {
            var result = new List<SegmentInterval>();

            double cursor = 0.0;

            foreach (var interval in covered)
            {
                if (interval.Start > cursor + tolerance)
                {
                    result.Add(new SegmentInterval(cursor, interval.Start));
                }

                cursor = Math.Max(cursor, interval.End);
            }

            if (cursor < 1.0 - tolerance)
            {
                result.Add(new SegmentInterval(cursor, 1.0));
            }

            return result;
        }

        /// <summary>
        /// 过滤过短区间。
        /// 
        /// 有些由于浮点误差产生的极短缺口没有业务意义，
        /// 可以用这个函数剔除。
        /// </summary>
        public static List<SegmentInterval> FilterTinyIntervals(
            IEnumerable<SegmentInterval> intervals,
            double minLengthInParameter)
        {
            return intervals
                .Where(x => Math.Abs(x.End - x.Start) >= minLengthInParameter)
                .ToList();
        }
    }
}
