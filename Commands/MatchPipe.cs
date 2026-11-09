using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using NetTopologySuite.Index.Strtree;
using process_pipeline.Core;
using process_pipeline.Core.Config;
using process_pipeline.Core.DataSource;
using process_pipeline.Core.PreCheck;
using process_pipeline.Forms;
using process_pipeline.Geometry;
using process_pipeline.Models;
using process_pipeline.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AcadDb = Autodesk.AutoCAD.DatabaseServices;

namespace process_pipeline.Commands
{
    /// <summary>
    /// 跨图规划管线与勘测管线匹配及管径一致性核查命令入口（Stage 6 Commit 6.1）。
    /// </summary>
    public class MatchPipe : CadCommandBase
    {
        [CommandMethod("MatchPipe", CommandFlags.Modal | CommandFlags.NoUndoMarker)]
        public override void Execute()
        {
            if (Doc == null) return;

            var session = DocumentSessionManager.GetOrCreateSession(Doc.Database);
            if (!CheckPipelineCoordinator.EnsureDataSourcesReady(
                    Doc.Database,
                    session?.DatasetContext,
                    needPreviousData: false,
                    needSurveyData: true,
                    out _,
                    allowInteractiveDialog: true))
            {
                Doc.Editor?.WriteMessage("\n[管段匹配核查] 未指定有效的勘测数据 DWG，操作已取消。\n");
                return;
            }

            var service = new MatchPipeService(Doc.Database, Doc.Editor);
            session?.RegisterActiveRule(service);

            service.Run("管段匹配核查", bOnlyUpdate: false);
        }
    }

    /// <summary>
    /// 单段规划管线与合并勘测管线的分段匹配及管径比对核查明细（Stage 6 Commit 6.1）。
    /// </summary>
    public class MatchPipeSegmentCheckItem
    {
        public ObjectId PlanPipeId { get; set; } = ObjectId.Null;
        public string PlanHandle { get; set; } = string.Empty;
        public string PlanLayerName { get; set; } = string.Empty;
        public string CategoryId { get; set; } = string.Empty;
        public string CategoryName { get; set; } = string.Empty;
        public int SubIndex { get; set; }
        public double PlanPipeLength { get; set; }
        public double? PlanDiameter { get; set; }
        public double? PlanSecondaryDiameter { get; set; }
        public string PlanRawLabel { get; set; } = string.Empty;
        public bool IsMatchedToSurvey { get; set; }
        public List<string> SurveyHandles { get; set; } = new List<string>();
        public double? SurveyDiameter { get; set; }
        public double? SurveyHeight { get; set; }
        public SegmentInterval TargetInterval { get; set; }
        public SegmentInterval SurveyInterval { get; set; }
        public double AverageDistance { get; set; }
        public double MaxDistance { get; set; }
        public double Score { get; set; }
        public bool IsDiameterConsistent { get; set; }
        public bool IsMissingPlanDiameterOnMatched { get; set; }
        public Point3d RepresentativeLocation { get; set; }
        public MatchItem HelperLine { get; set; }
        public CompareResult RawCompareResult { get; set; }
    }

    /// <summary>
    /// 规划数据（三类管线）与只读合并勘测数据管径匹配核查完整结构化报告（Stage 6 Commit 6.1）。
    /// </summary>
    public class MatchPipeCheckReport
    {
        public int TotalPlanPipeCount { get; set; }
        public int SurveyRawPolylineCount { get; set; }
        public int SurveyMergedPolylineCount { get; set; }
        public string SurveyCacheKey { get; set; } = string.Empty;

