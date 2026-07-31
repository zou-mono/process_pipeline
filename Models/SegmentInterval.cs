using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace process_pipeline.Models
{
    /// <summary>
    /// 一维参数区间。
    /// 
    /// 对一条 A 线段，我们把它参数化为：
    /// Start 点 = 0
    /// End 点 = 1
    /// 
    /// 如果某条 B 线段覆盖了 A 的中间部分，
    /// 例如从 30% 到 80%，则表示为：
    /// [0.3, 0.8]
    /// </summary>
    public class SegmentInterval
    {
        public double Start;
        public double End;
        public double Length => End - Start;

        public SegmentInterval(double start, double end)
        {
            Start = start;
            End = end;
        }

        /// <summary>
        /// 返回归一化区间，确保 Start <= End。
        /// </summary>
        public SegmentInterval Normalize()
        {
            if (Start <= End)
                return this;

            return new SegmentInterval(End, Start);
        }

        public override string ToString()
        {
            return $"[{Start}, {End}]";
        }
    }
}
