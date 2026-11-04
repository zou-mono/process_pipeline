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

        [Description("已解决")]
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
                    return "已解决";
                default:
                    return Status.ToString();
            }
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
            string layerName = null)
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

