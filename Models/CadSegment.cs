using Autodesk.AutoCAD.DatabaseServices;
using NetTopologySuite.Geometries;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace process_pipeline.Models
{
    /// <summary>
    /// CAD 线段模型。
    /// 
    /// 注意：
    /// 这里的 CadSegment 是我们自己定义的“基础线段”。
    /// 它不是 AutoCAD 的 Line，也不是 Polyline。
    /// 
    /// 它的作用是：
    /// 1. 表示从 CAD Entity 中拆出来的一段直线；
    /// 2. 保留原始 CAD 对象的 ObjectId；
    /// 3. 保留它来自原 Polyline 的第几个 segment；
    /// 4. 提供 NetTopologySuite 的 LineString，用于空间索引；
    /// 5. 提供 Start / End，用于我们自己的投影算法。
    /// </summary>
    public class CadSegment
    {
        /// <summary>
        /// 起点，使用 NTS 的 Coordinate。
        /// 这里只处理二维 X/Y。
        /// </summary>
        public Coordinate Start { get; set; }

        /// <summary>
        /// 终点。
        /// </summary>
        public Coordinate End { get; set; }

        /// <summary>
        /// 对应的 NTS LineString。
        /// 主要用于：
        /// 1. 获取 Envelope；
        /// 2. 放入 STRtree 空间索引。
        /// </summary>
        public LineString Geometry { get; set; }

        /// <summary>
        /// 当前 segment 来源的 CAD 对象 ID 集合。
        /// 
        /// 对原始 CAD polyline 拆出的 segment：
        ///     一般只有一个 ObjectId。
        /// 
        /// 对合并、截取、比较结果生成的 segment：
        ///     可能包含多个 ObjectId。
        /// </summary>
        public List<ObjectId> SourceObjectIds { get; set; } = new List<ObjectId>();

        /// <summary>
        /// 来源实体的 Handle 字符串。
        /// 对 side database 更有用。
        /// </summary>
        public string SourceHandle { get; set; }

        /// <summary>
        /// 来源 DWG 文件路径。
        /// 当前图可以为空或填写当前图路径。
        /// </summary>
        public string SourceFilePath { get; set; }

        /// <summary>
        /// 是否来自 Side Database。
        /// </summary>
        public bool IsFromSideDatabase { get; set; }

        /// <summary>
        /// 该线段在原始 Entity 中的段号。
        /// 对 Polyline 有意义。
        /// 对 Line 可以设为 0。
        /// </summary>
        public int SourceSegmentIndex { get; set; }

        /// <summary>
        /// 原始图层名。
        /// </summary>
        public string LayerName { get; set; }

        /// <summary>
        /// 数据集名称。
        /// 可设为 A 或 B。
        /// </summary>
        public string DatasetName { get; set; }

        /// <summary>
        /// 线段长度。
        /// </summary>
        public double Length
        {
            get
            {
                double dx = End.X - Start.X;
                double dy = End.Y - Start.Y;
                return System.Math.Sqrt(dx * dx + dy * dy);
            }
        }
    }
}
