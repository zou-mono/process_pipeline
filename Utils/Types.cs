using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using process_pipeline.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices; // CAD应用程序核心
using AcadDb = Autodesk.AutoCAD.DatabaseServices;
using System.ComponentModel;

namespace process_pipeline.Utils
{
    public class MatchItem
    {
        // 字段/属性和原元组一一对应
        public ObjectId Id { get; set; }
        public Point3d Position {get; set;}
        public double Dist { get; set; }
        public Point3d closePoint { get; set; }
        public double SegAngle { get; set; }
        public double ArrowRot { get; set; }
        public bool IsReverse { get; set; }

        // 可选：构造函数（简化初始化）
        public MatchItem(ObjectId id, Point3d position, double dist, Point3d closePoint, double segAngle, double arrowRot, bool isReverse)
        {
            Id = id;
            Position = position;
            Dist = dist;
            this.closePoint = closePoint;
            SegAngle = segAngle;
            ArrowRot = arrowRot;
            IsReverse = isReverse;
        }
    }

    public class ProblemItem
    {
        //public static readonly Dictionary<ProblemType, string> Problem = new Dictionary<ProblemType, string>()
        //{
        //    { ProblemType.OneToMany, "与管线关联的多个箭头方向" },
        //    { ProblemType.NoAdjacentItems, "未检测到当前要素有任何邻近的要素" },
        //    { ProblemType.NoAdjacentItems, "当前要素仅存在部分邻近的关联要素（未完全匹配）" }
        //};

        public ObjectId PipeId { get; set; }  // 管线 ID
        public ProblemLevel Level { get; set; }  // 问题等级
        public ProblemType Type { get; set; }  // 问题类型
        public ObjectId ArrowId { get; set; }  // 匹配箭头 ID (可选)
        public string Description { get; set; }  // "无匹配箭头" 或 "方向不一致 (差值 XX°)"
        public Point3d Location { get; set; }  // 管线起点或最近点
        public bool IsFixed { get; set; } = true;
        public List<MatchItem> PossibleMatches { get; set; }
    }

    public class ProblemItemViewModel
    {
        public int NO { get; set; }
        public bool IsFixed { get; set; }
        public string PipeId { get; set; }
        public string Location { get; set; }
        public string Description { get; set; }
        public ProblemItem OriginalItem { get; set; } // 存储原始对象

        public ProblemItemViewModel()
        {
        }

        public ProblemItemViewModel(int no, ProblemItem item)
        {
            NO = no;
            OriginalItem = item;
            IsFixed = item.IsFixed;
            Description = item.Description;
    
            // CAD ObjectId 转字符串 (通常显示 Handle 句柄比较直观)
            PipeId = item.PipeId.IsNull ? "null" : item.PipeId.Handle.ToString();
    
            // CAD Point3d 转字符串 (保留两位小数)
            Location = $"{item.Location.X:F2}, {item.Location.Y:F2}";
        }
    }

    public enum ProblemType {
        [Description("与管线关联的多个箭头方向冲突（同向 {0} 个，反向 {1} 个）")]
        OneToMany,  // 一对多，比如一根管线找到了多个匹配的箭头

        [Description("无匹配箭头（在管线{0}米范围内没有找到符合方向的箭头）")]
        NoAdjacentItems, // 没有邻近的要素

        [Description("管线和箭头方向不一致")]
        DirectionConflict
    }
    public enum ProblemLevel {
        Error,  // 明显错误
        Warning  // 警告
    }

    public class ArrowCacheInfo
    {
        public Point3d Position { get; set; }
        public double Rotation { get; set; }
        // 如果需要包围盒，也可以加进来：public Extents3d Extents { get; set; }
    }
}
