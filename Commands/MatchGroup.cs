using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using process_pipeline.Core;
using process_pipeline.Forms;
using process_pipeline.Geometry;
using process_pipeline.Models;
using process_pipeline.Utils;
using AcadDb = Autodesk.AutoCAD.DatabaseServices;

namespace process_pipeline.Commands
{
    /// <summary>
    /// 单文档图属与 CAD Group 绑定核查命令入口（Stage 3 Commit 3.2）。
    /// 
    /// 交互规范：
    /// 严格以当前激活的 DWG 文档（MdiActiveDocument）作为唯一输入数据源，
    /// 自动识别当前文档中的上版数据图层（0污水-* / 0污水-属性）或勘测数据图层（勘测管），
    /// 核查已建 Group 的 1:1 合法性以及未建 Group 管线与属性块的空间配对歧义/缺失/待绑定状态。
    /// </summary>
    public class MatchGroupCommands : CadCommandBase
    {
        [CommandMethod("MATCHGROUP", CommandFlags.Modal | CommandFlags.NoUndoMarker)]
        public override void Execute()
        {
            if (Doc == null) return;

            var service = new MatchGroupService(Doc.Database, Doc.Editor);
            var session = DocumentSessionManager.GetOrCreateSession(Doc.Database);
            if (session != null)
            {
                session.RegisterActiveRule(service);
            }

            service.Run(CadConfig.TaskNames.MatchGroup, bOnlyUpdate: false);
        }
    }

    /// <summary>
    /// 单文档管线与属性块 Group 绑定及空间配对核查服务（Stage 3 Commit 3.2）。
    /// 
    /// 核心职责：
    /// 1. 核查当前文档中既有 CAD GroupDictionary 的 1:1（1条管线 : 1个属性块）结构合法性；
    /// 2. 对尚未建立 CAD Group 的管线与属性块执行三级配对核查（Group -> XData -> 空间双距离判据），
    ///    识别一对多空间歧义（附带 PossibleMatches 瞬态辅助连线）、无配对管线、孤立属性块以及待绑定 1:1 匹配项；
    /// 3. 实现 ICheckRule 接口，支持在核查过程中人工移动属性块、修改块内 LEN/WIDTH 属性、调整管线顶点或创建 Group 后的毫秒级增量重算。
    /// </summary>
    public class MatchGroupService : CadBase<Dictionary<ObjectId, CheckIssueItem>>, ICheckRule
    {
        public const string DefaultRuleId = "MatchGroup";
        public const string DefaultRuleName = "图属Group核查";

        private readonly AcadDb.Database _db;
        private readonly Editor _ed;
        private readonly PipeSpatialMatcher _matcher = new PipeSpatialMatcher();

        // 管线 ObjectId <-> 关联属性块 ObjectId 双向拓扑追踪映射（供增量刷新 GetAffectedTargetIds 极速反查）
        private readonly Dictionary<ObjectId, HashSet<ObjectId>> _pipeToAttachments =
            new Dictionary<ObjectId, HashSet<ObjectId>>();
        private readonly Dictionary<ObjectId, HashSet<ObjectId>> _attachmentToPipes =
            new Dictionary<ObjectId, HashSet<ObjectId>>();

        public string RuleId => DefaultRuleId;
        public string RuleName => DefaultRuleName;

