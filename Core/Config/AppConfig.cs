using System;
using System.Collections.Generic;
using System.Linq;

namespace process_pipeline.Core.Config
{
    /// <summary>
    /// 管线核查插件全局强类型分层配置模型（对应外部 Config.json）。
    /// 所有属性均提供标准默认值，确保即使外部配置文件缺失部分字段也能安全回退。
    /// </summary>
    public class AppConfig
    {
        /// <summary>
        /// 通用系统参数配置。
        /// </summary>
        public GeneralSettings General { get; set; } = new GeneralSettings();

        /// <summary>
        /// 各核查与工具命令的进度条/面板任务显示名称（替代原 Properties.Settings）。
        /// </summary>
        public TaskNameSettings TaskNames { get; set; } = new TaskNameSettings();

        /// <summary>
        /// 规划数据（本轮待核查维护的目标数据）图层、颜色优先级与标注图层配置。
        /// </summary>
        public PlanningDataSettings PlanningData { get; set; } = new PlanningDataSettings();

        /// <summary>
        /// 上版数据（上一轮成果参考数据，支持核查过程中编辑）图层与属性字段配置。
        /// </summary>
        public PreviousDataSettings PreviousData { get; set; } = new PreviousDataSettings();

        /// <summary>
        /// 勘测数据（实测现状管网只读基准数据）图层与属性字段配置。
        /// </summary>
        public SurveyDataSettings SurveyData { get; set; } = new SurveyDataSettings();

        /// <summary>
        /// 核心几何与核查算法阈值参数配置。
        /// </summary>
        public AlgorithmSettings Algorithms { get; set; } = new AlgorithmSettings();

        /// <summary>
        /// 多数据源核查路径记忆与自动嗅探策略配置。
        /// </summary>
        public DataSourceMemorySettings DataSources { get; set; } = new DataSourceMemorySettings();

        /// <summary>
        /// 创建一份完整的标准默认配置实例。
        /// </summary>
        public static AppConfig CreateDefault()
        {
            return new AppConfig();
        }
    }

    /// <summary>
    /// 通用系统参数配置。
    /// </summary>
    public class GeneralSettings
    {
        /// <summary>
        /// 选中实体时 CAD 夹点显示数量上限（对应系统变量 GRIPOBJLIMIT）。
        /// </summary>
        public short GripLimit { get; set; } = 500;

        /// <summary>
        /// 跨图纸预核查时，平面坐标范围（Extents3d）最小重叠比例阈值（0~1）。
        /// </summary>
        public double MinExtentOverlapRatio { get; set; } = 0.05;
    }

    /// <summary>
    /// 各任务的中文显示名称配置（彻底替代原 app.config 中的 Properties.Settings）。
    /// </summary>
    public class TaskNameSettings
    {
        public string FlowArrow { get; set; } = "管线与箭头匹配分析";
        public string ReversePolyline { get; set; } = "反转多段线";
        public string CheckPipe { get; set; } = "管线综合核查";
        public string CheckPlan { get; set; } = "规划数据自身合规核查";
        public string MatchGroup { get; set; } = "图属与Group绑定核查";
        public string MatchPipe { get; set; } = "规划与勘测管线匹配核查";
        public string CompareSegment { get; set; } = "规划与上版线段覆盖核查";
        public string SelectByHandle { get; set; } = "根据句柄值选择实体";
    }

    /// <summary>
    /// 规划数据单类管线的颜色与图层映射规则。
    /// 业务规则：颜色优先级高于图层名；若颜色与图层名规则不一致，列入待核查异常项。
    /// </summary>
    public class PlanPipeCategoryRule
    {
        /// <summary>
        /// 类别唯一标识（如 PlanNew / Existing / PlanExpand）。
        /// </summary>
        public string CategoryId { get; set; }

        /// <summary>
        /// 类别中文名称（如 规划新建管 / 现状管 / 规划改扩建管）。
        /// </summary>
        public string CategoryName { get; set; }

