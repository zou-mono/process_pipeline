using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace process_pipeline.Geometry
{
    /// <summary>
    /// Polyline 拓扑合并参数。
    /// </summary>
    public class PolylineMergeOptions
    {
        /// <summary>
        /// 端点归并容差。
        /// 
        /// 作用：
        /// 判断两个 polyline 的端点是否代表同一个拓扑节点。
        /// 
        /// 例如：
        /// NodeTolerance = 0.01
        /// 表示两个端点距离小于等于 0.01 时，认为它们是同一个节点。
        /// 
        /// 注意：
        /// 这个值通常应该比较小。
        /// 它不是 A/B 匹配时允许的几米偏差。
        /// 它只是判断 B 图层自身端点是否连接。
        /// </summary>
        public double NodeTolerance { get; set; } = 0.01;

        /// <summary>
        /// 用于分组合并的属性名。
        /// 
        /// 例如：
        /// MergeAttributeNames = { "WIDTH" }
        /// 表示只有 WIDTH 相同的 polyline 才能合并。
        /// 
        /// 如果有多个属性：
        /// { "WIDTH", "MATERIAL" }
        /// 则 WIDTH 和 MATERIAL 都相同才会进入同一个合并组。
        /// </summary>
        public List<string> MergeAttributeNames { get; set; } =
            new List<string> { "WIDTH" };

        /// <summary>
        /// 是否要求 LayerName 相同才能合并。
        /// 
        /// 如果你的输入本来就是同一个 B 图层，可以设为 true 或 false 都可以。
        /// 如果输入可能来自多个图层，建议设为 true。
        /// </summary>
        public bool RequireSameLayer { get; set; } = true;

        /// <summary>
        /// 是否合并闭环。
        /// 
        /// 闭环的特点是：
        /// 一个连通分量内所有节点 degree 都等于 2。
        /// 
        /// 如果设为 true，会把这种环形链也合并成一条闭合 polyline。
        /// </summary>
        public bool MergeClosedLoops { get; set; } = true;

        /// <summary>
        /// 创建合并后 polyline 时，是否移除相邻重复点。
        /// 
        /// 一般建议 true。
        /// </summary>
        public bool RemoveDuplicateConsecutivePoints { get; set; } = true;

        /// <summary>
        /// 过短的 polyline 不参与合并。
        /// 
        /// 例如：
        /// MinPolylineLength = 0.001
        /// 可以过滤 CAD 中几乎为零长度的脏数据。
        /// </summary>
        public double MinPolylineLength { get; set; } = 0.001;
    }

    public class PolylineMatchOptions
    {
        /// <summary>
        /// 空间容差。
        /// </summary>
        public double Tolerance { get; set; } = 5;

        /// <summary>
        /// STRtree 查询扩张距离。
        /// 如果小于等于 0，则使用 Tolerance。
        /// </summary>
        public double CandidateSearchExpand { get; set; } = -1;

        /// <summary>
        /// 采样间距。
        /// </summary>
        public double SampleStep { get; set; } = 1.0;

        /// <summary>
        /// 最大坏点比例。
        /// 坏点指 B 采样点到 A 距离超过 Tolerance 的点。
        /// </summary>
        public double MaxBadPointRatio { get; set; } = 0.2;

        /// <summary>
        /// 平均距离上限。
        /// </summary>
        public double MaxAverageDistance { get; set; } = 5;

        /// <summary>
        /// 最大距离上限。
        /// 可选控制。
        /// </summary>
        public double MaxDistance { get; set; } = 5;

        /// <summary>
        /// 最小投影区间长度。
        /// </summary>
        public double MinCoveredLength { get; set; } = 0.01;

        /// <summary>
        /// Buffer 粗筛的最小重叠比例。
        /// </summary>
        public double MinBufferOverlapRatio { get; set; } = 0.3;

        /// <summary>
        /// 是否启用 Buffer 粗筛。
        /// </summary>
        public bool UseBufferRoughFilter { get; set; } = true;

        public double MinProjectedLength { get; set; } = 0.01;
    }
}