        /// <summary>
        /// 显式指定的目标管线图层集合（为空时自动探测当前 DWG 中的上版数据图层与勘测管图层）。
        /// </summary>
        public HashSet<string> PipeLayerNames { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 显式指定的目标属性块图层集合（为空时自动探测当前 DWG 中的 0污水-属性 与 勘测管 图层）。
        /// </summary>
        public HashSet<string> AttributeLayerNames { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 空间配对算法参数选项（默认从 Config.json 加载）。
        /// </summary>
        public PipeSpatialMatchOptions MatchOptions { get; set; } = PipeSpatialMatchOptions.FromConfig();

        /// <summary>
        /// 是否将“空间 1:1 唯一匹配成功但尚未建立 CAD Group”的管线作为【待复核（ToRecheck）】项输出至主核查面板（默认 true）。
        /// 开启后，用户可在主核查面板点击查看配对连线，并通过右键“一键修复：绑定图属 Group 与 XData”单条或批量落库成组。
        /// </summary>
        public bool IncludeUnboundUniqueMatches { get; set; } = true;

        /// <summary>
        /// 是否强制启用规划文字标注配对模式（null 表示根据当前图纸内实有图元自动嗅探：若存在规划管线与规划文字标注且无属性块，则自动进入规划文字配对模式）。
        /// </summary>
        public bool? UsePlanningTextMode { get; set; }

        /// <summary>
        /// 最近一次核查是否运行在规划文字标注配对模式下（Stage 5 Commit 5.2）。
        /// </summary>
        public bool IsPlanningTextMode { get; private set; }

        /// <summary>
        /// 最近一次核查产出的完整空间配对汇总报告。
        /// </summary>
        public PipeSpatialMatchSummary LastSummary { get; private set; }

        public MatchGroupService(AcadDb.Database db, Editor ed = null) : base(db, ed)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _ed = ed;
        }

        protected override Dictionary<ObjectId, CheckIssueItem> Execute(ProgressContext context)
        {
            return CheckAll(context);
        }

        protected override Dictionary<ObjectId, CheckIssueItem> Execute(ProgressContext context, List<ObjectId> objectIds)
        {
            var session = DocumentSessionManager.GetOrCreateSession(_db);
            var existingIssues = session?.CurrentIssues != null && session.CurrentIssues.Count > 0
                ? new Dictionary<ObjectId, CheckIssueItem>(session.CurrentIssues)
                : new Dictionary<ObjectId, CheckIssueItem>();

            return CheckIncremental(objectIds, existingIssues, context);
        }

        /// <summary>
        /// 执行全量图属与 Group 合法性核查（ICheckRule.CheckAll）。
        /// 自动支持“上版/勘测属性块模式”与“规划文字标注配对复核模式（Stage 5 Commit 5.2）”。
        /// </summary>
        public Dictionary<ObjectId, CheckIssueItem> CheckAll(ProgressContext context = null)
        {
            ResolveTargetLayers(out HashSet<string> pipeLayers, out HashSet<string> attrLayers, out bool isPlanTextMode);
            IsPlanningTextMode = isPlanTextMode;

            var session = DocumentSessionManager.GetOrCreateSession(_db);
            var options = MatchOptions ?? PipeSpatialMatchOptions.FromConfig();
            PipeSpatialMatchSummary summary = isPlanTextMode
                ? (PipeLayerNames != null && PipeLayerNames.Count > 0 && AttributeLayerNames != null && AttributeLayerNames.Count > 0
                    ? _matcher.MatchDatabaseTextLabels(_db, pipeLayers, attrLayers, options)
                    : _matcher.MatchAndEnrichPlanTextLabels(_db, session?.DatasetContext, CadConfig.PlanningData, options))
                : _matcher.MatchDatabaseBlockAttributes(_db, pipeLayers, attrLayers, options);
            LastSummary = summary;

            if (summary.TotalPipeCount == 0 && summary.TotalAttachmentCount == 0)
            {
                if (_ed != null)
                {
                    CadDialogService.ShowMessage(
                        "[Title]当前文档未找到可核查的管线、属性块或文字标注数据。\n\n" +
                        "[Hint]MATCHGROUP 命令针对当前激活文档核查图属配对与 Group 绑定关系：\n" +
                        $"1. 规划数据文字配对：{string.Join("、", CadConfig.PlanningData.GetAllPipeLayers())} <-> {string.Join("、", CadConfig.PlanningData.GetAllTextLayers())}；\n" +
                        $"2. 上版数据块配对：{string.Join("、", CadConfig.PreviousData.PipeLayers)}（属性块：{string.Join("、", CadConfig.PreviousData.AttributeLayers)}）；\n" +
                        $"3. 勘测数据块配对：{string.Join("、", CadConfig.SurveyData.PipeLayers)}。",
                        type: MessageBoxType.Warning);
                }
                return new Dictionary<ObjectId, CheckIssueItem>();
            }

            RebuildTopologyMappings(summary);

            // 同步继承当前会话中人工已标记为 Ignored 的历史状态
            var existingSameRuleIssues = new Dictionary<ObjectId, CheckIssueItem>();
            if (session?.CurrentIssues != null)
            {
                foreach (var kvp in session.CurrentIssues)
                {
                    if (kvp.Value != null && string.Equals(kvp.Value.RuleId, RuleId, StringComparison.OrdinalIgnoreCase))
                    {
                        existingSameRuleIssues[kvp.Key] = kvp.Value;
                    }
                }
            }

            return BuildIssuesFromSummary(summary, existingSameRuleIssues, context);
        }

        /// <summary>
        /// 执行局部增量核查（ICheckRule.CheckIncremental）：
        /// 当用户在核查过程中移动属性块/文字标注、修改块内属性或文字内容、修改管线几何或通过命令绑定/解绑 Group 后，
        /// 实时刷新受影响管线与邻近要素的核查状态，并将已完成 1:1 Group 绑定的记录自动归档为 Resolved。
        /// </summary>
        public Dictionary<ObjectId, CheckIssueItem> CheckIncremental(
            List<ObjectId> targetIds,
            Dictionary<ObjectId, CheckIssueItem> currentIssues = null,
            ProgressContext context = null)
        {
            var mergedIssues = currentIssues != null
                ? new Dictionary<ObjectId, CheckIssueItem>(currentIssues)
                : new Dictionary<ObjectId, CheckIssueItem>();

            var session = DocumentSessionManager.GetOrCreateSession(_db);
            session?.DatasetContext.InvalidatePreviousData();

            ResolveTargetLayers(out HashSet<string> pipeLayers, out HashSet<string> attrLayers, out bool isPlanTextMode);
            IsPlanningTextMode = isPlanTextMode;

            var options = MatchOptions ?? PipeSpatialMatchOptions.FromConfig();
            PipeSpatialMatchSummary latestSummary = isPlanTextMode
                ? (PipeLayerNames != null && PipeLayerNames.Count > 0 && AttributeLayerNames != null && AttributeLayerNames.Count > 0
                    ? _matcher.MatchDatabaseTextLabels(_db, pipeLayers, attrLayers, options)
                    : _matcher.MatchAndEnrichPlanTextLabels(_db, session?.DatasetContext, CadConfig.PlanningData, options))
                : _matcher.MatchDatabaseBlockAttributes(_db, pipeLayers, attrLayers, options);
            LastSummary = latestSummary;

            RebuildTopologyMappings(latestSummary);

            var latestIssues = BuildIssuesFromSummary(latestSummary, mergedIssues, context);

            // 1. 对已被擦除或已在最新核查中完成 1:1 Group 合法绑定的旧问题，自动标记为 Resolved（已修复）
            var existingRuleKeys = mergedIssues
                .Where(kvp => kvp.Value != null && string.Equals(kvp.Value.RuleId, RuleId, StringComparison.OrdinalIgnoreCase))
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (ObjectId keyId in existingRuleKeys)
            {
                if (keyId.IsNull || !keyId.IsValid || keyId.IsErased)
                {
                    mergedIssues[keyId].Status = IssueReviewStatus.Resolved;
                    mergedIssues[keyId].Remark = "图元已删除（已归档）";
                    continue;
                }

                if (!latestIssues.ContainsKey(keyId))
                {
                    // 该管线或属性块/文字在最新全图配对中已不再属于异常/未建Group项（即已完成 1:1 CAD Group 绑定）
                    CheckIssueItem resolvedItem = mergedIssues[keyId];
                    resolvedItem.Status = IssueReviewStatus.Resolved;
                    if (latestSummary.MatchByPipeId.TryGetValue(keyId, out PipeAttachmentMatchItem matched) &&
                        matched != null &&
                        matched.Channel == SpatialMatchChannel.CadGroup)
                    {
                        resolvedItem.Description = matched.Description;
                        resolvedItem.Remark = $"{matched.Description}（已修复）";
                    }
                    else
                    {
                        resolvedItem.Remark = "已完成 1:1 图属 Group 绑定（已修复）";
                    }
                }
            }

            // 2. 将最新检出的待核查/待复核问题合并写入字典
            foreach (var kvp in latestIssues)
            {
                mergedIssues[kvp.Key] = kvp.Value;
            }

            return mergedIssues;
        }

        /// <summary>
        /// 变更影响范围追踪（ICheckRule.GetAffectedTargetIds）：
        /// 监听管线（Polyline/Line）、属性块（BlockReference）、块内属性文字（AttributeReference）、规划文字标注（DBText/MText）及 CAD Group 的增删改。
        /// </summary>
        public IEnumerable<ObjectId> GetAffectedTargetIds(DBObject changedObj)
        {
            var affectedIds = new HashSet<ObjectId>();
            if (changedObj == null)
                return affectedIds;

            try
            {
                // 情况 1：CAD Group 发生增删改
                if (changedObj is Group grp)
                {
                    if (!grp.IsErased)
                    {
                        ObjectId[] memberIds = grp.GetAllEntityIds();
                        if (memberIds != null)
                        {
                            foreach (ObjectId mid in memberIds)
                            {
                                if (!mid.IsNull)
                                {
                                    affectedIds.Add(mid);
                                    if (_attachmentToPipes.TryGetValue(mid, out HashSet<ObjectId> linkedPipes))
                                    {
                                        foreach (ObjectId pid in linkedPipes)
                                            affectedIds.Add(pid);
                                    }
                                }
                            }
                        }
                    }
                    return affectedIds;
                }

                // 情况 2：块内属性（AttributeReference，如修改 LEN / WIDTH 字段值）发生修改，追溯其父块 BlockReference
                Entity targetEnt = changedObj as Entity;
                if (changedObj is AttributeReference attrRef && !attrRef.OwnerId.IsNull && attrRef.OwnerId.IsValid)
                {
                    using (OpenCloseTransaction tr = _db.TransactionManager.StartOpenCloseTransaction())
                    {
                        targetEnt = tr.GetObject(attrRef.OwnerId, OpenMode.ForRead, false) as BlockReference;
                    }
                }

                if (targetEnt == null)
                    return affectedIds;

                ResolveTargetLayers(out HashSet<string> pipeLayers, out HashSet<string> attrLayers, out bool isPlanTextMode);

                // 情况 3：目标管线图层上的多段线或直线发生几何/图层变更
                if ((targetEnt is Polyline || targetEnt is Line) &&
                    !string.IsNullOrWhiteSpace(targetEnt.Layer) &&
                    pipeLayers.Contains(targetEnt.Layer))
                {
                    affectedIds.Add(targetEnt.ObjectId);
                    if (_pipeToAttachments.TryGetValue(targetEnt.ObjectId, out HashSet<ObjectId> linkedAtts))
                    {
                        foreach (ObjectId aid in linkedAtts)
                        {
                            affectedIds.Add(aid);
                            if (_attachmentToPipes.TryGetValue(aid, out HashSet<ObjectId> neighborPipes))
                            {
                                foreach (ObjectId np in neighborPipes)
                                    affectedIds.Add(np);
                            }
                        }
                    }
                }

                // 情况 4：目标属性块图层上的 BlockReference 或规划文字标注图层上的 DBText / MText 发生位移、编辑或删除
                if ((targetEnt is BlockReference || targetEnt is DBText || targetEnt is MText) &&
                    !string.IsNullOrWhiteSpace(targetEnt.Layer) &&
                    attrLayers.Contains(targetEnt.Layer))
                {
                    affectedIds.Add(targetEnt.ObjectId);
                    if (_attachmentToPipes.TryGetValue(targetEnt.ObjectId, out HashSet<ObjectId> boundPipes))
                    {
                        foreach (ObjectId pid in boundPipes)
                            affectedIds.Add(pid);
                    }

                    double defaultRadius = isPlanTextMode ? 85.0 : 25.0;
                    double searchBuffer = (MatchOptions?.MaxCurveDistance ?? defaultRadius) * 1.5;
                    using (OpenCloseTransaction tr = _db.TransactionManager.StartOpenCloseTransaction())
                    {
                        BlockTableRecord btr = (BlockTableRecord)tr.GetObject(_db.CurrentSpaceId, OpenMode.ForRead);
                        foreach (ObjectId oid in btr)
                        {
                            string dxfName = oid.ObjectClass.DxfName;
                            if (dxfName == "LINE" || dxfName == "LWPOLYLINE" || dxfName == "POLYLINE")
                            {
                                Entity pipeCandidate = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                                if (pipeCandidate != null &&
                                    !string.IsNullOrWhiteSpace(pipeCandidate.Layer) &&
                                    pipeLayers.Contains(pipeCandidate.Layer) &&
                                    GeometryHelper.IsIntersection2D(targetEnt, pipeCandidate, searchBuffer))
                                {
                                    affectedIds.Add(pipeCandidate.ObjectId);
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // 对象被深度擦除时安全忽略
            }

            return affectedIds;
        }

        /// <summary>
        /// 将当前核查出的所有 1:1 唯一匹配但尚未建立 CAD Group 的管线与属性块一键批量封装为可逆绑定操作（支持 Undo / Redo）。
        /// </summary>
        public BindPipeGroupOperation CreateBatchBindOperationForUniqueMatches(
            Dictionary<ObjectId, CheckIssueItem> currentIssues = null,
            Action<IReadOnlyList<(ObjectId PipeId, CheckIssueItem Snapshot)>, bool> onStateChanged = null)
        {
            if (LastSummary == null)
            {
                CheckAll();
            }

            if (LastSummary == null)
                return null;

            var bindPairs = new List<(ObjectId PipeId, ObjectId AttachmentId, CheckIssueItem BeforeSnapshot)>();
            foreach (PipeAttachmentMatchItem match in LastSummary.UniqueMatches)
            {
                if (match?.Pipe == null || match.MatchedAttachment == null)
                    continue;

                if (match.Channel == SpatialMatchChannel.CadGroup)
                    continue;

                if (match.Pipe.PipeId.IsNull || match.MatchedAttachment.AttachmentId.IsNull)
                    continue;

                CheckIssueItem snapshot = null;
                if (currentIssues != null)
                {
                    currentIssues.TryGetValue(match.Pipe.PipeId, out snapshot);
                }

                bindPairs.Add((match.Pipe.PipeId, match.MatchedAttachment.AttachmentId, snapshot));
            }

            if (bindPairs.Count == 0)
                return null;

            return new BindPipeGroupOperation(bindPairs, onStateChanged, _db);
        }

        protected override void OnSuccess(Dictionary<ObjectId, CheckIssueItem> result, bool bOnlyUpdate = false)
        {
            var session = DocumentSessionManager.GetOrCreateSession(_db);

            if (bOnlyUpdate)
            {
                var merged = session != null
                    ? session.MergeRuleIssues(RuleId, result ?? new Dictionary<ObjectId, CheckIssueItem>(), isIncremental: true)
                    : (result ?? new Dictionary<ObjectId, CheckIssueItem>());
                palCheckPipe.Instance.UpdateIncrementalData(merged);
                return;
            }

            if (result == null || result.Count == 0)
            {
                Ed?.WriteMessage($"\n[{RuleName}] 检查通过：当前文档共 {LastSummary?.ValidGroupPairCount ?? 0} 组管线与属性要素均已 1:1 合法绑定 Group，无异常或未绑定项。\n");
                if (session != null)
                {
                    var mergedEmpty = session.MergeRuleIssues(RuleId, new Dictionary<ObjectId, CheckIssueItem>(), isIncremental: false);
                    if (palCheckPipe.Instance.IsVisible)
                    {
                        palCheckPipe.Instance.UpdateData(mergedEmpty);
                    }
                }
                return;
            }

            int pendingCount = result.Values.Count(i => i.Status == IssueReviewStatus.Pending);
            int toRecheckCount = result.Values.Count(i => i.Status == IssueReviewStatus.ToRecheck);
            Ed?.WriteMessage(
                $"\n[{RuleName}] 核查完成：共检出 {result.Count} 项（待核查冲突/不规范/多段共标/多余标注：{pendingCount} 项，待复核确认连线与绑定 Group：{toRecheckCount} 项）。\n");

            var mergedAll = session != null
                ? session.MergeRuleIssues(RuleId, result, isIncremental: false)
                : result;
            palCheckPipe.Instance.Show(mergedAll);
        }

        private Dictionary<ObjectId, CheckIssueItem> BuildIssuesFromSummary(
            PipeSpatialMatchSummary summary,
            Dictionary<ObjectId, CheckIssueItem> previousIssues,
            ProgressContext context)
        {
            var issues = new Dictionary<ObjectId, CheckIssueItem>();
            if (summary == null)
                return issues;

            int totalItems = summary.InvalidGroups.Count +
                             summary.ConflictMatches.Count +
                             summary.UnmatchedPipes.Count +
                             summary.OrphanAttachments.Count +
                             (IncludeUnboundUniqueMatches ? summary.UniqueMatches.Count : 0);

            context?.SetTotal(Math.Max(totalItems, 1));
            int subSeq = 0;

            // 1. 既有 CAD Group 成员结构异常（非 1:1 或被重复引用）
            foreach (PipeAttachmentMatchItem invalidGrp in summary.InvalidGroups)
            {
                context?.Step();
                ObjectId targetId = invalidGrp.Pipe?.PipeId ?? invalidGrp.MatchedAttachment?.AttachmentId ?? invalidGrp.GroupId;
                if (targetId.IsNull)
                    continue;

                string targetHandle = invalidGrp.Pipe?.Handle ?? invalidGrp.MatchedAttachment?.Handle ?? invalidGrp.GroupName ?? targetId.Handle.ToString();
                string layerName = invalidGrp.Pipe?.LayerName ?? invalidGrp.MatchedAttachment?.LayerName ?? "GroupDictionary";

                var relatedIds = new List<ObjectId>();
                if (invalidGrp.MatchedAttachment != null && !invalidGrp.MatchedAttachment.AttachmentId.IsNull)
                    relatedIds.Add(invalidGrp.MatchedAttachment.AttachmentId);
                if (!invalidGrp.GroupId.IsNull)
                    relatedIds.Add(invalidGrp.GroupId);

                ExtractDiameterAndHeight(invalidGrp.MatchedAttachment?.Attributes, out double diam, out double height);

                var issue = new CheckIssueItem
                {
                    IssueId = $"{RuleId}:{targetHandle}:{subSeq++}",
                    RuleId = RuleId,
                    RuleName = RuleName,
                    Type = ProblemType.OneToMany,
                    Level = ProblemLevel.Error,
                    Status = IssueReviewStatus.Pending,
                    TargetObjectId = targetId,
                    TargetHandle = targetHandle,
                    LayerName = layerName,
                    RelatedObjectIds = relatedIds,
                    Location = invalidGrp.ClosestPointOnPipe,
                    Diameter = diam,
                    Height = height,
                    Description = invalidGrp.Description,
                    Remark = invalidGrp.Description,
                    PossibleMatches = invalidGrp.PossibleMatches ?? new List<MatchItem>(),
                    RulePayload = invalidGrp
                };

                PreserveIgnoredStatusIfApplicable(issue, previousIssues);
                issues[targetId] = issue;
            }

            // 2. 一对多空间邻近歧义、一管多标或不规范文字标注（列入 Pending 待核查，附带 PossibleMatches 供绘制瞬态红色辅助连线）
            foreach (PipeAttachmentMatchItem conflict in summary.ConflictMatches)
            {
                context?.Step();
                if (conflict?.Pipe == null || conflict.Pipe.PipeId.IsNull)
                    continue;

                ObjectId pipeId = conflict.Pipe.PipeId;
                var relatedIds = new List<ObjectId>();
                if (conflict.MatchedAttachment != null && !conflict.MatchedAttachment.AttachmentId.IsNull)
                {
                    relatedIds.Add(conflict.MatchedAttachment.AttachmentId);
                }
                if (conflict.PossibleMatches != null)
                {
                    foreach (MatchItem cand in conflict.PossibleMatches)
                    {
                        if (!cand.Id.IsNull && !relatedIds.Contains(cand.Id))
                            relatedIds.Add(cand.Id);
                    }
                }

                ExtractDiameterAndHeight(conflict.MatchedAttachment?.Attributes, out double diam, out double height);

                var issue = new CheckIssueItem
                {
                    IssueId = $"{RuleId}:{conflict.Pipe.Handle}:0",
                    RuleId = RuleId,
                    RuleName = RuleName,
                    Type = ProblemType.OneToMany,
                    Level = ProblemLevel.Warning,
                    Status = IssueReviewStatus.Pending,
                    TargetObjectId = pipeId,
                    TargetHandle = conflict.Pipe.Handle,
                    LayerName = conflict.Pipe.LayerName,
                    RelatedObjectIds = relatedIds,
                    Location = conflict.ClosestPointOnPipe,
                    Diameter = diam,
                    Height = height,
                    Description = conflict.Description,
                    Remark = conflict.Description,
                    PossibleMatches = conflict.PossibleMatches ?? new List<MatchItem>(),
                    RulePayload = conflict
                };

                PreserveIgnoredStatusIfApplicable(issue, previousIssues);
                issues[pipeId] = issue;
            }

            // 3. 未找到专属 1:1 配对的管线（含多段共标与缺标管段，多段共标时附带指向相邻共享标注的辅助连线）
            foreach (PipeAttachmentMatchItem unmatched in summary.UnmatchedPipes)
            {
                context?.Step();
                if (unmatched?.Pipe == null || unmatched.Pipe.PipeId.IsNull)
                    continue;

                ObjectId pipeId = unmatched.Pipe.PipeId;
                ExtractDiameterAndHeight(unmatched.MatchedAttachment?.Attributes, out double uDiam, out double uHeight);

                var relatedIds = new List<ObjectId>();
                if (unmatched.MatchedAttachment != null && !unmatched.MatchedAttachment.AttachmentId.IsNull)
                {
                    relatedIds.Add(unmatched.MatchedAttachment.AttachmentId);
                }

                var issue = new CheckIssueItem
                {
                    IssueId = $"{RuleId}:{unmatched.Pipe.Handle}:0",
                    RuleId = RuleId,
                    RuleName = RuleName,
                    Type = ProblemType.NoAdjacentItems,
                    Level = ProblemLevel.Error,
                    Status = IssueReviewStatus.Pending,
                    TargetObjectId = pipeId,
                    TargetHandle = unmatched.Pipe.Handle,
                    LayerName = unmatched.Pipe.LayerName,
                    RelatedObjectIds = relatedIds,
                    Location = unmatched.ClosestPointOnPipe,
                    Diameter = uDiam,
                    Height = uHeight,
                    Description = unmatched.Description,
                    Remark = unmatched.Description,
                    PossibleMatches = unmatched.PossibleMatches ?? new List<MatchItem>(),
                    RulePayload = unmatched
                };

                PreserveIgnoredStatusIfApplicable(issue, previousIssues);
                issues[pipeId] = issue;
            }

            // 4. 未被任何管线 1:1 认领的多余/孤立属性块或文字标注（附带指向最邻近管线的辅助连线）
            foreach (PipeAttachmentMatchItem orphan in summary.OrphanAttachments)
            {
                context?.Step();
                if (orphan?.MatchedAttachment == null || orphan.MatchedAttachment.AttachmentId.IsNull)
                    continue;

                ObjectId attId = orphan.MatchedAttachment.AttachmentId;
                ExtractDiameterAndHeight(orphan.MatchedAttachment.Attributes, out double diam, out double height);

                var relatedIds = new List<ObjectId>();
                if (orphan.Pipe != null && !orphan.Pipe.PipeId.IsNull)
                {
                    relatedIds.Add(orphan.Pipe.PipeId);
                }

                var issue = new CheckIssueItem
                {
                    IssueId = $"{RuleId}:ATT_{orphan.MatchedAttachment.Handle}:0",
                    RuleId = RuleId,
                    RuleName = RuleName,
                    Type = ProblemType.NoAdjacentItems,
                    Level = ProblemLevel.Warning,
                    Status = IssueReviewStatus.Pending,
                    TargetObjectId = attId,
                    TargetHandle = orphan.MatchedAttachment.Handle,
                    LayerName = orphan.MatchedAttachment.LayerName,
                    RelatedObjectIds = relatedIds,
                    Location = orphan.MatchedAttachment.Position,
                    Diameter = diam,
                    Height = height,
                    Description = orphan.Description,
                    Remark = orphan.Description,
                    PossibleMatches = orphan.PossibleMatches ?? new List<MatchItem>(),
                    RulePayload = orphan
                };

                PreserveIgnoredStatusIfApplicable(issue, previousIssues);
                issues[attId] = issue;
            }

            // 5. 空间或 XData 1:1 唯一匹配成功但尚未建立 CAD Group 的管线（列入 ToRecheck 待复核，支持查看连线与一键成组）
            if (IncludeUnboundUniqueMatches)
            {
                foreach (PipeAttachmentMatchItem unique in summary.UniqueMatches)
                {
                    context?.Step();
                    if (unique?.Pipe == null || unique.Pipe.PipeId.IsNull || unique.MatchedAttachment == null)
                        continue;

                    // 已经通过原生 CAD Group 1:1 绑定的管线完全合规，无需列入问题表
                    if (unique.Channel == SpatialMatchChannel.CadGroup)
                        continue;

                    ObjectId pipeId = unique.Pipe.PipeId;
                    if (issues.ContainsKey(pipeId))
                        continue;

                    ExtractDiameterAndHeight(unique.MatchedAttachment.Attributes, out double diam, out double height);

                    string channelText = unique.Channel == SpatialMatchChannel.XDataLink
                        ? $"XData({PipeSpatialMatcher.XDataRegAppName})句柄匹配"
                        : (unique.Channel == SpatialMatchChannel.ExtentsCenterMatch ? "外包框中心+管长匹配" : "曲线邻近+平行角匹配");

                    string desc;
                    if (IsPlanningTextMode)
                    {
                        string labelPreview = !string.IsNullOrWhiteSpace(unique.MatchedAttachment.BlockNameOrText)
                            ? $" '{unique.MatchedAttachment.BlockNameOrText}'"
                            : string.Empty;
                        string angText = unique.AngleDifferenceDegrees.HasValue
                            ? $", 平行角差={unique.AngleDifferenceDegrees.Value:F1}°"
                            : string.Empty;
                        desc = $"待人工复核：已通过[{channelText}]配对文字标注 [{unique.MatchedAttachment.Handle}]{labelPreview}" +
                               $"（距曲线={unique.DistanceToCurve:F2}m{angText}），点击可查看配对连线，右键可一键绑定 Group 与 XData";
                    }
                    else
                    {
                        string blockChannelText = unique.Channel == SpatialMatchChannel.XDataLink
                            ? $"XData({PipeSpatialMatcher.XDataRegAppName})句柄匹配"
                            : (unique.Channel == SpatialMatchChannel.ExtentsCenterMatch ? "外包框中心+管长匹配" : "曲线邻近匹配");
                        desc = $"未建 Group：已通过[{blockChannelText}]唯一配对属性块 [{unique.MatchedAttachment.Handle}]" +
                               $"（距曲线={unique.DistanceToCurve:F2}m, 距外包框中心={unique.DistanceToExtentsCenter:F2}m），右键可一键绑定 Group 与 XData";
                    }

                    var possibleMatches = unique.PossibleMatches != null && unique.PossibleMatches.Count > 0
                        ? unique.PossibleMatches
                        : new List<MatchItem>
                        {
                            new MatchItem(
                                unique.MatchedAttachment.AttachmentId,
                                unique.MatchedAttachment.Position,
                                unique.DistanceToCurve,
                                unique.ClosestPointOnPipe,
                                unique.MatchedAttachment.RotationDegrees ?? 0.0,
                                unique.AngleDifferenceDegrees ?? 0.0,
                                true)
                        };

                    var issue = new CheckIssueItem
                    {
                        IssueId = $"{RuleId}:{unique.Pipe.Handle}:0",
                        RuleId = RuleId,
                        RuleName = RuleName,
                        Type = ProblemType.OneToMany,
                        Level = ProblemLevel.Warning,
                        Status = IssueReviewStatus.ToRecheck,
                        TargetObjectId = pipeId,
                        TargetHandle = unique.Pipe.Handle,
                        LayerName = unique.Pipe.LayerName,
                        RelatedObjectIds = new List<ObjectId> { unique.MatchedAttachment.AttachmentId },
                        Location = unique.ClosestPointOnPipe,
                        Diameter = diam,
                        Height = height,
                        Description = desc,
                        Remark = desc,
                        PossibleMatches = possibleMatches,
                        RulePayload = unique
                    };

                    PreserveIgnoredStatusIfApplicable(issue, previousIssues);
                    issues[pipeId] = issue;
                }
            }

            return issues;
        }

        private void RebuildTopologyMappings(PipeSpatialMatchSummary summary)
        {
            _pipeToAttachments.Clear();
            _attachmentToPipes.Clear();

            if (summary == null)
                return;

            var allItems = summary.UniqueMatches
                .Concat(summary.ConflictMatches)
                .Concat(summary.InvalidGroups)
                .Concat(summary.UnmatchedPipes)
                .Concat(summary.OrphanAttachments);

            foreach (PipeAttachmentMatchItem item in allItems)
            {
                if (item == null)
                    continue;

                ObjectId pipeId = item.Pipe != null ? item.Pipe.PipeId : ObjectId.Null;
                ObjectId attId = item.MatchedAttachment != null ? item.MatchedAttachment.AttachmentId : ObjectId.Null;

                if (!pipeId.IsNull && !attId.IsNull)
                {
                    LinkPipeAndAttachment(pipeId, attId);
                }

                if (!pipeId.IsNull && item.PossibleMatches != null)
                {
                    foreach (MatchItem cand in item.PossibleMatches)
                    {
                        if (!cand.Id.IsNull && cand.Id != pipeId)
                        {
                            LinkPipeAndAttachment(pipeId, cand.Id);
                        }
                    }
                }
            }
        }

        private void LinkPipeAndAttachment(ObjectId pipeId, ObjectId attId)
        {
            if (!_pipeToAttachments.TryGetValue(pipeId, out HashSet<ObjectId> attSet))
            {
                attSet = new HashSet<ObjectId>();
                _pipeToAttachments[pipeId] = attSet;
            }
            attSet.Add(attId);

            if (!_attachmentToPipes.TryGetValue(attId, out HashSet<ObjectId> pipeSet))
            {
                pipeSet = new HashSet<ObjectId>();
                _attachmentToPipes[attId] = pipeSet;
            }
            pipeSet.Add(pipeId);
        }

        /// <summary>
        /// 自动探测当前文档中的目标管线图层与属性块/文字标注图层（严格以当前 _db 为唯一数据源，按实有图元数识别规划文字图纸与上版/勘测属性块图纸）。
        /// </summary>
        private void ResolveTargetLayers(
            out HashSet<string> pipeLayers,
            out HashSet<string> attrLayers,
            out bool isPlanningTextMode)
        {
            if (PipeLayerNames != null && PipeLayerNames.Count > 0 &&
                AttributeLayerNames != null && AttributeLayerNames.Count > 0)
            {
                pipeLayers = new HashSet<string>(PipeLayerNames, StringComparer.OrdinalIgnoreCase);
                attrLayers = new HashSet<string>(AttributeLayerNames, StringComparer.OrdinalIgnoreCase);
                if (UsePlanningTextMode.HasValue)
                {
                    isPlanningTextMode = UsePlanningTextMode.Value;
                }
                else
                {
                    var cfgTextSet = CadConfig.PlanningData?.GetAllTextLayers() ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    isPlanningTextMode = attrLayers.Any(l => cfgTextSet.Contains(l));
                }
                return;
            }

            pipeLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            attrLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var planPipes = CadConfig.PlanningData?.GetAllPipeLayers() ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var planTexts = CadConfig.PlanningData?.GetAllTextLayers() ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var prevPipes = new HashSet<string>(CadConfig.PreviousData.PipeLayers ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var prevAttrs = new HashSet<string>(CadConfig.PreviousData.AttributeLayers ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var surveyPipes = new HashSet<string>(CadConfig.SurveyData.PipeLayers ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var surveyAttrs = new HashSet<string>(CadConfig.SurveyData.AttributeLayers ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            if (UsePlanningTextMode == true)
            {
                isPlanningTextMode = true;
                foreach (string l in planPipes) pipeLayers.Add(l);
                foreach (string l in planTexts) attrLayers.Add(l);
                return;
            }

            int planPipeEntityCount = 0;
            int planTextEntityCount = 0;
            int prevAttrBlockCount = 0;
            int surveyAttrBlockCount = 0;

            using (OpenCloseTransaction tr = _db.TransactionManager.StartOpenCloseTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(_db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId objId in ms)
                {
                    if (objId.IsNull || objId.IsErased)
                        continue;

                    string dxfName = objId.ObjectClass.DxfName;
                    if (dxfName != "LWPOLYLINE" && dxfName != "POLYLINE" && dxfName != "LINE" &&
                        dxfName != "INSERT" && dxfName != "TEXT" && dxfName != "MTEXT")
                    {
                        continue;
                    }

                    Entity ent = tr.GetObject(objId, OpenMode.ForRead, false) as Entity;
                    if (ent == null || string.IsNullOrWhiteSpace(ent.Layer))
                        continue;

                    string layer = ent.Layer.Trim();
                    if ((dxfName == "LWPOLYLINE" || dxfName == "POLYLINE" || dxfName == "LINE") && planPipes.Contains(layer))
                    {
                        planPipeEntityCount++;
                    }
                    else if ((dxfName == "TEXT" || dxfName == "MTEXT") && planTexts.Contains(layer))
                    {
                        planTextEntityCount++;
                    }
                    else if (dxfName == "INSERT")
                    {
                        if (prevAttrs.Contains(layer))
                            prevAttrBlockCount++;
                        if (surveyAttrs.Contains(layer))
                            surveyAttrBlockCount++;
                    }
                }
            }

            if (UsePlanningTextMode != false &&
                planPipeEntityCount > 0 &&
                planTextEntityCount > 0 &&
                (planPipeEntityCount + planTextEntityCount) >= (prevAttrBlockCount + surveyAttrBlockCount))
            {
                isPlanningTextMode = true;
                foreach (string l in planPipes) pipeLayers.Add(l);
                foreach (string l in planTexts) attrLayers.Add(l);
                return;
            }

            isPlanningTextMode = false;
            if (prevAttrBlockCount > 0)
            {
                foreach (string l in prevPipes) pipeLayers.Add(l);
                foreach (string l in prevAttrs) attrLayers.Add(l);
            }

            if (surveyAttrBlockCount > 0)
            {
                foreach (string l in surveyPipes) pipeLayers.Add(l);
                foreach (string l in surveyAttrs) attrLayers.Add(l);
            }

            if (pipeLayers.Count == 0 && planPipeEntityCount > 0 && planTextEntityCount > 0)
            {
                isPlanningTextMode = true;
                foreach (string l in planPipes) pipeLayers.Add(l);
                foreach (string l in planTexts) attrLayers.Add(l);
                return;
            }

            if (pipeLayers.Count == 0)
            {
                foreach (string l in prevPipes.Concat(surveyPipes)) pipeLayers.Add(l);
            }
            if (attrLayers.Count == 0)
            {
                foreach (string l in prevAttrs.Concat(surveyAttrs)) attrLayers.Add(l);
            }
        }

        private static void ExtractDiameterAndHeight(
            IDictionary<string, object> attrs,
            out double diameter,
            out double height)
        {
            diameter = 0.0;
            height = 0.0;
            if (attrs == null || attrs.Count == 0)
                return;

            if (attrs.TryGetValue("WIDTH", out object wObj) && wObj != null)
            {
                double.TryParse(wObj.ToString().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out diameter);
            }

            if (attrs.TryGetValue("HEITH", out object hObj) && hObj != null)
            {
                double.TryParse(hObj.ToString().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out height);
            }
        }

        private static void PreserveIgnoredStatusIfApplicable(
            CheckIssueItem newIssue,
            Dictionary<ObjectId, CheckIssueItem> previousIssues)
        {
            if (newIssue == null || previousIssues == null || newIssue.TargetObjectId.IsNull)
                return;

            if (previousIssues.TryGetValue(newIssue.TargetObjectId, out CheckIssueItem oldIssue) &&
                oldIssue != null &&
                string.Equals(oldIssue.RuleId, newIssue.RuleId, StringComparison.OrdinalIgnoreCase) &&
                oldIssue.Status == IssueReviewStatus.Ignored)
            {
                newIssue.Status = IssueReviewStatus.Ignored;
                newIssue.Remark = oldIssue.Remark;
            }
        }
    }
}