        /// <summary>
        /// 标准 CAD 颜色索引值（ACI：1=红，5=蓝，6=洋红）。
        /// </summary>
        public short ColorAci { get; set; }

        /// <summary>
        /// 标准 RGB 颜色文本（如 "255,0,0"）。
        /// </summary>
        public string ColorRgb { get; set; }

        /// <summary>
        /// 该类别允许的标准管线图层名称列表。
        /// </summary>
        public List<string> PipeLayers { get; set; } = new List<string>();

        /// <summary>
        /// 该类别关联的管径/规格文字标注图层名称列表。
        /// </summary>
        public List<string> TextLayers { get; set; } = new List<string>();
    }

    /// <summary>
    /// 规划数据图层与分类规则配置。
    /// </summary>
    public class PlanningDataSettings
    {
        /// <summary>
        /// 三类规划管线（规划新建管、现状管、规划改扩建管）的颜色与图层规则列表。
        /// </summary>
        public List<PlanPipeCategoryRule> Categories { get; set; } = new List<PlanPipeCategoryRule>
        {
            new PlanPipeCategoryRule
            {
                CategoryId = "PlanNew",
                CategoryName = "规划新建管",
                ColorAci = 1,
                ColorRgb = "255,0,0",
                PipeLayers = new List<string> { "3-污水管-2025新建", "3-污水压力管-规划新建" },
                TextLayers = new List<string> { "2-管线名称-2025" }
            },
            new PlanPipeCategoryRule
            {
                CategoryId = "Existing",
                CategoryName = "现状管",
                ColorAci = 5,
                ColorRgb = "0,0,255",
                PipeLayers = new List<string> { "3-污水管-现状", "3-污水压力管-现状" },
                TextLayers = new List<string> { "2-管线规格-现状" }
            },
            new PlanPipeCategoryRule
            {
                CategoryId = "PlanExpand",
                CategoryName = "规划改扩建管",
                ColorAci = 6,
                ColorRgb = "255,0,255",
                PipeLayers = new List<string> { "3-污水管-规划扩建" },
                TextLayers = new List<string> { "3-标注-管线标注-规划扩建" }
            }
        };

        /// <summary>
        /// 流向箭头所在的图层名称列表。
        /// </summary>
        public List<string> ArrowLayers { get; set; } = new List<string>
        {
            "3-标注-流向",
            "污水-流向"
        };

        /// <summary>
        /// 规划图纸中若残留的上版数据图层，在预核查时需列入待核查异常项交由人工处理。
        /// </summary>
        public List<string> LegacyWarningLayers { get; set; } = new List<string>
        {
            "0污水-已核实需新增",
            "0污水-属性",
            "0污水-待补测现状管",
            "0污水-规划新建",
            "0污水-规划改建"
        };