        public Dictionary<string, int> PlanCountByCategory { get; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, int> MatchedPipeCountByCategory { get; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, int> UnmatchedPipeCountByCategory { get; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public List<MatchPipeSegmentCheckItem> AllSegmentItems { get; } = new List<MatchPipeSegmentCheckItem>();
        public List<MatchPipeSegmentCheckItem> DiameterConsistentItems { get; } = new List<MatchPipeSegmentCheckItem>();
        public List<MatchPipeSegmentCheckItem> DiameterMismatchItems { get; } = new List<MatchPipeSegmentCheckItem>();
        public List<MatchPipeSegmentCheckItem> MissingPlanLabelOnMatchedItems { get; } = new List<MatchPipeSegmentCheckItem>();
        public List<MatchPipeSegmentCheckItem> UnmatchedExistingPipeItems { get; } = new List<MatchPipeSegmentCheckItem>();
        public List<MatchPipeSegmentCheckItem> MultiSegmentPipeItems { get; } = new List<MatchPipeSegmentCheckItem>();

        public List<CheckIssueItem> AllIssueItems { get; } = new List<CheckIssueItem>();
        public Dictionary<ObjectId, CheckIssueItem> IssueMap { get; } = new Dictionary<ObjectId, CheckIssueItem>();
    }

    /// <summary>
    /// 规划数据（新建、现状、改扩建三类管线）与只读合并勘测数据分段匹配及管径一致性核查服务（Stage 6 Commit 6.1）。
    /// 
    /// 核心设计：
    /// 1. 串联 <see cref="PipeDatasetContext"/> 中已组装的规划管分类（颜色优先）与文字标注管径（WIDTH），
    ///    以及只读勘测管（按 WIDTH 拓扑合并 + 静态 STRtree 缓存）；
    /// 2. 对全部三类规划管线（规划新建管、现状管、规划改扩建管）执行分段匹配（PipeMatcher），
    ///    核对规划标注管径与勘测管径一致性；
    /// 3. 支持单根长规划管线跨越多个不同管径勘测管段时输出多子项（SubIndex = 0, 1, ...）核查问题；
    /// 4. 支持在核查过程中修改规划管线几何或修改规划管径文字标注（DBText/MText）后，
    ///    直接查询内存静态勘测 STRtree 实现毫秒级局部增量重算与自动归档。
    /// </summary>
    public class MatchPipeService : CadBase<Dictionary<ObjectId, CheckIssueItem>>, ICheckRule
    {
        public const string DefaultRuleId = "MatchPipe";
        public const string DefaultRuleName = "管段匹配核查";

        /// <summary>
        /// 管径数值一致性比对容差（毫米，默认 1.0mm）。
        /// </summary>
        public const double DefaultDiameterToleranceMm = 1.0;

        private readonly AcadDb.Database _db;
        private readonly Editor _ed;
        private readonly DataSourceService _dataSourceService = new DataSourceService();
        private readonly PipeSpatialMatcher _spatialMatcher = new PipeSpatialMatcher();
        private readonly GeometryFactory _geometryFactory =
            NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();

        /// <summary>
        /// 规划文字标注 ObjectId -> 关联规划管线 ObjectId 映射（供文字标注内容修改后触发对应管线的管径增量复核）。
        /// </summary>
        private readonly Dictionary<ObjectId, HashSet<ObjectId>> _textToPlanPipeIds =
            new Dictionary<ObjectId, HashSet<ObjectId>>();

        public string RuleId => DefaultRuleId;
        public string RuleName => DefaultRuleName;

        /// <summary>
        /// 可选的外部指定勘测数据 Database（若未指定则通过 DataSourceService 智能嗅探并缓存）。
        /// </summary>
        public AcadDb.Database OverrideSurveyDatabase { get; set; }

        /// <summary>
        /// 管线分段匹配参数。
        /// </summary>
        public PolylineMatchOptions MatchOptions { get; set; } = new PolylineMatchOptions();

        /// <summary>
        /// 勘测管拓扑合并参数（默认按 WIDTH 分组合并）。
        /// </summary>
        public PolylineMergeOptions MergeOptions { get; set; } = new PolylineMergeOptions
        {
            NodeTolerance = CadConfig.Algorithms.PipeMerge.NodeTolerance,
            MinPolylineLength = CadConfig.Algorithms.PipeMerge.MinPolylineLength,
            MergeAttributeNames = new List<string>(CadConfig.Algorithms.PipeMerge.MergeAttributeNames ?? new List<string> { "WIDTH" }),
            RequireSameLayer = true,
            MergeClosedLoops = true,
            RemoveDuplicateConsecutivePoints = true
        };

        /// <summary>
        /// 最近一次核查生成的完整结构化报告。
        /// </summary>
        public MatchPipeCheckReport LastReport { get; private set; }

        public MatchPipeService(AcadDb.Database db, Editor ed = null) : base(db, ed)
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
        /// 执行全量规划管与勘测管分段匹配及管径核查（ICheckRule.CheckAll）。
        /// </summary>
        public Dictionary<ObjectId, CheckIssueItem> CheckAll(ProgressContext context = null)
        {
            var session = DocumentSessionManager.GetOrCreateSession(_db);
            var datasetContext = session?.DatasetContext ?? new PipeDatasetContext();

            var existingByIssueId = new Dictionary<string, CheckIssueItem>(StringComparer.OrdinalIgnoreCase);
            if (session?.IssuesById != null)
            {
                foreach (var kvp in session.IssuesById)
                {
                    if (kvp.Value != null && string.Equals(kvp.Value.RuleId, RuleId, StringComparison.OrdinalIgnoreCase))
                    {
                        existingByIssueId[kvp.Key] = kvp.Value;
                    }
                }
            }

            MatchPipeCheckReport report = InspectPlanVsSurveyPipes(
                datasetContext,
                OverrideSurveyDatabase,
                existingByIssueId,
                context);

            LastReport = report;

            if (session != null && report != null)
            {
                session.MergeRuleIssueItems(RuleId, report.AllIssueItems, isIncremental: false);
            }

            return report?.IssueMap ?? new Dictionary<ObjectId, CheckIssueItem>();
        }

        /// <summary>
        /// 执行局部增量核查（ICheckRule.CheckIncremental）：
        /// 当用户在 CAD 图面修改了规划管线或修改了规划管径文字标注（如将 'd400' 改为 'd600'）后，
        /// 直接利用内存静态缓存的勘测管 STRtree 毫秒级重算受影响管线的管径一致性，并将已一致的子问题自动标记为 Resolved。
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
            var datasetContext = session?.DatasetContext ?? new PipeDatasetContext();

            var existingByIssueId = new Dictionary<string, CheckIssueItem>(StringComparer.OrdinalIgnoreCase);
            if (session?.IssuesById != null)
            {
                foreach (var kvp in session.IssuesById)
                {
                    if (kvp.Value != null && string.Equals(kvp.Value.RuleId, RuleId, StringComparison.OrdinalIgnoreCase))
                    {
                        existingByIssueId[kvp.Key] = kvp.Value;
                    }
                }
            }
            foreach (var kvp in mergedIssues)
            {
                if (kvp.Value != null && string.Equals(kvp.Value.RuleId, RuleId, StringComparison.OrdinalIgnoreCase))
                {
                    string issueId = DocumentCheckSession.EnsureIssueId(kvp.Value, RuleId, 0);
                    if (!existingByIssueId.ContainsKey(issueId))
                    {
                        existingByIssueId[issueId] = kvp.Value;
                    }
                }
            }

            MatchPipeCheckReport latestReport = InspectPlanVsSurveyPipes(
                datasetContext,
                OverrideSurveyDatabase,
                existingByIssueId,
                context);
            LastReport = latestReport;

            var latestByIssueId = new Dictionary<string, CheckIssueItem>(StringComparer.OrdinalIgnoreCase);
            foreach (CheckIssueItem item in latestReport.AllIssueItems)
            {
                if (item == null) continue;
                string iid = DocumentCheckSession.EnsureIssueId(item, RuleId, 0);
                latestByIssueId[iid] = item;
            }

            var updatedAllItems = new List<CheckIssueItem>();

            // 1. 对旧问题中已在最新比对中消除（管径已一致或图元已删除）的子项，自动归档为 Resolved（已修复）
            foreach (var kvp in existingByIssueId)
            {
                string oldIssueId = kvp.Key;
                CheckIssueItem oldIssue = kvp.Value;
                if (oldIssue == null) continue;

                ObjectId oid = oldIssue.TargetObjectId;
                if (!oid.IsNull && (!oid.IsValid || oid.IsErased))
                {
                    oldIssue.Status = IssueReviewStatus.Resolved;
                    oldIssue.Remark = "规划管线图元已删除（已归档）";
                    updatedAllItems.Add(oldIssue);
                    mergedIssues[oid] = oldIssue;
                    continue;
                }

                if (!latestByIssueId.ContainsKey(oldIssueId))
                {
                    oldIssue.Status = IssueReviewStatus.Resolved;
                    if (!oid.IsNull && datasetContext.PlanPipesById.TryGetValue(oid, out EnrichedPlanPipeInfo enriched) &&
                        enriched?.TextDiameter.HasValue == true)
                    {
                        oldIssue.Diameter = enriched.TextDiameter.Value;
                        oldIssue.Description = $"规划管 [{enriched.Handle}] 标注管径已更新为 DN{enriched.TextDiameter.Value:0}，与勘测管管径一致";
                        oldIssue.Remark = $"{oldIssue.Description}（已修复）";
                    }
                    else
                    {
                        oldIssue.Remark = "规划管径已与勘测管一致（已修复）";
                    }
                    updatedAllItems.Add(oldIssue);
                    if (!oid.IsNull)
                    {
                        mergedIssues[oid] = oldIssue;
                    }
                }
            }

            // 2. 合并最新仍处于待核查的子问题
            foreach (CheckIssueItem newItem in latestReport.AllIssueItems)
            {
                if (newItem == null) continue;
                updatedAllItems.Add(newItem);
                if (!newItem.TargetObjectId.IsNull)
                {
                    mergedIssues[newItem.TargetObjectId] = newItem;
                }
            }

            if (session != null)
            {
                session.MergeRuleIssueItems(RuleId, updatedAllItems, isIncremental: true, incrementalTargetIds: targetIds);
            }

            return mergedIssues;
        }

        /// <summary>
        /// 变更影响范围追踪（ICheckRule.GetAffectedTargetIds）：
        /// 监听规划管线（Polyline/Line）以及规划管径文字标注（DBText/MText）的增删改。
        /// </summary>
        public IEnumerable<ObjectId> GetAffectedTargetIds(DBObject changedObj)
        {
            var affected = new HashSet<ObjectId>();
            if (changedObj == null)
                return affected;

            try
            {
                Entity ent = changedObj as Entity;
                if (ent == null || string.IsNullOrWhiteSpace(ent.Layer))
                    return affected;

                string layer = ent.Layer.Trim();
                var planPipeLayers = CadConfig.PlanningData.GetAllPipeLayers();
                var planTextLayers = CadConfig.PlanningData.GetAllTextLayers();

                if ((ent is Polyline || ent is Autodesk.AutoCAD.DatabaseServices.Line) &&
                    planPipeLayers.Contains(layer))
                {
                    affected.Add(ent.ObjectId);
                }
                else if ((ent is DBText || ent is MText) && planTextLayers.Contains(layer))
                {
                    if (_textToPlanPipeIds.TryGetValue(ent.ObjectId, out HashSet<ObjectId> linkedPipes) && linkedPipes != null)
                    {
                        foreach (ObjectId pid in linkedPipes)
                        {
                            if (!pid.IsNull)
                                affected.Add(pid);
                        }
                    }

                    var session = DocumentSessionManager.GetOrCreateSession(_db);
                    if (session != null)
                    {
                        foreach (ObjectId pid in session.DatasetContext.PlanPipesById.Keys)
                        {
                            if (!pid.IsNull)
                                affected.Add(pid);
                        }
                    }
                }
            }
            catch
            {
                // 忽略瞬态事务读取异常
            }

            return affected;
        }

        protected override void OnSuccess(Dictionary<ObjectId, CheckIssueItem> result, bool bOnlyUpdate = false)
        {
            var session = DocumentSessionManager.GetOrCreateSession(_db);

            if (bOnlyUpdate)
            {
                if (session != null && session.IssuesById.Count > 0)
                {
                    palCheckPipe.Instance.UpdateIssueItems(session.GetAllIssueItems(), isIncremental: true);
                }
                else
                {
                    palCheckPipe.Instance.UpdateIncrementalData(result ?? new Dictionary<ObjectId, CheckIssueItem>());
                }
                return;
            }

            if (LastReport != null)
            {
                Ed?.WriteMessage(
                    $"\n[{RuleName}] 核查完成：共扫描三类规划管线 {LastReport.TotalPlanPipeCount} 条，" +
                    $"勘测管 {LastReport.SurveyRawPolylineCount} 条（按管径合并后 {LastReport.SurveyMergedPolylineCount} 条）；" +
                    $"管径一致分段 {LastReport.DiameterConsistentItems.Count} 处，" +
                    $"检出待核查问题 {LastReport.AllIssueItems.Count} 项（管径不符：{LastReport.DiameterMismatchItems.Count}，匹配段缺标注：{LastReport.MissingPlanLabelOnMatchedItems.Count}，现状管未匹配勘测管：{LastReport.UnmatchedExistingPipeItems.Count}）。\n");
            }

            if (session != null && session.IssuesById.Count > 0)
            {
                palCheckPipe.Instance.ShowIssueItems(session.GetAllIssueItems());
            }
            else if (result != null)
            {
                palCheckPipe.Instance.Show(result);
            }
        }

        /// <summary>
        /// 核心编排方法：串联 <see cref="PipeDatasetContext"/> 中的三类规划管（含文字管径）与只读合并勘测管（含静态 STRtree），
        /// 执行分段匹配与管径一致性校验，并产出完整报表与多子项（SubIndex）核查问题。
        /// </summary>
        public MatchPipeCheckReport InspectPlanVsSurveyPipes(
            PipeDatasetContext datasetContext = null,
            AcadDb.Database explicitSurveyDb = null,
            Dictionary<string, CheckIssueItem> existingIssuesById = null,
            ProgressContext progress = null)
        {
            var ctx = datasetContext ?? DocumentSessionManager.GetOrCreateSession(_db)?.DatasetContext ?? new PipeDatasetContext();
            var report = new MatchPipeCheckReport();

            // 1. 组装规划管线（颜色优先分类 + 文字标注管径提取）
            var planCheckSvc = new PlanDataCheckService(_db, _ed);
            planCheckSvc.InspectPlanningData(CadConfig.PlanningData, ctx, null, progress);

            PipeSpatialMatchSummary textMatchSummary = _spatialMatcher.MatchAndEnrichPlanTextLabels(
                _db,
                ctx,
                CadConfig.PlanningData);

            RebuildTextToPlanPipeMap(textMatchSummary);

            // 2. 获取或构建只读勘测数据静态缓存（RawPolylines + 按 WIDTH 合并后的 MergedPolylines + MergedPolylineIndex）
            SurveyDatasetCacheEntry surveyCache = ResolveAndGetSurveyCache(ctx, explicitSurveyDb);
            if (surveyCache == null)
            {
                return report;
            }

            report.SurveyRawPolylineCount = surveyCache.RawPolylines?.Count ?? 0;
            report.SurveyMergedPolylineCount = surveyCache.MergedPolylines?.Count ?? 0;
            report.SurveyCacheKey = surveyCache.CacheKey ?? string.Empty;

            var matchOpts = MatchOptions ?? new PolylineMatchOptions();

            // 3. 遍历全部三类规划管线（新建 PlanNew、现状 Existing、改扩建 PlanExpand）
            var orderedPlanPipes = ctx.PlanPipesById.Values
                .Where(p => p != null && p.Polyline != null && GeometryHelper.IsValidPolylineGeometry(p.Polyline))
                .OrderBy(p => p.Handle, StringComparer.OrdinalIgnoreCase)
                .ToList();

            report.TotalPlanPipeCount = orderedPlanPipes.Count;

            foreach (EnrichedPlanPipeInfo planInfo in orderedPlanPipes)
            {
                if (progress != null && progress.Token.IsCancellationRequested)
                    break;

                string catId = !string.IsNullOrWhiteSpace(planInfo.ResolvedCategoryId)
                    ? planInfo.ResolvedCategoryId
                    : "Existing";
                string catName = !string.IsNullOrWhiteSpace(planInfo.ResolvedCategoryName)
                    ? planInfo.ResolvedCategoryName
                    : "现状管";

                if (!report.PlanCountByCategory.ContainsKey(catId))
                {
                    report.PlanCountByCategory[catId] = 0;
                    report.MatchedPipeCountByCategory[catId] = 0;
                    report.UnmatchedPipeCountByCategory[catId] = 0;
                }
                report.PlanCountByCategory[catId]++;

                CadPolyline targetPoly = planInfo.Polyline;
                double? planWidth = planInfo.TextDiameter ?? targetPoly.GetAttributeDouble("WIDTH");
                double? planWidthMax = targetPoly.GetAttributeDouble("WIDTH_MAX");
                string rawLabel = planInfo.RawLabelText ?? targetPoly.GetAttributeString("RAW_LABEL") ?? string.Empty;

                // 利用已构建好的勘测管静态 STRtree 索引直接查询分段匹配
                List<CompareResult> segMatches = FindSegmentMatchesForTarget(
                    targetPoly,
                    surveyCache.MergedPolylineIndex,
                    _geometryFactory,
                    matchOpts);

                var validMatches = (segMatches ?? new List<CompareResult>())
                    .Where(m => m != null && m.IsMatched && m.BestBasePolyline != null)
                    .ToList();

                if (validMatches.Count == 0)
                {
                    report.UnmatchedPipeCountByCategory[catId]++;

                    Point3d repPt = GetMidPointOnLineString(targetPoly.Geometry, null);
                    var unmatchedItem = new MatchPipeSegmentCheckItem
                    {
                        PlanPipeId = planInfo.PipeId,
                        PlanHandle = planInfo.Handle,
                        PlanLayerName = planInfo.LayerName,
                        CategoryId = catId,
                        CategoryName = catName,
                        SubIndex = 0,
                        PlanPipeLength = targetPoly.Length,
                        PlanDiameter = planWidth,
                        PlanSecondaryDiameter = planWidthMax,
                        PlanRawLabel = rawLabel,
                        IsMatchedToSurvey = false,
                        RepresentativeLocation = repPt
                    };

                    report.AllSegmentItems.Add(unmatchedItem);

                    // 若为【现状管】且在勘测管中完全未找到对应管段，则生成“现状管未匹配勘测管”待核查问题
                    if (string.Equals(catId, "Existing", StringComparison.OrdinalIgnoreCase))
                    {
                        report.UnmatchedExistingPipeItems.Add(unmatchedItem);

                        string planWidthDesc = planWidth.HasValue ? $"DN{planWidth.Value:0}" : "无文字标注";
                        string desc =
                            $"现状管未匹配到勘测管：[{planInfo.LayerName}] 规划现状管 [{planInfo.Handle}] (标注={planWidthDesc}, L={targetPoly.Length:F1}m) 在容差 {matchOpts.Tolerance:F1}m 内无同源勘测管";

                        CheckIssueItem issue = CreateMatchPipeIssue(
                            unmatchedItem,
                            ProblemType.NoAdjacentItems,
                            ProblemLevel.Warning,
                            desc,
                            existingIssuesById);

                        report.AllIssueItems.Add(issue);
                        if (!planInfo.PipeId.IsNull)
                        {
                            report.IssueMap[planInfo.PipeId] = issue;
                        }
                    }

                    continue;
                }

                report.MatchedPipeCountByCategory[catId]++;

                for (int subIdx = 0; subIdx < validMatches.Count; subIdx++)
                {
                    CompareResult segMatch = validMatches[subIdx];
                    CadPolyline surveyPoly = segMatch.BestBasePolyline;
                    double? surveyWidth = surveyPoly.GetAttributeDouble("WIDTH");
                    double? surveyHeight = surveyPoly.GetAttributeDouble("HEITH");
                    var surveyHandles = surveyPoly.SourceHandles != null
                        ? new List<string>(surveyPoly.SourceHandles)
                        : new List<string>();

                    Point3d segMidPt = GetMidPointOnLineString(targetPoly.Geometry, segMatch.CoveredIntervalOnTarget);
                    MatchItem helperLine = BuildSegmentHelperLine(segMidPt, surveyPoly.Geometry);

                    bool isMissingPlanLabel = !planWidth.HasValue && surveyWidth.HasValue;
                    bool isDiameterMatch = false;

                    if (planWidth.HasValue && surveyWidth.HasValue)
                    {
                        if (Math.Abs(planWidth.Value - surveyWidth.Value) <= DefaultDiameterToleranceMm)
                        {
                            isDiameterMatch = true;
                        }
                        else if (planWidthMax.HasValue &&
                                 surveyWidth.Value >= Math.Min(planWidth.Value, planWidthMax.Value) - DefaultDiameterToleranceMm &&
                                 surveyWidth.Value <= Math.Max(planWidth.Value, planWidthMax.Value) + DefaultDiameterToleranceMm)
                        {
                            isDiameterMatch = true;
                        }
                    }

                    var segItem = new MatchPipeSegmentCheckItem
                    {
                        PlanPipeId = planInfo.PipeId,
                        PlanHandle = planInfo.Handle,
                        PlanLayerName = planInfo.LayerName,
                        CategoryId = catId,
                        CategoryName = catName,
                        SubIndex = subIdx,
                        PlanPipeLength = targetPoly.Length,
                        PlanDiameter = planWidth,
                        PlanSecondaryDiameter = planWidthMax,
                        PlanRawLabel = rawLabel,
                        IsMatchedToSurvey = true,
                        SurveyHandles = surveyHandles,
                        SurveyDiameter = surveyWidth,
                        SurveyHeight = surveyHeight,
                        TargetInterval = segMatch.CoveredIntervalOnTarget,
                        SurveyInterval = segMatch.CoveredIntervalOnBase,
                        AverageDistance = segMatch.AverageDistance,
                        MaxDistance = segMatch.MaxDistance,
                        Score = segMatch.Score,
                        IsDiameterConsistent = isDiameterMatch,
                        IsMissingPlanDiameterOnMatched = isMissingPlanLabel,
                        RepresentativeLocation = segMidPt,
                        HelperLine = helperLine,
                        RawCompareResult = segMatch
                    };

                    report.AllSegmentItems.Add(segItem);
                    if (validMatches.Count > 1)
                    {
                        report.MultiSegmentPipeItems.Add(segItem);
                    }

                    string surveyHandlesSummary = FormatHandlesSummary(surveyHandles);
                    string intervalDesc = segMatch.CoveredIntervalOnTarget != null
                        ? $"[{segMatch.CoveredIntervalOnTarget.Start:F1}m~{segMatch.CoveredIntervalOnTarget.End:F1}m]"
                        : $"[0~{targetPoly.Length:F1}m]";

                    if (isDiameterMatch)
                    {
                        report.DiameterConsistentItems.Add(segItem);
                    }
                    else if (planWidth.HasValue && surveyWidth.HasValue)
                    {
                        // 管径不一致：生成带 SubIndex 的核查问题
                        report.DiameterMismatchItems.Add(segItem);

                        string segPrefix = validMatches.Count > 1 ? $"分段#{subIdx + 1} " : string.Empty;
                        string desc =
                            $"{segPrefix}管径与勘测管不符：[{catName}] 规划管 [{planInfo.Handle}] 标注 DN{planWidth.Value:0}('{rawLabel}') vs 勘测管 [{surveyHandlesSummary}] DN{surveyWidth.Value:0}，区间 {intervalDesc} (均距={segMatch.AverageDistance:F2}m)";

                        CheckIssueItem issue = CreateMatchPipeIssue(
                            segItem,
                            ProblemType.DirectionConflict,
                            ProblemLevel.Error,
                            desc,
                            existingIssuesById);

                        report.AllIssueItems.Add(issue);
                        if (!planInfo.PipeId.IsNull && !report.IssueMap.ContainsKey(planInfo.PipeId))
                        {
                            report.IssueMap[planInfo.PipeId] = issue;
                        }
                    }
                    else if (isMissingPlanLabel)
                    {
                        // 规划管缺少文字管径标注，但匹配上了有管径的勘测管
                        report.MissingPlanLabelOnMatchedItems.Add(segItem);

                        string segPrefix = validMatches.Count > 1 ? $"分段#{subIdx + 1} " : string.Empty;
                        string desc =
                            $"{segPrefix}规划管缺管径标注（已匹配勘测管）：[{catName}] 规划管 [{planInfo.Handle}] 未提取到文字管径，对应勘测管 [{surveyHandlesSummary}] 管径为 DN{surveyWidth.Value:0}，区间 {intervalDesc}";

                        CheckIssueItem issue = CreateMatchPipeIssue(
                            segItem,
                            ProblemType.NoAdjacentItems,
                            ProblemLevel.Warning,
                            desc,
                            existingIssuesById);

                        report.AllIssueItems.Add(issue);
                        if (!planInfo.PipeId.IsNull && !report.IssueMap.ContainsKey(planInfo.PipeId))
                        {
                            report.IssueMap[planInfo.PipeId] = issue;
                        }
                    }
                }
            }

            return report;
        }

        private SurveyDatasetCacheEntry ResolveAndGetSurveyCache(
            PipeDatasetContext ctx,
            AcadDb.Database explicitSurveyDb)
        {
            var surveyLayers = CadConfig.SurveyData.PipeLayers ?? new List<string> { "勘测管" };

            if (explicitSurveyDb != null)
            {
                return ctx.GetOrBuildSurveyCache(explicitSurveyDb, surveyLayers, _geometryFactory, MergeOptions);
            }

            if (ctx.SurveyDataSource == null || !ctx.SurveyDataSource.IsValid)
            {
                _dataSourceService.TryAutoResolveDataSources(_db, ctx, needPreviousData: false, needSurveyData: true);
            }

            if (ctx.SurveyDataSource == null || !ctx.SurveyDataSource.IsValid)
            {
                return null;
            }

            AcadDb.Database surveyDb = null;
            bool shouldDispose = false;
            try
            {
                surveyDb = _dataSourceService.OpenOrReadDatabase(ctx.SurveyDataSource, out shouldDispose);
                if (surveyDb == null)
                    return null;

                return ctx.GetOrBuildSurveyCache(surveyDb, ctx.SurveyDataSource.PipeLayers ?? surveyLayers, _geometryFactory, MergeOptions);
            }
            finally
            {
                if (shouldDispose && surveyDb != null)
                {
                    surveyDb.Dispose();
                }
            }
        }

        private void RebuildTextToPlanPipeMap(PipeSpatialMatchSummary textSummary)
        {
            _textToPlanPipeIds.Clear();
            if (textSummary == null || textSummary.MatchByPipeId == null)
                return;

            foreach (var match in textSummary.MatchByPipeId.Values)
            {
                if (match?.Pipe == null || match.Pipe.PipeId.IsNull)
                    continue;

                ObjectId pipeId = match.Pipe.PipeId;
                if (match.MatchedAttachment != null && !match.MatchedAttachment.AttachmentId.IsNull)
                {
                    ObjectId attId = match.MatchedAttachment.AttachmentId;
                    if (!_textToPlanPipeIds.TryGetValue(attId, out HashSet<ObjectId> set))
                    {
                        set = new HashSet<ObjectId>();
                        _textToPlanPipeIds[attId] = set;
                    }
                    set.Add(pipeId);
                }

                if (match.PossibleMatches != null)
                {
                    foreach (MatchItem pm in match.PossibleMatches)
                    {
                        if (pm.Id.IsNull) continue;
                        if (!_textToPlanPipeIds.TryGetValue(pm.Id, out HashSet<ObjectId> set))
                        {
                            set = new HashSet<ObjectId>();
                            _textToPlanPipeIds[pm.Id] = set;
                        }
                        set.Add(pipeId);
                    }
                }
            }
        }

        private CheckIssueItem CreateMatchPipeIssue(
            MatchPipeSegmentCheckItem segItem,
            ProblemType problemType,
            ProblemLevel level,
            string description,
            Dictionary<string, CheckIssueItem> existingIssuesById)
        {
            string issueId = $"{RuleId}:{segItem.PlanHandle}:{segItem.SubIndex}";
            IssueReviewStatus status = IssueReviewStatus.Pending;
            string remark = description;

            if (existingIssuesById != null && existingIssuesById.TryGetValue(issueId, out CheckIssueItem oldIssue) && oldIssue != null)
            {
                if (oldIssue.Status == IssueReviewStatus.Ignored || oldIssue.Status == IssueReviewStatus.ToRecheck)
                {
                    status = oldIssue.Status;
                    if (!string.IsNullOrWhiteSpace(oldIssue.Remark))
                        remark = oldIssue.Remark;
                }
            }

            var helperLines = new List<MatchItem>();
            if (segItem.HelperLine != null)
            {
                helperLines.Add(segItem.HelperLine);
            }

            return new CheckIssueItem
            {
                IssueId = issueId,
                RuleId = RuleId,
                RuleName = RuleName,
                Type = problemType,
                Level = level,
                TargetObjectId = segItem.PlanPipeId,
                TargetHandle = segItem.PlanHandle,
                LayerName = segItem.PlanLayerName,
                Location = segItem.RepresentativeLocation,
                Diameter = segItem.PlanDiameter ?? segItem.SurveyDiameter ?? 0.0,
                Height = segItem.SurveyHeight ?? 0.0,
                Description = description,
                Remark = remark,
                Status = status,
                PossibleMatches = helperLines,
                RulePayload = segItem
            };
        }

        private static Point3d GetMidPointOnLineString(LineString line, SegmentInterval interval)
        {
            if (line == null || line.IsEmpty)
                return Point3d.Origin;

            double midMeasure = interval != null
                ? (interval.Start + interval.End) * 0.5
                : line.Length * 0.5;

            Coordinate c = GetPointAtMeasure(line, midMeasure);
            return c != null ? new Point3d(c.X, c.Y, 0.0) : Point3d.Origin;
        }

        private static MatchItem BuildSegmentHelperLine(Point3d planMidPt, LineString surveyLine)
        {
            if (surveyLine == null || surveyLine.IsEmpty)
                return null;

            ProjectionResult proj = ProjectPointToLineStringMeasure(new Coordinate(planMidPt.X, planMidPt.Y), surveyLine);
            if (proj == null || proj.Coordinate == null)
                return null;

            return new MatchItem(
                ObjectId.Null,
                new Point3d(proj.Coordinate.X, proj.Coordinate.Y, 0.0),
                proj.Distance,
                planMidPt,
                0.0,
                0.0,
                true);
        }

        private static string FormatHandlesSummary(List<string> handles)
        {
            if (handles == null || handles.Count == 0)
                return "-";
            if (handles.Count <= 3)
                return string.Join(",", handles);
            return $"{string.Join(",", handles.Take(3))}等{handles.Count}段";
        }

        public static List<CompareResult> PipeMatcher(
            List<CadPolyline> baseLayerPolylines,
            List<CadPolyline> targetLayerPolylines,
            GeometryFactory geometryFactory,
            PolylineMatchOptions match_options,
            PolylineMergeOptions merge_options)
        {
            if (baseLayerPolylines == null)
                baseLayerPolylines = new List<CadPolyline>();

            if (targetLayerPolylines == null)
                targetLayerPolylines = new List<CadPolyline>();

            if (match_options == null)
                match_options = new PolylineMatchOptions();

            if (merge_options == null)
                merge_options = new PolylineMergeOptions();

            // ============================================================
            // 1. A 是基准图层，必须先 merge。
            // ============================================================
            var merger = new PolylineGraphMerger(geometryFactory);
            var mergedBasePolylines = merger.Merge(baseLayerPolylines, merge_options);

            // ============================================================
            // 2. 用 merged A 建 STRtree。
            // ============================================================
            STRtree<CadPolyline> mergedBaseIndex =
                BuildPolylineIndex(mergedBasePolylines);

            return PipeMatcherWithPremergedIndex(
                mergedBaseIndex,
                targetLayerPolylines,
                geometryFactory,
                match_options);
        }

        /// <summary>
        /// 使用已在 <see cref="PipeDatasetContext"/> 中预合并并构建好的静态勘测管 <see cref="STRtree{CadPolyline}"/> 直接执行分段匹配，
        /// 避免重复执行耗时的拓扑合并与空间索引构建。
        /// </summary>
        public static List<CompareResult> PipeMatcherWithPremergedIndex(
            STRtree<CadPolyline> mergedBaseIndex,
            List<CadPolyline> targetLayerPolylines,
            GeometryFactory geometryFactory,
            PolylineMatchOptions match_options)
        {
            if (targetLayerPolylines == null)
                targetLayerPolylines = new List<CadPolyline>();

            if (match_options == null)
                match_options = new PolylineMatchOptions();

            var results = new List<CompareResult>();

            foreach (CadPolyline targetPolyline in targetLayerPolylines)
            {
                if (!GeometryHelper.IsValidPolylineGeometry(targetPolyline))
                    continue;

                List<CompareResult> targetResults =
                    FindSegmentMatchesForTarget(
                        targetPolyline,
                        mergedBaseIndex,
                        geometryFactory,
                        match_options);

                if (targetResults == null || targetResults.Count == 0)
                {
                    results.Add(new CompareResult
                    {
                        TargetPolyline = targetPolyline
                    });
                }
                else
                {
                    results.AddRange(targetResults);
                }
            }

            return results;
        }

        /// <summary>
        /// 为当前 target 寻找一组分段匹配结果。
        /// </summary>
        private static List<CompareResult> FindSegmentMatchesForTarget(
            CadPolyline targetPolyline,
            STRtree<CadPolyline> mergedBaseIndex,
            GeometryFactory geometryFactory,
            PolylineMatchOptions options)
        {
            var empty = new List<CompareResult>();

            if (!GeometryHelper.IsValidPolylineGeometry(targetPolyline))
                return empty;

            List<CadPolyline> candidates =
                QueryCandidateBases(
                    mergedBaseIndex,
                    targetPolyline,
                    options);

            if (candidates == null || candidates.Count == 0)
                return empty;

            var candidateResults = new List<CompareResult>();

            foreach (CadPolyline candidateBase in candidates)
            {
                if (!GeometryHelper.IsValidPolylineGeometry(candidateBase))
                    continue;

                if (options.UseBufferRoughFilter)
                {
                    if (!PassBufferRoughFilter(
                            candidateBase,
                            targetPolyline,
                            options))
                    {
                        continue;
                    }
                }

                CompareResult current =
                    ScoreMatch(
                        targetPolyline,
                        candidateBase,
                        options);

                if (current == null)
                    continue;

                if (!current.IsMatched)
                    continue;

                if (current.CoveredIntervalOnTarget == null)
                    continue;

                candidateResults.Add(current);
            }

            if (candidateResults.Count == 0)
                return empty;

            // 从所有候选结果中挑选一组不明显重叠的 target 区间。
            // 这样同一条 target 可以被多个 base 分段覆盖。
            return SelectNonOverlappingTargetMatches(
                candidateResults,
                options);
        }

        // 建 base 的空间索引
        private static STRtree<CadPolyline> BuildPolylineIndex(
            IEnumerable<CadPolyline> polylines)
        {
            var index = new STRtree<CadPolyline>();

            if (polylines == null)
                return index;

            foreach (CadPolyline polyline in polylines)
            {
                if (!GeometryHelper.IsValidPolylineGeometry(polyline))
                    continue;

                Envelope env = polyline.Geometry.EnvelopeInternal;

                if (env == null || env.IsNull)
                    continue;

                index.Insert(env, polyline);
            }

            index.Build();

            return index;
        }

        // 为当前这条 target 多段线，寻找最匹配的 merged base 多段线
        private static CompareResult FindBestMergedBase(
            CadPolyline targetPolyline,
            STRtree<CadPolyline> mergedBaseIndex,
            GeometryFactory geometryFactory,
            PolylineMatchOptions options)
        {
            var emptyResult = new CompareResult
            {
                TargetPolyline = targetPolyline
            };

            if (targetPolyline.SourceHandles[0] == "520B") {
                Console.WriteLine("debug");
            }

            if (!GeometryHelper.IsValidPolylineGeometry(targetPolyline))
                return emptyResult;

            List<CadPolyline> candidates =
                QueryCandidateBases(
                    mergedBaseIndex,
                    targetPolyline,
                    options);

            CompareResult best = null;

            foreach (CadPolyline candidateBase in candidates)
            {
                if (!GeometryHelper.IsValidPolylineGeometry(candidateBase))
                    continue;

                if (options.UseBufferRoughFilter)
                {
                    if (!PassBufferRoughFilter(
                            candidateBase,
                            targetPolyline,
                            options))
                    {
                        continue;
                    }
                }

                CompareResult current =
                    ScoreMatch(
                        targetPolyline,
                        candidateBase,
                        //geometryFactory,
                        options);

                if (current == null)
                    continue;

                if (!current.IsMatched)
                    continue;

                if (best == null || current.Score < best.Score)
                    best = current;
            }

            return best ?? emptyResult;
        }

        private static List<CadPolyline> QueryCandidateBases(
            STRtree<CadPolyline> mergedBaseIndex,
            CadPolyline targetPolyline,
            PolylineMatchOptions options)
        {
            var result = new List<CadPolyline>();

            if (mergedBaseIndex == null)
                return result;

            if (!GeometryHelper.IsValidPolylineGeometry(targetPolyline))
                return result;

            double expand = options.CandidateSearchExpand > 0
                ? options.CandidateSearchExpand
                : options.Tolerance;

            Envelope searchEnv = new Envelope(
                targetPolyline.Geometry.EnvelopeInternal);

            searchEnv.ExpandBy(expand);

            IList<CadPolyline> queryResult =
                mergedBaseIndex.Query(searchEnv);

            if (queryResult == null || queryResult.Count == 0)
                return result;

            foreach (CadPolyline candidate in queryResult)
            {
                if (!GeometryHelper.IsValidPolylineGeometry(candidate))
                    continue;

                result.Add(candidate);
            }

            return result;
        }

        // 是否通过 Buffer 粗筛
        private static bool PassBufferRoughFilter(
            CadPolyline basePolyline,
            CadPolyline targetPolyline,
            PolylineMatchOptions options)
        {
            if (!GeometryHelper.IsValidPolylineGeometry(basePolyline))
                return false;

            if (!GeometryHelper.IsValidPolylineGeometry(targetPolyline))
                return false;

            double tolerance = options.Tolerance;

            if (tolerance <= 0)
                return false;

            NtsGeometry baseGeometry = basePolyline.Geometry;
            NtsGeometry targetGeometry = targetPolyline.Geometry;

            if (baseGeometry == null || targetGeometry == null)
                return false;

            /*
             * 先做 Envelope 预筛。
             * 
             * 这一步比 Buffer 便宜很多。
             * 如果扩展 tolerance 之后的外包框都不相交，
             * 那么后面不需要再做 Buffer/Intersection。
             */
            Envelope baseEnv = new Envelope(baseGeometry.EnvelopeInternal);
            Envelope targetEnv = new Envelope(targetGeometry.EnvelopeInternal);

            baseEnv.ExpandBy(tolerance);
            targetEnv.ExpandBy(tolerance);

            if (!baseEnv.Intersects(targetEnv))
                return false;

            NtsGeometry baseBuffer = baseGeometry.Buffer(tolerance);
            NtsGeometry targetBuffer = targetGeometry.Buffer(tolerance);

            if (baseBuffer == null || targetBuffer == null)
                return false;

            if (baseBuffer.IsEmpty || targetBuffer.IsEmpty)
                return false;

            if (!baseBuffer.Intersects(targetBuffer))
                return false;

            NtsGeometry intersection = baseBuffer.Intersection(targetBuffer);

            if (intersection == null || intersection.IsEmpty)
                return false;

            double baseArea = baseBuffer.Area;
            double targetArea = targetBuffer.Area;

            if (baseArea <= 0 || targetArea <= 0)
                return false;

            /*
             * 关键说明：
             * 
             * 这里必须用较小 buffer 面积作为分母。
             * 
             * 如果 target 比 base 长，但 base 是 target 的一部分，
             * intersection 可能接近 baseBuffer.Area。
             * 
             * 如果此时用 targetBuffer.Area 作为分母，overlapRatio 会很低，
             * 从而误杀包含型匹配。
             */
            double minArea = Math.Min(baseArea, targetArea);

            double overlapRatio = intersection.Area / minArea;

            return overlapRatio >= options.MinBufferOverlapRatio;
        }

        /// <summary>
        /// 评估当前 target 与某条候选 base 的匹配程度。
        /// 
        /// 重要修改：
        /// 原算法把 target 全长都拿去和 base 比较，
        /// 如果 target 比 base 长，target 多出来的部分会被统计为 bad point。
        /// 
        /// 新算法改为：
        /// 沿 target 采样，找出当前 base 能覆盖 target 的连续区间。
        /// 当前 base 覆盖不到的 target 区间，不在这里作为 bad point 处理，
        /// 而是留给其他 base 继续匹配。
        /// </summary>
        private static CompareResult ScoreMatch(
            CadPolyline targetPolyline,
            CadPolyline candidateBasePolyline,
            PolylineMatchOptions options)
        {
            if (!GeometryHelper.IsValidPolylineGeometry(targetPolyline))
                return null;

            if (!GeometryHelper.IsValidPolylineGeometry(candidateBasePolyline))
                return null;

            LineString targetLine = targetPolyline.Geometry as LineString;
            LineString baseLine = candidateBasePolyline.Geometry as LineString;

            if (targetLine == null || baseLine == null)
                return null;

            if (targetLine.Length <= 0 || baseLine.Length <= 0)
                return null;

            List<MeasuredSamplePoint> samplePoints =
                SampleLineStringByStepWithMeasure(
                    targetLine,
                    options.SampleStep);

            if (samplePoints.Count == 0)
                return null;

            /*
             * acceptedRuns 的含义：
             * 
             * 每一个 run 表示 target 上一段连续的、可以被当前 base 覆盖的采样点。
             * 
             * 例如 target 很长，当前 base 只覆盖中间一段：
             * 
             * target:  ------------------------------
             * base:          ------------
             * accepted:      [xxxxxxxxxx]
             * 
             * 那么这里只会生成中间那一段 run。
             */
            var acceptedRuns = new List<List<AcceptedProjectionSample>>();
            List<AcceptedProjectionSample> currentRun = null;

            foreach (MeasuredSamplePoint sample in samplePoints)
            {
                ProjectionResult projection =
                    ProjectPointToLineStringMeasure(
                        sample.Point,
                        baseLine);

                bool accepted = false;

                if (projection != null &&
                    projection.Distance <= options.Tolerance)
                {
                    /*
                     * 方向一致性检查：
                     * 
                     * 仅仅距离近不够。
                     * 如果 target 和 base 在此处构成明显夹角，
                     * 即使投影距离很小，也不能认为它们是同一条管线。
                     */
                    bool directionAccepted =
                        CadMath.IsDirectionCompatibleAtMeasures(
                            targetLine,
                            sample.Measure,
                            baseLine,
                            projection.Measure,
                            options.MaxDirectionAngleDegrees);

                    accepted = directionAccepted;
                }

                //bool accepted =
                //    projection != null &&
                //    projection.Distance <= options.Tolerance;

                if (accepted)
                {
                    if (currentRun == null)
                    {
                        currentRun = new List<AcceptedProjectionSample>();
                        acceptedRuns.Add(currentRun);
                    }

                    currentRun.Add(new AcceptedProjectionSample
                    {
                        TargetMeasure = sample.Measure,
                        BaseMeasure = projection.Measure,
                        Distance = projection.Distance
                    });
                }
                else
                {
                    /*
                     * 关键修改点：
                     * 
                     * 以前这里会 badCount++。
                     * 
                     * 现在不能这么做。
                     * 因为当前 base 覆盖不到 target 的地方，
                     * 并不代表 target 这一段是坏点，
                     * 它可能应该匹配另一条 base。
                     * 
                     * 所以这里仅仅结束当前连续覆盖段。
                     */
                    currentRun = null;
                }
            }

            if (acceptedRuns.Count == 0)
                return null;

            CompareResult bestRunResult = null;

            foreach (List<AcceptedProjectionSample> run in acceptedRuns)
            {
                if (run == null || run.Count == 0)
                    continue;

                double targetStart = run.Min(x => x.TargetMeasure);
                double targetEnd = run.Max(x => x.TargetMeasure);

                double targetCoveredLength = Math.Max(0.0, targetEnd - targetStart);

                double minRequiredTargetLen = Math.Max(
                    options.MinCoveredLength,
                    Math.Min(options.MinProjectedLength, Math.Min(targetLine.Length, baseLine.Length) * 0.6));

                if (targetCoveredLength < minRequiredTargetLen)
                    continue;

                double baseStart = run.Min(x => x.BaseMeasure);
                double baseEnd = run.Max(x => x.BaseMeasure);

                double baseCoveredLength = Math.Max(0.0, baseEnd - baseStart);

                if (baseCoveredLength < minRequiredTargetLen)
                    continue;

                double averageDistance = run.Average(x => x.Distance);
                double maxDistance = run.Max(x => x.Distance);

                /*
                 * 新语义下：
                 * 当前 run 内的点全部都是 distance <= tolerance 的 accepted 点。
                 * 所以这个分段结果内部的 BadPointRatio 设为 0。
                 * 
                 * target 上未被当前 base 覆盖的部分，不在这里算 bad point，
                 * 而是交给其他 base 继续匹配。
                 */
                double badPointRatio = 0.0;

                var intervalOnTarget = new SegmentInterval(
                    Math.Min(targetStart, targetEnd),
                    Math.Max(targetStart, targetEnd));

                var intervalOnBase = new SegmentInterval(
                    Math.Min(baseStart, baseEnd),
                    Math.Max(baseStart, baseEnd));

                var result = new CompareResult
                {
                    TargetPolyline = targetPolyline,
                    BestBasePolyline = candidateBasePolyline,

                    // 原有字段：当前 target 片段投影到 base 上的区间。
                    CoveredIntervalOnBase = intervalOnBase,

                    // 新增字段：当前 base 覆盖 target 的哪个区间。
                    CoveredIntervalOnTarget = intervalOnTarget,

                    AverageDistance = averageDistance,
                    MaxDistance = maxDistance,
                    BadPointRatio = badPointRatio
                };

                result.Score = CalculateSegmentMatchScore(
                    targetLine,
                    baseLine,
                    intervalOnTarget,
                    intervalOnBase,
                    averageDistance,
                    maxDistance,
                    options);

                if (bestRunResult == null || result.Score < bestRunResult.Score)
                    bestRunResult = result;
            }

            return bestRunResult;
        }

        /// <summary>
        /// 分段匹配评分。
        /// 
        /// 注意：
        /// 这里不再用 target 全长和 base 投影长度做强比较。
        /// 因为当前结果只表示 target 的一个局部区间被当前 base 覆盖。
        /// 
        /// 分数越小越好。
        /// </summary>
        private static double CalculateSegmentMatchScore(
            LineString targetLine,
            LineString baseLine,
            SegmentInterval intervalOnTarget,
            SegmentInterval intervalOnBase,
            double averageDistance,
            double maxDistance,
            PolylineMatchOptions options)
        {
            if (targetLine == null || baseLine == null)
                return double.MaxValue;

            if (intervalOnTarget == null || intervalOnBase == null)
                return double.MaxValue;

            double targetSegmentLength = intervalOnTarget.Length;
            double baseSegmentLength = intervalOnBase.Length;

            if (targetSegmentLength <= 0 || baseSegmentLength <= 0)
                return double.MaxValue;

            /*
             * 分段长度差比例：
             * 
             * 比较的是：
             * 当前被 base 覆盖的 target 区间长度
             * 和
             * 它在 base 上对应的覆盖区间长度。
             * 
             * 而不是 target 全长和 base 全长。
             */
            double lengthDiffRatio =
                Math.Abs(targetSegmentLength - baseSegmentLength) /
                Math.Max(targetSegmentLength, baseSegmentLength);

            /*
             * 覆盖长度奖励：
             * 
             * 在距离差不多的情况下，优先选择覆盖 target 更长的候选。
             * 
             * 注意这是负项，所以覆盖越长，score 越低。
             * 系数不要太大，避免长但不准的匹配压过短但准确的匹配。
             */
            double coverageReward =
                -targetSegmentLength * 0.01;

            double score =
                averageDistance
                + maxDistance * 0.1
                + lengthDiffRatio * options.Tolerance
                + coverageReward;

            return score;
        }

        private static double CalculateMatchScore(
            LineString targetLine,
            LineString baseLine,
            SegmentInterval interval,
            double averageDistance,
            double maxDistance,
            double badPointRatio,
            PolylineMatchOptions options)
        {
            double targetLength = targetLine.Length;
            double projectedLength = interval != null
                ? interval.Length
                : 0;

            double lengthDiffRatio = 1.0;

            if (targetLength > 0)
            {
                lengthDiffRatio =
                    Math.Abs(projectedLength - targetLength) / targetLength;
            }

            double score =
                averageDistance
                + badPointRatio * options.Tolerance * 2.0
                + lengthDiffRatio * options.Tolerance
                + maxDistance * 0.1;

            return score;
        }

        private static List<Coordinate> SampleLineStringByStep(
            LineString line,
            double step)
        {
            var result = new List<Coordinate>();

            if (line == null || line.Length <= 0)
                return result;

            if (step <= 0)
                step = line.Length;

            double length = line.Length;

            Coordinate start = GetPointAtMeasure(line, 0);

            if (start != null)
                result.Add(start);

            double m = step;

            while (m < length)
            {
                Coordinate p = GetPointAtMeasure(line, m);

                if (p != null)
                    result.Add(p);

                m += step;
            }

            Coordinate end = GetPointAtMeasure(line, length);

            if (end != null)
                result.Add(end);

            return RemoveConsecutiveDuplicateCoordinates(result);
        }

        /// <summary>
        /// 按步长采样 LineString，并记录每个采样点在 LineString 上的累计长度位置。
        /// 
        /// 这是对现有 SampleLineStringByStep 的增强版，
        /// 没有替代原函数，避免影响其他代码。
        /// </summary>
        private static List<MeasuredSamplePoint> SampleLineStringByStepWithMeasure(
            LineString line,
            double step)
        {
            var result = new List<MeasuredSamplePoint>();

            if (line == null || line.Length <= 0)
                return result;

            if (step <= 0)
                step = line.Length;

            double length = line.Length;

            Coordinate start = GetPointAtMeasure(line, 0);

            if (start != null)
            {
                result.Add(new MeasuredSamplePoint
                {
                    Point = start,
                    Measure = 0
                });
            }

            double m = step;

            while (m < length)
            {
                Coordinate p = GetPointAtMeasure(line, m);

                if (p != null)
                {
                    result.Add(new MeasuredSamplePoint
                    {
                        Point = p,
                        Measure = m
                    });
                }

                m += step;
            }

            Coordinate end = GetPointAtMeasure(line, length);

            if (end != null)
            {
                result.Add(new MeasuredSamplePoint
                {
                    Point = end,
                    Measure = length
                });
            }

            return result;
        }

        private static Coordinate GetPointAtMeasure(
            LineString line,
            double measure)
        {
            if (line == null)
                return null;

            Coordinate[] coords = line.Coordinates;

            if (coords == null || coords.Length == 0)
                return null;

            if (measure <= 0)
                return new Coordinate(coords[0]);

            double totalLength = line.Length;

            if (measure >= totalLength)
                return new Coordinate(coords[coords.Length - 1]);

            double accumulated = 0;

            for (int i = 0; i < coords.Length - 1; i++)
            {
                Coordinate a = coords[i];
                Coordinate b = coords[i + 1];

                double segLen = a.Distance(b);

                if (segLen <= 0)
                    continue;

                if (accumulated + segLen >= measure)
                {
                    double t = (measure - accumulated) / segLen;

                    return new Coordinate(
                        a.X + (b.X - a.X) * t,
                        a.Y + (b.Y - a.Y) * t);
                }

                accumulated += segLen;
            }

            return new Coordinate(coords[coords.Length - 1]);
        }

        /// <summary>
        /// 从候选分段结果中选择一组尽量不重叠、分数较好的匹配。
        /// 
        /// 简单贪心策略：
        /// 1. 先按 Score 从小到大排序；
        /// 2. 分数相近时，优先选择覆盖 target 更长的；
        /// 3. 如果新候选与已选结果在 target 上明显重叠，则跳过。
        /// 
        /// 这样可以让一条 target 被多个 base 分段覆盖。
        /// </summary>
        private static List<CompareResult> SelectNonOverlappingTargetMatches(
            List<CompareResult> candidates,
            PolylineMatchOptions options)
        {
            var selected = new List<CompareResult>();

            if (candidates == null || candidates.Count == 0)
                return selected;

            double overlapRatioThreshold =
                (options != null && options.overlapRatio > 0)
                    ? options.overlapRatio
                    : 0.5;

            foreach (CompareResult candidate in candidates
                .Where(x => x != null && x.CoveredIntervalOnTarget != null)
                .OrderBy(x => x.Score)
                .ThenByDescending(x => x.CoveredIntervalOnTarget.Length))
            {
                bool conflict = selected.Any(existing =>
                    IsTargetIntervalConflict(
                        existing.CoveredIntervalOnTarget,
                        candidate.CoveredIntervalOnTarget,
                        overlapRatioThreshold));

                if (conflict)
                    continue;

                selected.Add(candidate);
            }

            return selected
                .OrderBy(x => x.CoveredIntervalOnTarget.Start)
                .ToList();
        }

        /// <summary>
        /// 判断两个 target 区间是否冲突。
        /// 
        /// 允许很小的重叠，避免因为采样步长或容差造成边界误差。
        /// </summary>
        private static bool IsTargetIntervalConflict(
            SegmentInterval a,
            SegmentInterval b,
            double overlapRatioThreshold)
        {
            if (a == null || b == null)
                return false;

            double start = Math.Max(a.Start, b.Start);
            double end = Math.Min(a.End, b.End);

            double overlap = end - start;

            if (overlap <= 0)
                return false;

            double shorterLength = Math.Min(a.Length, b.Length);
            if (shorterLength <= 0)
                return false;

            double overlapRatio = overlap / shorterLength;

            // 只有当重叠已经占到较短区间的大部分时，
            // 才认为是真正冲突。
            return overlapRatio >= overlapRatioThreshold;

            //return overlap > overlapTolerance;
        }

        private static List<Coordinate> RemoveConsecutiveDuplicateCoordinates(
            List<Coordinate> coordinates)
        {
            var result = new List<Coordinate>();

            if (coordinates == null || coordinates.Count == 0)
                return result;

            Coordinate last = null;

            foreach (Coordinate c in coordinates)
            {
                if (c == null)
                    continue;

                if (last == null || !last.Equals2D(c))
                {
                    result.Add(c);
                    last = c;
                }
            }

            return result;
        }

        private static ProjectionResult ProjectPointToLineStringMeasure(
            Coordinate point,
            LineString line)
        {
            if (point == null || line == null)
                return null;

            Coordinate[] coords = line.Coordinates;

            if (coords == null || coords.Length < 2)
                return null;

            double accumulated = 0;

            double bestDistance = double.MaxValue;
            double bestMeasure = 0;
            Coordinate bestCoordinate = null;

            for (int i = 0; i < coords.Length - 1; i++)
            {
                Coordinate a = coords[i];
                Coordinate b = coords[i + 1];

                double segLen = a.Distance(b);

                if (segLen <= 0)
                    continue;

                ProjectionResultOnSegment segProjection =
                    CadMath.ProjectPointToSegment(point, a, b);

                double measure =
                    accumulated + segProjection.T * segLen;

                if (segProjection.Distance < bestDistance)
                {
                    bestDistance = segProjection.Distance;
                    bestMeasure = measure;
                    bestCoordinate = segProjection.Coordinate;
                }

                accumulated += segLen;
            }

            return new ProjectionResult
            {
                Measure = bestMeasure,
                Distance = bestDistance,
                Coordinate = bestCoordinate
            };
        }

        /// <summary>
        /// 带 target 里程的采样点。
        /// 这是为了知道某个采样点位于 target 的哪一个长度位置。
        /// </summary>
        private class MeasuredSamplePoint
        {
            public Coordinate Point { get; set; }

            /// <summary>
            /// 当前点在 source LineString 上的累计长度位置。
            /// </summary>
            public double Measure { get; set; }
        }

        /// <summary>
        /// 一个被接受的投影采样点。
        /// 表示 target 上某个点可以被当前 base 覆盖。
        /// </summary>
        private class AcceptedProjectionSample
        {
            /// <summary>
            /// 采样点在 target 上的位置。
            /// </summary>
            public double TargetMeasure { get; set; }

            /// <summary>
            /// 采样点投影到 base 上的位置。
            /// </summary>
            public double BaseMeasure { get; set; }

            /// <summary>
            /// 点到 base 的距离。
            /// </summary>
            public double Distance { get; set; }
        }
    }
}
