using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using NetTopologySuite.Geometries;
using process_pipeline.Core.DataSource;
using process_pipeline.Models;
using process_pipeline.Utils;

namespace process_pipeline.Core.PreCheck
{
    /// <summary>
    /// 预核查问题严重级别枚举（Stage 4 Commit 4.1）。
    /// </summary>
    public enum PreCheckSeverity
    {
        /// <summary>
        /// 提示信息（正常通过）。
        /// </summary>
        Info = 0,

        /// <summary>
        /// 待核查级问题（可转化为标准 <see cref="CheckIssueItem"/> 汇入主核查窗口供人工判读与定位）。
        /// </summary>
        Reviewable = 1,

        /// <summary>
        /// 阻断级问题（如目标图层缺失、有效实体为空、跨图平面坐标范围严重偏离，需弹窗阻断后续算法执行）。
        /// </summary>
        Blocking = 2
    }

    /// <summary>
    /// 规划数据自身合规核查问题细分类型（Stage 4 Commit 4.1）。
    /// </summary>
    public enum PlanIssueKind
    {
        /// <summary>
        /// 无异常（颜色与所属图层标准规则完全一致）。
        /// </summary>
        None = 0,

        /// <summary>
        /// 管线颜色与所属图层名规则冲突（含非标准颜色如绿色 ACI=3，或红/蓝/洋红跨层错放）。
        /// </summary>
        ColorLayerMismatch = 1,

        /// <summary>
        /// 退化几何图元（如长度为 0 的退化闭合多段线 Handle=184，或有效顶点数不足 2）。
        /// </summary>
        DegenerateGeometry = 2,

        /// <summary>
        /// 规划图纸中残留的旧版图层图元（如 0污水-已核实需新增 的多段线、0污水-属性 的 ws 属性块）。
        /// </summary>
        LegacyResidualEntity = 3
    }

    /// <summary>
    /// 单条数据预核查结果项（Stage 4 Commit 4.1）。
    /// </summary>
    public class PreCheckItem
    {
        /// <summary>
        /// 预核查规则编码（如 LAYER_MISSING / ENTITY_EMPTY / EXTENT_MISMATCH / PLAN_COLOR_LAYER_MISMATCH / PLAN_DEGENERATE_GEOMETRY / PLAN_LEGACY_RESIDUAL）。
        /// </summary>
        public string Code { get; set; } = string.Empty;

        /// <summary>
        /// 问题严重级别（阻断级 Blocking / 待核查级 Reviewable / 信息 Info）。
        /// </summary>
        public PreCheckSeverity Severity { get; set; } = PreCheckSeverity.Reviewable;

        /// <summary>
        /// 若属于规划数据自检问题，对应的细分类型。
        /// </summary>
        public PlanIssueKind PlanIssueKind { get; set; } = PlanIssueKind.None;

        /// <summary>
        /// 简短标题。
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// 详细问题描述（用于主核查面板展示或阻断弹窗说明）。
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// 关联的 CAD 实体 ObjectId（若为图纸/图层级阻断问题则为 ObjectId.Null）。
        /// </summary>
        public ObjectId TargetObjectId { get; set; } = ObjectId.Null;

        /// <summary>
        /// 关联的 CAD 实体十六进制句柄字符串。
        /// </summary>
        public string TargetHandle { get; set; } = string.Empty;

        /// <summary>
        /// 实体所在图层名称。
        /// </summary>
        public string LayerName { get; set; } = string.Empty;

        /// <summary>
        /// 实体有效 ACI 颜色索引值。
        /// </summary>
        public short EffectiveColorAci { get; set; }

        /// <summary>
        /// 实体几何定位点（供双击/单击居中缩放使用）。
        /// </summary>
        public Point3d Location { get; set; } = Point3d.Origin;

        /// <summary>
        /// 将待核查级预核查项转换为标准 <see cref="CheckIssueItem"/>，以便直接汇入 <see cref="DocumentCheckSession"/> 与主核查窗口。
        /// </summary>
        public CheckIssueItem ToCheckIssueItem(string ruleId = "CheckPlan", int subIndex = 0)
        {
            string handle = !string.IsNullOrWhiteSpace(TargetHandle)
                ? TargetHandle
                : (!TargetObjectId.IsNull && TargetObjectId.IsValid ? TargetObjectId.Handle.ToString() : "NULL");

            return new CheckIssueItem
            {
                IssueId = $"{ruleId}:{handle}:{subIndex}",
                RuleId = ruleId,
                RuleName = "规划数据自检",
                TargetObjectId = TargetObjectId,
                TargetHandle = handle,
                LayerName = LayerName,
                Status = IssueReviewStatus.Pending,
                Description = Description,
                Remark = Title ?? string.Empty,
                Location = Location,
                PossibleMatches = new List<MatchItem>()
            };
        }
    }

