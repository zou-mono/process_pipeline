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
    /// 表示一条 CAD 多段线。
    /// 
    /// 既可以表示原始 CAD polyline，
    /// 也可以表示拓扑合并后的 polyline。
    /// </summary>
    public class CadPolyline
    {
        /// <summary>
        /// 所有来源 CAD 对象 ID。
        /// 
        /// 对原始 CadPolyline：
        ///     通常只有一个 ObjectId。
        /// 
        /// 对合并后的 CadPolyline：
        ///     这里保存所有参与合并的原始 ObjectId。
        /// </summary>
        public List<ObjectId> SourceObjectIds { get; set; } = new List<ObjectId>();

        /// <summary>
        /// CAD 图层名。
        /// </summary>
        public string LayerName { get; set; }

        /// <summary>
        /// 数据集名称。
        /// 例如 base、target，或者某个业务数据名称。
        /// </summary>
        public string DatasetName { get; set; }

        /// <summary>
        /// NTS LineString 几何。
        /// </summary>
        public LineString Geometry { get; set; }

        /// <summary>
        /// 这条 polyline 拆出来的线段。
        /// </summary>
        public List<CadSegment> Segments { get; set; } = new List<CadSegment>();

        /// <summary>
        /// CAD 或业务属性。
        /// 例如 WIDTH、MATERIAL、TYPE 等。
        /// 
        /// 这里用 object 是为了同时支持 string、double、int 等。
        /// </summary>
        public Dictionary<string, object> Attributes { get; set; } =
            new Dictionary<string, object>();

        /// <summary>
        /// 如果当前 CadPolyline 是合并后的结果，
        /// 这里记录它由哪些原始 CadPolyline 合并而来。
        /// 
        /// 如果当前对象是原始 polyline，可以为空。
        /// </summary>
        public List<CadPolyline> SourcePolylines { get; set; } =
            new List<CadPolyline>();

        /// <summary>
        /// 几何长度。
        /// </summary>
        public double Length
        {
            get
            {
                if (Geometry != null)
                    return Geometry.Length;

                if (Segments != null)
                    return Segments.Sum(x => x.Length);

                return 0.0;
            }
        }

        /// <summary>
        /// 获取属性值。
        /// </summary>
        public object GetAttribute(string name)
        {
            if (Attributes != null && Attributes.TryGetValue(name, out var value))
                return value;

            return null;
        }

        /// <summary>
        /// 获取字符串属性。
        /// </summary>
        public string GetAttributeString(string name)
        {
            return GetAttribute(name)?.ToString();
        }

        /// <summary>
        /// 获取 double 属性。
        /// 如果属性原本是字符串，例如 "300"，也会尝试转换。
        /// </summary>
        public double? GetAttributeDouble(string name)
        {
            var value = GetAttribute(name);

            if (value == null)
                return null;

            if (value is double d)
                return d;

            if (value is int i)
                return i;

            if (double.TryParse(value.ToString(), out double parsed))
                return parsed;

            return null;
        }
    }
}
