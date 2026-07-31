using NetTopologySuite.Geometries;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace process_pipeline.Models
{
    /// <summary>
    /// 单次方向性比较结果。
    /// 
    /// 例如：
    /// Compare(A, B) 表示：
    /// 检查 A 中哪些部分没有被 B 覆盖。
    /// 
    /// MissingSegments 就是 A 中未被 B 覆盖的线段。
    /// </summary>
    public class CompareResult
    {
        /// <summary>
        /// 基准数据集中未被目标数据集覆盖的线段。
        /// 
        /// Compare(A, B) 时：
        /// 表示 A 有、B 没有的部分。
        /// 
        /// Compare(B, A) 时：
        /// 表示 B 有、A 没有的部分，也就是 B 的多余部分。
        /// </summary>
        public List<CadSegment> MissingSegments { get; set; } = new List<CadSegment>();

        /// <summary>
        /// A 中被 B 覆盖的部分，也就是 A 和 B 的重叠线段
        /// </summary>
        public List<CadSegment> CoveredSegments { get; } = new List<CadSegment>();

        /// <summary>
        /// 基准数据集的原始线段总数。
        /// </summary>
        public int BaseSegmentCount { get; set; }

        /// <summary>
        /// 目标数据集的原始线段总数。
        /// </summary>
        public int TargetSegmentCount { get; set; }

        /// <summary>
        /// B 图层中的目标多段线。
        /// </summary>
        public CadPolyline TargetPolyline { get; set; }

        /// <summary>
        /// B 最匹配到的 merged A 基准多段线。
        /// 如果没有匹配，则为 null。
        /// </summary>
        public CadPolyline BestBasePolyline { get; set; }


        /// <summary>
        /// B 投影到 BestBasePolyline 上得到的里程区间。
        /// </summary>
        public SegmentInterval CoveredIntervalOnBase { get; set; }

        /// <summary>
        /// 是否找到有效匹配。
        /// </summary>
        public bool IsMatched => BestBasePolyline != null;

        /// <summary>
        /// 分数越小越好。
        /// </summary>
        public double Score { get; set; } = double.MaxValue;

        public double AverageDistance { get; set; } = double.MaxValue;

        public double MaxDistance { get; set; } = double.MaxValue;

        public double BadPointRatio { get; set; } = 1.0;

        public double CoveredLengthOnBase
        {
            get
            {
                if (CoveredIntervalOnBase == null)
                    return 0;

                return CoveredIntervalOnBase.Length;
            }
        }
    }

    public class ProjectionResult
    {
        public double Measure { get; set; }

        public double Distance { get; set; }

        public Coordinate Coordinate { get; set; }
    }

    public class ProjectionResultOnSegment
    {
        public double T { get; set; }

        public double Distance { get; set; }

        public Coordinate Coordinate { get; set; }
    }
}