    /// <summary>
    /// 图层组存在性与有效实体数量校验规格（Stage 4 Commit 4.1）。
    /// </summary>
    public class LayerGroupRequirement
    {
        /// <summary>
        /// 图层组业务名称（如 "规划管线图层"、"流向箭头图层"）。
        /// </summary>
        public string GroupTitle { get; set; } = string.Empty;

        /// <summary>
        /// 候选图层名称列表（满足其中任意一个或多个图层存在有效实体即可）。
        /// </summary>
        public List<string> LayerNames { get; set; } = new List<string>();

        /// <summary>
        /// 允许的 DXF 实体类型名称列表（如 "LINE", "LWPOLYLINE", "POLYLINE" 或 "INSERT"；为空表示不限类型）。
        /// </summary>
        public List<string> AllowedDxfNames { get; set; } = new List<string>();

        /// <summary>
        /// 实体类型中文说明（用于阻断提示，如 "Line或者Polyline"、"BlockReference(块参照)"）。
        /// </summary>
        public string EntityTypeHint { get; set; } = string.Empty;

        /// <summary>
        /// 至少需要的有效实体数量（默认为 1）。
        /// </summary>
        public int MinRequiredEntityCount { get; set; } = 1;
    }

    /// <summary>
    /// 单个图层组的校验结果明细。
    /// </summary>
    public class LayerGroupCheckDetail
    {
        public LayerGroupRequirement Requirement { get; set; }
        public List<string> ExistingLayersInTable { get; set; } = new List<string>();
        public int ValidEntityCount { get; set; }
        public Dictionary<string, int> EntityCountByLayer { get; set; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public bool IsPassed =>
            Requirement == null || ValidEntityCount >= Requirement.MinRequiredEntityCount;
    }

    /// <summary>
    /// 跨图纸平面坐标范围（Extents3d / Envelope）对齐校验结果（Stage 4 Commit 4.1）。
    /// </summary>
    public class ExtentAlignmentResult
    {
        /// <summary>
        /// 主图纸（如规划数据）名称。
        /// </summary>
        public string PrimaryName { get; set; } = "规划数据";

        /// <summary>
        /// 参考图纸（如上版数据 / 勘测数据）名称。
        /// </summary>
        public string ReferenceName { get; set; } = "参考数据";

        /// <summary>
        /// 主图纸目标图层有效实体的平面外包框。
        /// </summary>
        public Envelope PrimaryEnvelope { get; set; }

        /// <summary>
        /// 参考图纸目标图层有效实体的平面外包框。
        /// </summary>
        public Envelope ReferenceEnvelope { get; set; }

        /// <summary>
        /// 两张图纸平面外包框的交集区域。
        /// </summary>
        public Envelope IntersectionEnvelope { get; set; }

        /// <summary>
        /// 主图纸参与范围统计的有效实体数。
        /// </summary>
        public int PrimaryEntityCount { get; set; }

        /// <summary>
        /// 参考图纸参与范围统计的有效实体数。
        /// </summary>
        public int ReferenceEntityCount { get; set; }

        /// <summary>
        /// 平面范围重叠比例（IntersectionArea / Min(PrimaryArea, ReferenceArea)，范围 0.0 ~ 1.0）。
        /// </summary>
        public double OverlapRatio { get; set; }

        /// <summary>
        /// 最小要求的重叠比例阈值（来自 Config.json General.MinExtentOverlapRatio，默认 0.05）。
        /// </summary>
        public double RequiredMinOverlapRatio { get; set; } = 0.05;

        /// <summary>
        /// 两个平面外包框中心点之间的欧氏距离（米）。
        /// </summary>
        public double CenterDistance { get; set; }

        /// <summary>
        /// 坐标范围是否对齐通过。
        /// </summary>
        public bool IsAligned { get; set; }

        /// <summary>
        /// 校验结果摘要说明。
        /// </summary>
        public string SummaryMessage { get; set; } = string.Empty;
    }

    /// <summary>
    /// 通用预核查汇总结果（包含阻断级问题与待核查级问题列表）。
    /// </summary>
    public class PreCheckSummary
    {
        /// <summary>
        /// 阻断级问题列表（非空时表示前置条件不满足，不可继续执行核心核查算法）。
        /// </summary>
        public List<PreCheckItem> BlockingItems { get; } = new List<PreCheckItem>();

        /// <summary>
        /// 待核查级问题列表（可转化为 CheckIssueItem 汇入核查面板）。
        /// </summary>
        public List<PreCheckItem> ReviewableItems { get; } = new List<PreCheckItem>();

        /// <summary>
        /// 图层组校验明细列表。
        /// </summary>
        public List<LayerGroupCheckDetail> LayerGroupDetails { get; } = new List<LayerGroupCheckDetail>();

        /// <summary>
        /// 跨图坐标范围对齐校验明细列表。
        /// </summary>
        public List<ExtentAlignmentResult> ExtentAlignmentDetails { get; } = new List<ExtentAlignmentResult>();

        /// <summary>
        /// 是否存在阻断级错误。
        /// </summary>
        public bool HasBlockingIssues => BlockingItems.Count > 0;