        /// <summary>
        /// 汇总获取所有标准规划管线图层集合（忽略大小写）。
        /// </summary>
        public HashSet<string> GetAllPipeLayers()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Categories != null)
            {
                foreach (var cat in Categories)
                {
                    if (cat?.PipeLayers == null) continue;
                    foreach (string layer in cat.PipeLayers)
                    {
                        if (!string.IsNullOrWhiteSpace(layer))
                            set.Add(layer.Trim());
                    }
                }
            }
            return set;
        }

        /// <summary>
        /// 汇总获取所有规划管径文字标注图层集合（忽略大小写）。
        /// </summary>
        public HashSet<string> GetAllTextLayers()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Categories != null)
            {
                foreach (var cat in Categories)
                {
                    if (cat?.TextLayers == null) continue;
                    foreach (string layer in cat.TextLayers)
                    {
                        if (!string.IsNullOrWhiteSpace(layer))
                            set.Add(layer.Trim());
                    }
                }
            }
            return set;
        }

        /// <summary>
        /// 获取流向箭头图层集合（忽略大小写）。
        /// </summary>
        public HashSet<string> GetArrowLayersSet()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ArrowLayers != null)
            {
                foreach (string layer in ArrowLayers)
                {
                    if (!string.IsNullOrWhiteSpace(layer))
                        set.Add(layer.Trim());
                }
            }
            return set;
        }
    }

    /// <summary>
    /// 上版数据图层与属性字段配置。
    /// </summary>
    public class PreviousDataSettings
    {
        /// <summary>
        /// 上版数据管线图形图层列表。
        /// </summary>
        public List<string> PipeLayers { get; set; } = new List<string>
        {
            "0污水-待补测现状管",
            "0污水-规划新建",
            "0污水-规划改建",
            "0污水-已核实需新增"
        };

        /// <summary>
        /// 上版数据属性块（ws 块）所在图层列表。
        /// </summary>
        public List<string> AttributeLayers { get; set; } = new List<string>
        {
            "0污水-属性"
        };

        /// <summary>
        /// 上版数据属性块的核心业务字段 Tag 列表（16 项标准字段）。
        /// </summary>
        public List<string> CoreAttributeTags { get; set; } = new List<string>
        {
            "QSBG", "MDBG", "QSMS", "MDMS", "PD", "LEN", "TYPE", "GHZT",
            "WIDTH", "HEITH", "YWIDTH", "YHEITH", "LX", "SJLY", "BZRQ", "BZH"
        };
    }

    /// <summary>
    /// 勘测数据图层与属性字段配置。
    /// </summary>
    public class SurveyDataSettings
    {
        /// <summary>
        /// 勘测数据管线图形图层列表。
        /// </summary>
        public List<string> PipeLayers { get; set; } = new List<string>
        {
            "勘测管"
        };

        /// <summary>
        /// 勘测数据属性块（1 块）所在图层列表。
        /// </summary>
        public List<string> AttributeLayers { get; set; } = new List<string>
        {
            "勘测管"
        };

        /// <summary>
        /// 勘测数据属性块的核心字段 Tag 列表。
        /// </summary>
        public List<string> CoreAttributeTags { get; set; } = new List<string>
        {
            "WIDTH", "HEITH"
        };
    }

    /// <summary>
    /// 各核心算法参数配置。
    /// </summary>
    public class AlgorithmSettings
    {
        /// <summary>
        /// 流向箭头匹配与核查参数。
        /// </summary>
        public ArrowMatchSettings ArrowMatch { get; set; } = new ArrowMatchSettings();

        /// <summary>
        /// 通用空间邻近配对（Group 图属配对 / 文字标注配对）参数。
        /// </summary>
        public SpatialMatchSettings SpatialMatch { get; set; } = new SpatialMatchSettings();

        /// <summary>
        /// 勘测管线拓扑合并（PolylineGraphMerger）参数。
        /// </summary>
        public PipeMergeSettings PipeMerge { get; set; } = new PipeMergeSettings();

        /// <summary>
        /// 跨图管线匹配（MatchPipe）与线段覆盖比较（CompareSegment）参数。
        /// </summary>
        public PipeCompareSettings PipeCompare { get; set; } = new PipeCompareSettings();
    }

    public class ArrowMatchSettings
    {
        /// <summary>
        /// 箭头与管线匹配的最大缓冲搜索距离（图纸单位：米）。
        /// </summary>
        public double MaxBufferDistance { get; set; } = 50.0;

        /// <summary>
        /// 箭头方向与管线走向允许的最大角度偏差（度）。
        /// </summary>
        public double AngleTolerance { get; set; } = 30.0;
    }

    public class SpatialMatchSettings
    {
        /// <summary>
        /// 附属块/文字直接贴合在管线曲线上的常规距离阈值（米）。
        /// </summary>
        public double CurveDistanceTolerance { get; set; } = 2.0;

        /// <summary>
        /// 针对 GIS 导出弯折管线，属性块位于多段线外包框中心（GeometricExtents Center）的匹配容差（米）。
        /// </summary>
        public double ExtentsCenterTolerance { get; set; } = 0.5;

        /// <summary>
        /// 空间邻近搜索的最大外扩半径（米）。
        /// </summary>
        public double MaxSearchDistance { get; set; } = 25.0;

        /// <summary>
        /// 属性块 LEN 字段与多段线实际几何长度校验的容差（米）。
        /// </summary>
        public double LengthTolerance { get; set; } = 1.5;

        /// <summary>
        /// 管线文字标注与管线走向平行校验的角度容差（度）。
        /// </summary>
        public double TextAngleTolerance { get; set; } = 35.0;

        /// <summary>
        /// 属性块中表示“管线长度”的候选字段名（Tag）优先级列表（支持不同图纸模板的字段别名，匹配到首个有效正数即生效）。
        /// </summary>
        public List<string> LengthAttributeAliases { get; set; } = new List<string>
        {
            "LEN", "LENGTH", "PIPE_LEN", "管长", "长度", "CD"
        };

        /// <summary>
        /// 属性块中表示“管径/断面宽度”的候选字段名（Tag）优先级列表。
        /// </summary>
        public List<string> WidthAttributeAliases { get; set; } = new List<string>
        {
            "WIDTH", "DN", "DIAMETER", "管径", "宽度", "GJ"
        };

        /// <summary>
        /// 属性块中表示“断面高度（渠箱高度）”的候选字段名（Tag）优先级列表（兼容 GIS 模板拼写 HEITH 与标准 HEIGHT）。
        /// </summary>
        public List<string> HeightAttributeAliases { get; set; } = new List<string>
        {
            "HEITH", "HEIGHT", "高度", "GD"
        };
    }

    public class PipeMergeSettings
    {
        /// <summary>
        /// 端点拓扑归并容差（米）。
        /// </summary>
        public double NodeTolerance { get; set; } = 0.01;

        /// <summary>
        /// 过滤极短多段线的最小长度阈值（米）。
        /// </summary>
        public double MinPolylineLength { get; set; } = 0.001;

        /// <summary>
        /// 拓扑合并时要求保持一致的属性字段名列表（默认按管径 WIDTH 分组合并）。
        /// </summary>
        public List<string> MergeAttributeNames { get; set; } = new List<string> { "WIDTH" };
    }

    public class PipeCompareSettings
    {
        /// <summary>
        /// CompareSegment 同源线段共线与覆盖容差（米）。
        /// </summary>
        public double SegmentCoverageTolerance { get; set; } = 0.1;

        /// <summary>
        /// 圆弧段（Bulge）折线化离散的最大段长（米）。
        /// </summary>
        public double ArcMaxSegmentLength { get; set; } = 1.0;
    }

    /// <summary>
    /// 跨文档核查数据源历史路径记忆与智能嗅探配置。
    /// </summary>
    public class DataSourceMemorySettings
    {
        /// <summary>
        /// 是否优先嗅探当前激活 DWG 内部的同名上版/勘测图层。
        /// </summary>
        public bool PreferSameDrawingLayers { get; set; } = true;

        /// <summary>
        /// 是否优先嗅探当前 AutoCAD 中已打开的其他 MDI 文档标签页。
        /// </summary>
        public bool PreferOpenMdiDocuments { get; set; } = true;

        /// <summary>
        /// 上一次绑定的【上版数据】外部 DWG 文件完整路径（自动持久化记忆）。
        /// </summary>
        public string LastPreviousDataDwgPath { get; set; } = string.Empty;

        /// <summary>
        /// 上一次绑定的【勘测数据】外部 DWG 文件完整路径（自动持久化记忆）。
        /// </summary>
        public string LastSurveyDataDwgPath { get; set; } = string.Empty;
    }
}
