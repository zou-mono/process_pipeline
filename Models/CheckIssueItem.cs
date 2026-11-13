using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using process_pipeline.Utils;
using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace process_pipeline.Models
{
    /// <summary>
    /// 核查问题的人机交互状态枚举。
    /// 
    /// 设计意图：
    /// 原有的 ProblemItem 仅有一个布尔值 IsFixed，无法表达“人工确认忽略（误报）”、
    /// “人工已修改属性”、“待复核”等丰富的人机交互生命周期。
    /// 引入此状态机后，既能支撑主界面（ucMainCheckWindow）的“状态”列展示，
    /// 也能在局部增量刷新和 Undo/Redo 时准确还原人工判读结果。
    /// </summary>
    public enum IssueReviewStatus
    {
        [Description("待核查")]
        Pending = 0,

        [Description("已修改")]
        Modified = 1,

        [Description("待复核")]
        ToRecheck = 2,

        [Description("已忽略")]
        Ignored = 3,

        [Description("已修复")]
        Resolved = 4
    }

    /// <summary>
    /// 统一的管线核查问题领域模型。
    /// 
    /// 架构设计说明（Commit 1.1）：
    /// 1. 解决多算法模型割裂问题：
    ///    将早期的 ProblemItem（流向箭头专用）、CompareResult（管线对比专用）以及
    ///    ucMainCheckWindow 中的 PipeRowVm 字段需求统一收敛到本模型中。
    /// 2. 解决字典 Key 冲突问题：
    ///    不再强依赖单一管线 ObjectId 作为唯一键，而是通过 IssueId（规则ID + 实体Handle + 子序号）
    ///    允许同一根管线同时挂载多条不同规则的核查问题。
    /// 3. 兼顾运行时性能与跨会话/Undo安全性：
    ///    同时保留 TargetObjectId（用于当前图纸极速高亮/定位）和 TargetHandle（用于跨图对比、
    ///    持久化以及 CAD 原生 Undo 后 ObjectId 变化时的稳定追踪）。
    /// </summary>
    public class CheckIssueItem
    {
        // ============================================================
        // 1. 唯一标识与规则归属
        // ============================================================

        /// <summary>
        /// 问题全局唯一标识。
        /// 推荐格式："{RuleId}:{TargetHandle}:{SubIndex}"，例如 "FlowArrow:511D:0"。
        /// </summary>
        public string IssueId { get; set; }

        /// <summary>
        /// 产出该问题的核查规则标识。
        /// 例如："FlowArrow"（流向箭头核查）、"MatchPipe"（管线匹配核查）、"CompareSegment"（线段覆盖核查）。
        /// </summary>
        public string RuleId { get; set; }

        /// <summary>
        /// 产出该问题的核查规则中文名称（Commit 5.1）。
        /// 例如："流向箭头核查"、"管段匹配核查"、"管线标注核查"，用于主窗口左侧一级导航树及表格“核查类型”列展示。
        /// </summary>
        public string RuleName { get; set; }

        /// <summary>
        /// 问题分类/细分类型枚举（兼容原有的 ProblemType）。
        /// </summary>
        public ProblemType Type { get; set; }

        /// <summary>
        /// 问题严重等级（Error / Warning）。
        /// </summary>
        public ProblemLevel Level { get; set; } = ProblemLevel.Warning;

        // ============================================================
        // 2. CAD 实体定位信息
        // ============================================================

        /// <summary>
        /// 主管线在当前激活文档中的运行时 ObjectId。
        /// 注意：如果数据来源于后台 Side Database，此属性可能为 ObjectId.Null，此时应使用 TargetHandle。
        /// </summary>
        public ObjectId TargetObjectId { get; set; } = ObjectId.Null;

        /// <summary>
        /// 主管线的十六进制句柄字符串（如 "511D"）。
        /// 相比于内存级的 ObjectId，Handle 在图纸保存重开、跨图核查和 Undo/Redo 版本管理中保持稳定。
        /// </summary>
        public string TargetHandle { get; set; }

        /// <summary>
        /// 管线所属图层或业务分类名称（如 "给水"、"排水"、"3-污水管-现状"）。
        /// 对应主核查窗口（ucMainCheckWindow）左侧图层树节点与表格的“类型/图层”列。
        /// </summary>
        public string LayerName { get; set; }

        /// <summary>
        /// 关联的次要 CAD 实体 ID 列表（如流向核查中关联的一个或多个箭头块 ObjectId）。
        /// </summary>
        public List<ObjectId> RelatedObjectIds { get; set; } = new List<ObjectId>();

        /// <summary>
        /// 问题在 CAD 图面上的代表点坐标（用于双击缩放定位及坐标展示）。
        /// </summary>
        public Point3d Location { get; set; }

        /// <summary>
        /// 格式化后的二维代表点坐标文本（Commit 5.1，如 "12543.20, 38410.55"），供主窗口表格“位置坐标”列直接绑定。
        /// </summary>
        public string LocationText =>
            (Math.Abs(Location.X) < 1e-6 && Math.Abs(Location.Y) < 1e-6)
                ? "-"
                : $"{Location.X:F2}, {Location.Y:F2}";

        // ============================================================
        // 3. 业务属性与核查描述（对接主界面表格列）
        // ============================================================

        /// <summary>
        /// 管径（对应主窗口“管径”列，支持人工核查修改）。
        /// </summary>
        public double Diameter { get; set; }

        /// <summary>
        /// 离地高/高程（对应主窗口“离地高”列，支持人工核查修改）。
        /// </summary>
        public double Height { get; set; }

        /// <summary>
        /// 算法生成的客观问题描述（如 "无匹配箭头"、"管线和箭头方向不一致"）。
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// 备注信息（对应主窗口“备注”列）。
        /// 初始可填入算法简要提示，人机交互过程中允许人工直接编辑补充说明。
        /// </summary>
        public string Remark { get; set; }

        /// <summary>
        /// 针对该问题的推荐解决方案或修复操作说明（与客观问题描述分离，供悬停提示卡片展示）。
        /// </summary>
        public string Solution { get; set; }

        // ============================================================
        // 4. 人机交互状态与扩展载荷
        // ============================================================

        /// <summary>
        /// 当前核查问题的人机交互状态（默认：待核查 Pending）。
        /// </summary>
        public IssueReviewStatus Status { get; set; } = IssueReviewStatus.Pending;

        /// <summary>
        /// 问题是否已消除或无需继续作为异常展示（兼容旧版 ProblemItem.IsFixed 语义）。
        /// 当状态为 Resolved（已解决）或 Ignored（人工忽略）时，视为 true。
        /// </summary>
        public bool IsFixed
        {
            get => Status == IssueReviewStatus.Resolved || Status == IssueReviewStatus.Ignored;
            set
            {
                if (value)
                {
                    if (Status != IssueReviewStatus.Ignored)
                        Status = IssueReviewStatus.Resolved;
                }
                else
                {
                    if (Status == IssueReviewStatus.Resolved || Status == IssueReviewStatus.Ignored)
                        Status = IssueReviewStatus.Pending;
                }
            }
        }

        /// <summary>
        /// 候选匹配项集合（保留自 ProblemItem，供 GraphicManager.DrawAuxiliaryLines 绘制一对多瞬态辅助线）。
        /// </summary>
        public List<MatchItem> PossibleMatches { get; set; } = new List<MatchItem>();

        /// <summary>
        /// 规则专属扩展载荷。
        /// 例如：MatchPipe 规则可挂载 CompareResult，避免未来新增算法时频繁修改本基础类。
        /// </summary>
        public object RulePayload { get; set; }

        // ============================================================
        // 5. 辅助方法（状态展示、旧模型适配与 Undo 快照克隆）
        // ============================================================

        /// <summary>
        /// 获取当前 Status 对应的中文显示文本（如 "待核查"、"已修改"）。
        /// </summary>
        public string GetStatusDisplayText()
        {
            switch (Status)
            {
                case IssueReviewStatus.Pending:
                    return "待核查";
                case IssueReviewStatus.Modified:
                    return "已修改";
                case IssueReviewStatus.ToRecheck:
                    return "待复核";
                case IssueReviewStatus.Ignored:
                    return "已忽略";
                case IssueReviewStatus.Resolved:
                    return "已修复";
                default:
                    return Status.ToString();
            }
        }

        /// <summary>
        /// 获取当前问题所属核查类型的中文显示名称（Commit 5.1）。
        /// </summary>
        public string GetRuleDisplayName()
        {
            if (!string.IsNullOrWhiteSpace(RuleName))
                return RuleName;

            switch (RuleId)
            {
                case "FlowArrow":
                    return "流向箭头核查";
                case "MatchGroup":
                    return "图属匹配核查";
                case "CheckPlan":
                    return "规划数据自检";
                case "MatchPipe":
                    return "管段匹配核查";
                case "CompareSegment":
                    return "线段覆盖核查";
                default:
                    return !string.IsNullOrWhiteSpace(RuleId) ? RuleId : "常规管线核查";
            }
        }

        /// <summary>
        /// 获取当前问题的推荐解决方案/操作指南（若未显式指定 Solution，则按规则与问题状态自动推导）。
        /// 统一遵循软件帮助式描述规范：说明查看/定位方式、核查要点与可执行工具；无自动修复工具时明确提示“需人工核查和编辑”。
        /// </summary>
        public string GetEffectiveSolution()
        {
            if (Status == IssueReviewStatus.Resolved)
                return "该问题已完成修复归档，无需额外处理（可按 Ctrl+Z 撤销操作）";

            if (Status == IssueReviewStatus.Ignored)
                return "该问题已标记为忽略（可通过右键菜单恢复为待核查）";

            if (!string.IsNullOrWhiteSpace(Solution))
                return Solution.Trim();

            string desc = !string.IsNullOrWhiteSpace(Remark) ? Remark : (Description ?? string.Empty);

            if (string.Equals(RuleId, "MatchGroup", StringComparison.OrdinalIgnoreCase))
            {
                if (Status == IssueReviewStatus.ToRecheck)
                    return "点击行可查看配对连线，右键可一键绑定 Group 与 XData";

                if (desc.Contains("一管多标多余") || desc.Contains("一管多标"))
                    return "点击行可查看关联标注连线，需人工核查和编辑（核实沿线标注规格，按需打断管线或清理重复标注）";

                if (desc.Contains("多段共标"))
                    return "点击行可查看邻近共用标注连线，需人工核查和编辑（确认相邻管段是否共用该规格，按需补充标注或合并管段）";

                if (desc.Contains("不规范"))
                    return "点击行可查看配对连线，需人工核查和编辑标注中的管径与根数，确认无误后右键可一键绑定 Group 与 XData";

                if (desc.Contains("无配对"))
                    return "双击行可定位到该管段，需人工核查和编辑（在管线旁补充对应的属性块或管径文字标注）";

                if (desc.Contains("Group") && (desc.Contains("结构异常") || desc.Contains("重复引用")))
                    return "双击行可定位到异常编组图元，需人工核查和编辑（解组异常 Group 后再重新执行 1:1 绑定）";

                return "点击行可查看候选连线，需人工核查和编辑（调整属性要素位置消除歧义，或确认后右键一键绑定 Group 与 XData）";
            }

            if (string.Equals(RuleId, "FlowArrow", StringComparison.OrdinalIgnoreCase))
            {
                if (Type == ProblemType.DirectionConflict || Type == ProblemType.OneToMany)
                    return "点击行可查看关联流向箭头，右键可一键翻转管线流向";

                if (Type == ProblemType.NoAdjacentItems)
                    return "双击行可定位到该管段，需人工核查和编辑（在管线邻近位置补充绘制流向箭头）";

                return "点击行可查看管线与流向箭头，需人工核查和编辑箭头角度，或右键一键翻转管线流向";
            }

            if (string.Equals(RuleId, "CheckPlan", StringComparison.OrdinalIgnoreCase))
            {
                if (desc.Contains("退化") || desc.Contains("零长度"))
                    return "双击行可定位到该退化图元，需人工核查和编辑（确认后在图面直接删除清理）";

                if (desc.Contains("颜色与图层不一致"))
                    return "双击行可定位到该管段，需人工核查和编辑（将管线移至匹配图层或将颜色改为随层 ByLayer）";

                if (desc.Contains("残留旧版图层"))
                    return "双击行可定位到该图元，需人工核查和编辑（确认是否迁移至标准规划图层或直接清理）";

                return "双击行可定位到该图元，需人工核查和编辑（按规划图层与颜色规范修正属性）";
            }

            if (string.Equals(RuleId, "MatchPipe", StringComparison.OrdinalIgnoreCase))
            {
                return "双击行可定位到对应管段，需人工核查和编辑（核对勘测管径与规划管径文字标注并修正）";
            }

            if (string.Equals(RuleId, "CompareSegment", StringComparison.OrdinalIgnoreCase))
            {
                return "双击行可定位到偏移或缺失位置，需人工核查和编辑（按需调整规划管线走向，或右键标记忽略）";
            }

            return "双击行可定位到目标图元，需人工核查和编辑图面数据，或通过右键菜单更新状态";
        }

        /// <summary>
        /// 创建当前问题项的浅表状态快照。
        /// 用于后续 Undo/Redo 管理器在人工修改属性或状态前保存历史版本。
        /// </summary>
        public CheckIssueItem CloneSnapshot()
        {
            return new CheckIssueItem
            {
                IssueId = this.IssueId,
                RuleId = this.RuleId,
                RuleName = this.RuleName,
                Type = this.Type,
                Level = this.Level,
                TargetObjectId = this.TargetObjectId,
                TargetHandle = this.TargetHandle,
                LayerName = this.LayerName,
                RelatedObjectIds = this.RelatedObjectIds != null
                    ? new List<ObjectId>(this.RelatedObjectIds)
                    : new List<ObjectId>(),
                Location = this.Location,
                Diameter = this.Diameter,
                Height = this.Height,
                Description = this.Description,
                Remark = this.Remark,
                Solution = this.Solution,
                Status = this.Status,
                PossibleMatches = this.PossibleMatches != null
                    ? new List<MatchItem>(this.PossibleMatches)
                    : new List<MatchItem>(),
                RulePayload = this.RulePayload
            };
        }

        /// <summary>
        /// 适配器方法：将现有的 ProblemItem 无损转换为统一的 CheckIssueItem。
        /// 用于在过渡期保证现有 FlowArrowService 与新架构平滑衔接。
        /// </summary>
        public static CheckIssueItem FromProblemItem(
            ProblemItem problem,
            string ruleId = "FlowArrow",
            string layerName = null,
            string ruleName = "流向箭头核查")
        {
            if (problem == null)
                return null;

            string handleStr = (!problem.PipeId.IsNull && problem.PipeId.IsValid)
                ? problem.PipeId.Handle.ToString()
                : "UNKNOWN";

            var relatedIds = new List<ObjectId>();
            if (!problem.ArrowId.IsNull && problem.ArrowId.IsValid)
            {
                relatedIds.Add(problem.ArrowId);
            }

            return new CheckIssueItem
            {
                IssueId = $"{ruleId}:{handleStr}:0",
                RuleId = ruleId,
                RuleName = ruleName,
                Type = problem.Type,
                Level = problem.Level,
                TargetObjectId = problem.PipeId,
                TargetHandle = handleStr,
                LayerName = layerName ?? string.Empty,
                RelatedObjectIds = relatedIds,
                Location = problem.Location,
                Description = problem.Description,
                Remark = problem.Description,
                Status = problem.IsFixed ? IssueReviewStatus.Resolved : IssueReviewStatus.Pending,
                PossibleMatches = problem.PossibleMatches != null
                    ? new List<MatchItem>(problem.PossibleMatches)
                    : new List<MatchItem>()
            };
        }
    }
}