        /// <summary>
        /// 是否完全无阻断且无待核查异常。
        /// </summary>
        public bool IsClean => BlockingItems.Count == 0 && ReviewableItems.Count == 0;

        /// <summary>
        /// 格式化生成供 CadDialogService.ShowMessage 展示的阻断提示信息。
        /// </summary>
        public string FormatBlockingDialogMessage(string customTitle = null)
        {
            if (!HasBlockingIssues)
                return string.Empty;

            var sb = new StringBuilder();
            string title = !string.IsNullOrWhiteSpace(customTitle)
                ? customTitle
                : BlockingItems[0].Title;

            sb.Append($"[Title]{title}\n\n");
            foreach (PreCheckItem item in BlockingItems)
            {
                if (!string.IsNullOrWhiteSpace(item.Description))
                {
                    sb.AppendLine(item.Description);
                }
            }

            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// 规划数据自身合规核查完整报告（Stage 4 Commit 4.1）。
    /// 
    /// 涵盖：
    /// 1. 颜色优先 + 图层名双重校验结果（正常分类统计 + 颜色/图层不一致异常项，如 4 条绿色管线 139, 213, 241, 242）；
    /// 2. 退化几何核查结果（如长度为 0 的退化闭合线 Handle=184）；
    /// 3. 残留旧版图层图元核查结果（如 0污水-已核实需新增 的 3 条多段线 10D, 10F, 1A3 与 0污水-属性 的 3 个 ws 块 4EA, 517, 52C）；
    /// 4. 富化后的规划管线属性集合（EnrichedPipes）及可直接推送至主核查面板的 CheckIssueItem 字典（IssueMap）。
    /// </summary>
    public class PlanDataCheckReport
    {
        /// <summary>
        /// 规划管线图层上扫描到的曲线图元总数（含正常管线、颜色冲突管线及退化零长度线）。
        /// </summary>
        public int TotalScannedPlanCurves { get; set; }

        /// <summary>
        /// 非退化的有效规划管线总数（正常管线 + 颜色/图层不一致但几何有效的管线）。
        /// </summary>
        public int ValidPlanPipeCount { get; set; }

        /// <summary>
        /// 颜色与所属图层标准规则完全一致的正常规划管线数量。
        /// </summary>
        public int NormalCompliantPipeCount { get; set; }

        /// <summary>
        /// 按规划类别 ID（PlanNew / Existing / PlanExpand）统计的正常合规管线数量。
        /// </summary>
        public Dictionary<string, int> NormalCountByCategoryId { get; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 按规划图层名称统计的正常合规管线数量。
        /// </summary>
        public Dictionary<string, int> NormalCountByLayer { get; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 按“颜色优先级高于图层名”最终归类的有效管线数量（含颜色冲突管线）。
        /// </summary>
        public Dictionary<string, int> ResolvedCountByCategoryId { get; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 颜色与图层名不一致的待核查问题项列表（例如 3-污水管-现状 中的 4 条绿色管线 139, 213, 241, 242）。
        /// </summary>
        public List<PreCheckItem> ColorLayerMismatchItems { get; } = new List<PreCheckItem>();

        /// <summary>
        /// 退化几何（零长度/顶点数不足）的待核查问题项列表（例如 3-污水管-现状 中的零长度闭合线 Handle=184）。
        /// </summary>
        public List<PreCheckItem> DegenerateGeometryItems { get; } = new List<PreCheckItem>();

        /// <summary>
        /// 残留旧版图层（0污水-*）图元的待核查问题项列表（例如 0污水-已核实需新增 的 10D, 10F, 1A3 与 0污水-属性 的 4EA, 517, 52C）。
        /// </summary>
        public List<PreCheckItem> LegacyResidualItems { get; } = new List<PreCheckItem>();

        /// <summary>
        /// 汇总所有待核查预核查项（ColorLayerMismatch + DegenerateGeometry + LegacyResidual）。
        /// </summary>
        public IReadOnlyList<PreCheckItem> AllReviewableItems =>
            ColorLayerMismatchItems
                .Concat(DegenerateGeometryItems)
                .Concat(LegacyResidualItems)
                .ToList();

        /// <summary>
        /// 转化为标准 <see cref="CheckIssueItem"/> 的问题字典（ObjectId -> CheckIssueItem），可直接接入 ucMainCheckWindow。
        /// </summary>
        public Dictionary<ObjectId, CheckIssueItem> IssueMap { get; } =
            new Dictionary<ObjectId, CheckIssueItem>();

        /// <summary>
        /// 规划管线富化信息表（ObjectId -> EnrichedPlanPipeInfo），已同步写入 CATEGORY_ID 与 GHZT。
        /// </summary>
        public Dictionary<ObjectId, EnrichedPlanPipeInfo> EnrichedPipes { get; } =
            new Dictionary<ObjectId, EnrichedPlanPipeInfo>();
    }
}
