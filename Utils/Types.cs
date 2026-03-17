using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace process_pipeline.Utils
{
    public class ProblemItem
    {
        public String PipeId { get; set; }  // 管线 ID
        public String ArrowId { get; set; }  // 匹配箭头 ID (可选)
        public string Description { get; set; }  // "无匹配箭头" 或 "方向不一致 (差值 XX°)"
        public Point3d Location { get; set; }  // 管线起点或最近点
    }
}
