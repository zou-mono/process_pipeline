using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using NetTopologySuite.Geometries;
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
    /// 规划数据与可编辑上版数据同源覆盖及 19 项业务属性比对核查命令入口（Stage 6 Commit 6.2）。
    /// </summary>
    public class CompareSegment : CadCommandBase
    {
        [CommandMethod("CompareSegment", CommandFlags.Modal | CommandFlags.NoUndoMarker)]
        public override void Execute()
        {
            if (Doc == null) return;

            var session = DocumentSessionManager.GetOrCreateSession(Doc.Database);
            if (!CheckPipelineCoordinator.EnsureDataSourcesReady(
                    Doc.Database,
                    session?.DatasetContext,
                    needPreviousData: true,
                    needSurveyData: false,
                    out _,
                    allowInteractiveDialog: true))
            {
                Doc.Editor?.WriteMessage("\n[线段覆盖核查] 未指定有效的上版数据 DWG，操作已取消。\n");
                return;
            }

            var geometryFactory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
            var service = new CompareSegmentService(Doc.Database, Doc.Editor, geometryFactory);
            session?.RegisterActiveRule(service);

            service.Run("线段覆盖核查", bOnlyUpdate: false);
        }
    }

    /// <summary>
    /// 规划管线与同源上版管线的配对及 19 项属性对比明细（Stage 6 Commit 6.2）。
    /// </summary>
    public class HomologousPipeComparisonItem
    {
        public ObjectId PlanPipeId { get; set; } = ObjectId.Null;
        public string PlanHandle { get; set; } = string.Empty;
        public string PlanLayerName { get; set; } = string.Empty;
        public string PlanCategoryId { get; set; } = string.Empty;
        public string PlanCategoryName { get; set; } = string.Empty;
        public double PlanLength { get; set; }
        public double? PlanDiameter { get; set; }
        public double? PlanSlope { get; set; }
        public double? PlanTextLength { get; set; }
        public string PlanRawLabel { get; set; } = string.Empty;

        public string PreviousHandle { get; set; } = string.Empty;
        public string PreviousLayerName { get; set; } = string.Empty;
        public double PreviousLength { get; set; }
        public double? PreviousDiameter { get; set; }
        public double? PreviousOriginalDiameter { get; set; }
        public double? PreviousSlope { get; set; }
        public double? PreviousAttrLength { get; set; }
        public string PreviousStatusText { get; set; } = string.Empty;

        public double OverlapLength { get; set; }
        public double PlanCoverageRatio { get; set; }
        public double PreviousCoverageRatio { get; set; }

        /// <summary>
        /// 从同源上版管线 ws 属性块提取并继承的完整 19 项业务属性字典。
        /// </summary>
        public Dictionary<string, string> Inherited19Attributes { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 规划图面属性与上版 ws 块属性存在差异的字段明细（如 WIDTH、PD）。
        /// </summary>
        public List<string> AttributeDifferences { get; set; } = new List<string>();

        public bool IsDiameterConsistent { get; set; } = true;
        public bool IsSlopeConsistent { get; set; } = true;
        public Point3d RepresentativeLocation { get; set; }
        public MatchItem HelperLine { get; set; }
    }

    /// <summary>
    /// 规划数据与可编辑上版数据同源覆盖及 19 项属性对比完整结构化报告（Stage 6 Commit 6.2）。
    /// </summary>
    public class CompareSegmentCheckReport
    {
        public int TotalPlanPolylines { get; set; }
        public int TotalPlanSegments { get; set; }
        public int TotalPreviousPolylines { get; set; }
        public int TotalPreviousSegments { get; set; }
        public int PreviousPolylinesWithAll19Attrs { get; set; }

        /// <summary>
        /// CompareCoverage(Plan, Previous)：
        /// CoveredSegments = 规划与上版同源共线覆盖段；
        /// MissingSegments = 规划相对上版的新增/改线折线段。
        /// </summary>
        public CompareResult PlanToPreviousCoverage { get; set; }

        /// <summary>
        /// CompareCoverage(Previous, Plan)：
        /// CoveredSegments = 上版被规划保留的折线段；
        /// MissingSegments = 上版相对规划缺失/删减的折线段。
        /// </summary>
        public CompareResult PreviousToPlanCoverage { get; set; }

        /// <summary>
        /// 同源管线配对与 19 项属性对比列表。
        /// </summary>
        public List<HomologousPipeComparisonItem> HomologousPairs { get; set; } =
            new List<HomologousPipeComparisonItem>();

        /// <summary>
        /// 同源管线中属性完全一致的记录。
        /// </summary>
        public List<HomologousPipeComparisonItem> ConsistentHomologousPairs { get; set; } =
            new List<HomologousPipeComparisonItem>();

        /// <summary>
        /// 同源管线中规划属性（WIDTH / PD）与上版 ws 块属性不一致的记录。
        /// </summary>
        public List<HomologousPipeComparisonItem> AttributeMismatchPairs { get; set; } =
            new List<HomologousPipeComparisonItem>();

        /// <summary>
        /// 规划新增/调整折线段问题项列表。
        /// </summary>
        public List<CheckIssueItem> PlanUncoveredSegmentIssues { get; set; } =
            new List<CheckIssueItem>();

        /// <summary>
        /// 上版缺失/删减折线段问题项列表（支持 TargetObjectId == ObjectId.Null 跨图坐标定位）。
        /// </summary>
        public List<CheckIssueItem> PreviousMissingSegmentIssues { get; set; } =
            new List<CheckIssueItem>();

        /// <summary>
        /// 同源管线属性（管径 WIDTH / 坡度 PD）不一致问题项列表。
        /// </summary>
        public List<CheckIssueItem> AttributeMismatchIssues { get; set; } =
            new List<CheckIssueItem>();

        /// <summary>
        /// 汇总全部核查问题列表（按唯一 IssueId "CompareSegment:{Handle}:{SubIndex}" 索引）。
        /// </summary>
        public List<CheckIssueItem> AllIssueItems { get; set; } =
            new List<CheckIssueItem>();

        /// <summary>
        /// 按 ObjectId 索引的主问题字典（兼容 ICheckRule.CheckAll 返回签名）。
        /// </summary>
        public Dictionary<ObjectId, CheckIssueItem> IssueMap { get; set; } =
            new Dictionary<ObjectId, CheckIssueItem>();
    }

    /// <summary>
    /// 规划数据与可编辑上版数据同源折线段覆盖及 19 项属性核查服务（Stage 6 Commit 6.2）。
    /// 
    /// 核心职责：
    /// 1. 同源覆盖比较（CompareCoverage）：
    ///    - 正向 Compare(Plan, Previous)：识别规划与上版的同源共线管段，以及规划新增/改线管段；
    ///    - 反向 Compare(Previous, Plan)：识别上版数据在新版规划中缺失/被删减的管段（支持跨图无 ObjectId 记录按坐标缩放定位）；
    /// 2. 19 项业务属性完整性与同源继承对比：
    ///    - 提取上版数据 ws 属性块的全部 19 项字段（核心 16 项 + 扩展 3 项 QSDM/MDDM/SHAPE_TYPE），挂载至同源规划管线；
    ///    - 重点对比同源管线的管径（WIDTH / YWIDTH）、坡度（PD）、规划状态（GHZT）等核心属性一致性；
    /// 3. 双向编辑增量感知（CheckIncremental & GetAffectedTargetIds）：
    ///    - 无论用户在核查过程中修改了【规划数据】（管线几何、文字标注 DBText/MText），
    ///      还是修改了【上版数据】（0污水-* 管线几何、0污水-属性 ws 块位置、块内 AttributeReference 字段值、Group 绑定），
    ///      均自动使 PipeDatasetContext 的上版动态缓存局部失效重建，并实时刷新受影响管段的核查状态（自动归档为 Resolved）。
    /// </summary>
    public class CompareSegmentService : CadBase<Dictionary<ObjectId, CheckIssueItem>>, ICheckRule
    {
        public const string DefaultRuleId = "CompareSegment";
        public const string DefaultRuleName = "线段覆盖核查";

        public const double DefaultDiameterToleranceMm = 1.0;
        public const double DefaultSlopeTolerance = 0.05;

        /// <summary>
        /// 上版数据 ws 属性块全部 19 项标准属性字段（16 项核心字段 + 3 项扩展字段）。
        /// </summary>
        public static readonly IReadOnlyList<string> Standard19AttributeTags = new[]
        {
            "QSBG", "MDBG", "QSMS", "MDMS", "PD", "LEN", "TYPE", "GHZT",
            "WIDTH", "HEITH", "YWIDTH", "YHEITH", "LX", "SJLY", "BZRQ", "BZH",
            "QSDM", "MDDM", "SHAPE_TYPE"
        };

        private readonly GeometryFactory _geometryFactory;
        private readonly AcadDb.Database _db;
        private readonly Editor _ed;
        private readonly DataSourceService _dataSourceService = new DataSourceService();
        private readonly PipeSpatialMatcher _spatialMatcher = new PipeSpatialMatcher();

        /// <summary>
        /// 上版管线 Handle -> 同源规划管线 ObjectId 集合映射（供上版数据被编辑时反向追踪受影响的规划管线）。
        /// </summary>
        private readonly Dictionary<string, HashSet<ObjectId>> _prevHandleToPlanPipeIds =
            new Dictionary<string, HashSet<ObjectId>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 上版 ws 属性块 Handle -> 配对的上版管线 Handle 映射（供修改 ws 块内属性时追踪到上版管线进而定位同源规划管线）。
        /// </summary>
        private readonly Dictionary<string, string> _prevBlockHandleToPrevPipeHandle =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 规划文字标注 ObjectId -> 规划管线 ObjectId 映射。
        /// </summary>
        private readonly Dictionary<ObjectId, HashSet<ObjectId>> _planTextToPlanPipeIds =
            new Dictionary<ObjectId, HashSet<ObjectId>>();

        public string RuleId => DefaultRuleId;
        public string RuleName => DefaultRuleName;

        /// <summary>
        /// 可选的外部指定上版数据 Database（用于无 UI 测试或显式绑定已打开的 MDI 上版文档）。
        /// </summary>
        public AcadDb.Database OverridePreviousDatabase { get; set; }

        /// <summary>
        /// 同源线段共线与覆盖容差（米，默认读取 Config.json 中的 SegmentCoverageTolerance = 0.1m）。
        /// </summary>
        public double CoverageTolerance { get; set; } = CadConfig.Algorithms.PipeCompare.SegmentCoverageTolerance;

        /// <summary>
        /// 圆弧段离散最大弦长（米，默认 1.0m）。
        /// </summary>
        public double ArcMaxSegmentLength { get; set; } = CadConfig.Algorithms.PipeCompare.ArcMaxSegmentLength;

        /// <summary>
        /// 最近一次核查生成的完整结构化报告。
        /// </summary>
        public CompareSegmentCheckReport LastReport { get; private set; }

        public CompareSegmentService(
            AcadDb.Database db,
            Editor ed = null,
            GeometryFactory geometryFactory = null) : base(db, ed)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _ed = ed;
            _geometryFactory = geometryFactory ?? NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
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
        /// 执行规划数据与可编辑上版数据全量同源覆盖及 19 项属性对比核查（ICheckRule.CheckAll）。
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

            CompareSegmentCheckReport report = InspectPlanVsPreviousData(
                datasetContext,
                OverridePreviousDatabase,
                forceReloadPrevious: true,
                existingIssuesById: existingByIssueId,
                progress: context);

            LastReport = report;

            if (session != null && report != null)
            {
                session.MergeRuleIssueItems(RuleId, report.AllIssueItems, isIncremental: false);
            }

            return report?.IssueMap ?? new Dictionary<ObjectId, CheckIssueItem>();
        }

        /// <summary>
        /// 执行双向编辑局部增量核查（ICheckRule.CheckIncremental）：
        /// 无论用户在核查过程中修改了【规划数据】（管线几何、文字标注 DBText/MText），
        /// 还是编辑了【上版数据】（管线几何、ws 属性块位置、块内 AttributeReference 字段值或 Group 绑定），
        /// 均自动刷新 PipeDatasetContext 动态缓存并重算受影响管段的覆盖与属性核查状态，将已消除的问题归档为 Resolved。
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
                    string iid = DocumentCheckSession.EnsureIssueId(kvp.Value, RuleId, 0);
                    if (!existingByIssueId.ContainsKey(iid))
                    {
                        existingByIssueId[iid] = kvp.Value;
                    }
                }
            }

            // 当上版数据缓存已被标记失效（或外部文件修改时间戳变化）时，自动重新加载最新上版数据
            bool forceReloadPrev = !datasetContext.IsPreviousDataCacheValid;

            CompareSegmentCheckReport latestReport = InspectPlanVsPreviousData(
                datasetContext,
                OverridePreviousDatabase,
                forceReloadPrevious: forceReloadPrev,
                existingIssuesById: existingByIssueId,
                progress: context);
            LastReport = latestReport;

            var latestByIssueId = new Dictionary<string, CheckIssueItem>(StringComparer.OrdinalIgnoreCase);
            foreach (CheckIssueItem item in latestReport.AllIssueItems)
            {
                if (item == null) continue;
                string iid = DocumentCheckSession.EnsureIssueId(item, RuleId, 0);
                latestByIssueId[iid] = item;
            }

            var updatedAllItems = new List<CheckIssueItem>();

            // 1. 对旧问题中已在最新核查中消除（如修改规划文字或修改上版 ws 块属性后管径/坡度已一致，或缺失段已补齐）的记录，自动归档为 Resolved
            foreach (var kvp in existingByIssueId)
            {
                string oldIssueId = kvp.Key;
                CheckIssueItem oldIssue = kvp.Value;
                if (oldIssue == null) continue;

                ObjectId oid = oldIssue.TargetObjectId;
                if (!oid.IsNull && (!oid.IsValid || oid.IsErased))
                {
                    oldIssue.Status = IssueReviewStatus.Resolved;
                    oldIssue.Remark = "图元已删除（已归档）";
                    updatedAllItems.Add(oldIssue);
                    mergedIssues[oid] = oldIssue;
                    continue;
                }

                if (!latestByIssueId.ContainsKey(oldIssueId))
                {
                    oldIssue.Status = IssueReviewStatus.Resolved;
                    HomologousPipeComparisonItem matchedHomologous = latestReport.HomologousPairs.FirstOrDefault(h =>
                        string.Equals(h.PlanHandle, oldIssue.TargetHandle, StringComparison.OrdinalIgnoreCase));

                    if (matchedHomologous != null && matchedHomologous.IsDiameterConsistent && matchedHomologous.IsSlopeConsistent)
                    {
                        oldIssue.Diameter = matchedHomologous.PlanDiameter ?? matchedHomologous.PreviousDiameter ?? oldIssue.Diameter;
                        oldIssue.Description =
                            $"同源管线 [{matchedHomologous.PlanHandle}] <-> 上版 [{matchedHomologous.PreviousHandle}] 属性已核对一致 (DN{oldIssue.Diameter:0})";
                        oldIssue.Remark = $"{oldIssue.Description}（已修复）";
                    }
                    else
                    {
                        oldIssue.Remark = $"{oldIssue.Description}（已修复）";
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
        /// 双向变更影响范围追踪（ICheckRule.GetAffectedTargetIds）：
        /// 同时监听【规划数据】（3-污水管-* 管线、2-* / 3-标注-* 文字标注）与
        /// 【上版数据】（0污水-* 管线、0污水-属性 ws 块、块内 AttributeReference 字段值、CAD Group）的增删改。
        /// </summary>
        public IEnumerable<ObjectId> GetAffectedTargetIds(DBObject changedObj)
        {
            var affected = new HashSet<ObjectId>();
            if (changedObj == null)
                return affected;

            var session = DocumentSessionManager.GetOrCreateSession(_db);
            var datasetContext = session?.DatasetContext;

            try
            {
                // 情况 A：CAD Group 发生变更（如在上版数据中绑定/解绑了管线与 ws 属性块）
                if (changedObj is Group grp)
                {
                    datasetContext?.InvalidatePreviousData();
                    if (!grp.IsErased)
                    {
                        ObjectId[] memberIds = grp.GetAllEntityIds();
                        if (memberIds != null)
                        {
                            foreach (ObjectId mid in memberIds)
                            {
                                if (mid.IsNull || !mid.IsValid) continue;
                                affected.Add(mid);
                                string h = mid.Handle.ToString();
                                AddAffectedPlanPipesByPreviousHandle(h, affected);
                            }
                        }
                    }
                    return affected;
                }

                // 情况 B：上版 ws 属性块内部的 AttributeReference（如修改了 WIDTH / PD / GHZT 字段值）发生修改
                Entity targetEnt = changedObj as Entity;
                if (changedObj is AttributeReference attrRef && !attrRef.OwnerId.IsNull && attrRef.OwnerId.IsValid)
                {
                    datasetContext?.InvalidatePreviousData();
                    Autodesk.AutoCAD.DatabaseServices.Database ownerDb = attrRef.Database ?? _db;
                    using (OpenCloseTransaction tr = ownerDb.TransactionManager.StartOpenCloseTransaction())
                    {
                        targetEnt = tr.GetObject(attrRef.OwnerId, OpenMode.ForRead, false) as BlockReference;
                    }
                }

                if (targetEnt == null)
                    return affected;

                string layer = (targetEnt.Layer ?? string.Empty).Trim();
                string handle = !targetEnt.ObjectId.IsNull && targetEnt.ObjectId.IsValid
                    ? targetEnt.ObjectId.Handle.ToString()
                    : (targetEnt.Handle.Value != 0 ? targetEnt.Handle.ToString() : string.Empty);

                var planPipeLayers = CadConfig.PlanningData.GetAllPipeLayers();
                var planTextLayers = CadConfig.PlanningData.GetAllTextLayers();
                var prevPipeLayers = new HashSet<string>(
                    CadConfig.PreviousData.PipeLayers ?? new List<string>(),
                    StringComparer.OrdinalIgnoreCase);
                var prevAttrLayers = new HashSet<string>(
                    CadConfig.PreviousData.AttributeLayers ?? new List<string>(),
                    StringComparer.OrdinalIgnoreCase);

                // 分支 1：规划数据管线发生修改
                if ((targetEnt is Polyline || targetEnt is Autodesk.AutoCAD.DatabaseServices.Line) &&
                    planPipeLayers.Contains(layer))
                {
                    affected.Add(targetEnt.ObjectId);
                }
                // 分支 2：规划数据管径/坡度文字标注发生修改
                else if ((targetEnt is DBText || targetEnt is MText) && planTextLayers.Contains(layer))
                {
                    if (_planTextToPlanPipeIds.TryGetValue(targetEnt.ObjectId, out HashSet<ObjectId> linkedPlanPipes) &&
                        linkedPlanPipes != null)
                    {
                        foreach (ObjectId pid in linkedPlanPipes)
                        {
                            if (!pid.IsNull)
                                affected.Add(pid);
                        }
                    }
                }
                // 分支 3：上版数据管线发生修改（0污水-*）
                else if ((targetEnt is Polyline || targetEnt is Autodesk.AutoCAD.DatabaseServices.Line) &&
                         prevPipeLayers.Contains(layer))
                {
                    datasetContext?.InvalidatePreviousData();
                    if (!targetEnt.ObjectId.IsNull)
                        affected.Add(targetEnt.ObjectId);

                    AddAffectedPlanPipesByPreviousHandle(handle, affected);
                    AddAffectedPlanPipesByEnvelope(targetEnt, datasetContext, affected);
                }
                // 分支 4：上版数据 ws 属性块发生位移或块内属性变更（0污水-属性）
                else if (targetEnt is BlockReference && prevAttrLayers.Contains(layer))
                {
                    datasetContext?.InvalidatePreviousData();
                    if (!targetEnt.ObjectId.IsNull)
                        affected.Add(targetEnt.ObjectId);

                    if (!string.IsNullOrWhiteSpace(handle) &&
                        _prevBlockHandleToPrevPipeHandle.TryGetValue(handle, out string prevPipeHandle))
                    {
                        AddAffectedPlanPipesByPreviousHandle(prevPipeHandle, affected);
                    }
                    AddAffectedPlanPipesByEnvelope(targetEnt, datasetContext, affected);
                }
            }
            catch
            {
                // 忽略瞬态事务读取异常
            }

            return affected;
        }

        private void AddAffectedPlanPipesByPreviousHandle(string prevPipeHandle, HashSet<ObjectId> affected)
        {
            if (string.IsNullOrWhiteSpace(prevPipeHandle))
                return;

            if (_prevHandleToPlanPipeIds.TryGetValue(prevPipeHandle, out HashSet<ObjectId> planIds) && planIds != null)
            {
                foreach (ObjectId pid in planIds)
                {
                    if (!pid.IsNull)
                        affected.Add(pid);
                }
            }
        }

        private void AddAffectedPlanPipesByEnvelope(
            Entity prevEnt,
            PipeDatasetContext datasetContext,
            HashSet<ObjectId> affected)
        {
            if (prevEnt == null || datasetContext == null)
                return;

            try
            {
                Extents3d ext = prevEnt.GeometricExtents;
                double expand = Math.Max(CoverageTolerance * 5.0, 25.0);
                var env = new Envelope(
                    ext.MinPoint.X - expand,
                    ext.MaxPoint.X + expand,
                    ext.MinPoint.Y - expand,
                    ext.MaxPoint.Y + expand);

                foreach (var planInfo in datasetContext.PlanPipesById.Values)
                {
                    if (planInfo?.Polyline?.Geometry == null || planInfo.PipeId.IsNull)
                        continue;

                    if (planInfo.Polyline.Geometry.EnvelopeInternal.Intersects(env))
                    {
                        affected.Add(planInfo.PipeId);
                    }
                }
            }
            catch
            {
                // 忽略无包围盒实体异常
            }
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

            if (LastReport == null || LastReport.TotalPlanPolylines <= 0)
            {
                if (palCheckPipe.Instance.IsVisible)
                {
                    palCheckPipe.Instance.UpdateIssueItems(Array.Empty<CheckIssueItem>(), isIncremental: false);
                }
                return;
            }

            Ed?.WriteMessage(
                $"\n[{RuleName}] 核查完成：规划管线 {LastReport.TotalPlanPolylines} 条（{LastReport.TotalPlanSegments} 折线段） vs " +
                $"上版管线 {LastReport.TotalPreviousPolylines} 条（{LastReport.TotalPreviousSegments} 折线段）；" +
                $"同源管线配对 {LastReport.HomologousPairs.Count} 组（属性一致={LastReport.ConsistentHomologousPairs.Count}，属性不符={LastReport.AttributeMismatchPairs.Count}），" +
                $"规划新增折线段 {LastReport.PlanUncoveredSegmentIssues.Count} 处，上版缺失折线段 {LastReport.PreviousMissingSegmentIssues.Count} 处。\n");

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
        /// 核心编排方法：执行规划数据与可编辑上版数据的双向同源折线段覆盖分析及 19 项业务属性继承/对比。
        /// </summary>
        public CompareSegmentCheckReport InspectPlanVsPreviousData(
            PipeDatasetContext datasetContext = null,
            AcadDb.Database explicitPreviousDb = null,
            bool forceReloadPrevious = false,
            Dictionary<string, CheckIssueItem> existingIssuesById = null,
            ProgressContext progress = null)
        {
            var ctx = datasetContext ?? DocumentSessionManager.GetOrCreateSession(_db)?.DatasetContext ?? new PipeDatasetContext();
            var report = new CompareSegmentCheckReport();

            // 1. 组装规划管线（颜色优先分类 + 规划文字标注 WIDTH/PD/LEN 提取）
            var planCheckSvc = new PlanDataCheckService(_db, _ed) { SuppressBlockingDialog = true };
            planCheckSvc.InspectPlanningData(CadConfig.PlanningData, ctx, null, progress);

            // Stage 6 Commit 6.6：若当前主图没有任何有效规划管线，严禁拿 0 条规划管与上版数据比对（避免误产生 hundreds 条“上版缺失”假阳性记录）
            if (ctx.PlanPipesById == null || ctx.PlanPipesById.Count == 0)
            {
                return report;
            }

            PipeSpatialMatchSummary planTextSummary = _spatialMatcher.MatchAndEnrichPlanTextLabels(
                _db,
                ctx,
                CadConfig.PlanningData);
            RebuildPlanTextMap(planTextSummary);

            // 2. 加载或复用可编辑上版数据动态缓存（含 Group + XData + 空间邻近三级提取的 19 项 ws 属性）
            EnsurePreviousDatasetLoaded(ctx, explicitPreviousDb, forceReloadPrevious);

            var planPipes = ctx.PlanPipesById.Values
                .Where(p => p != null && p.Polyline != null && GeometryHelper.IsValidPolylineGeometry(p.Polyline))
                .OrderBy(p => p.Handle, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var prevPolylines = ctx.PreviousPolylines ?? new List<CadPolyline>();

            report.TotalPlanPolylines = planPipes.Count;
            report.TotalPreviousPolylines = prevPolylines.Count;
            report.PreviousPolylinesWithAll19Attrs = prevPolylines.Count(p => HasAll19Attributes(p));

            // 收集全部规划折线段与上版折线段
            var planSegments = new List<CadSegment>();
            foreach (var p in planPipes)
            {
                if (p.Polyline.Segments != null)
                    planSegments.AddRange(p.Polyline.Segments);
            }

            var prevSegments = new List<CadSegment>();
            foreach (var p in prevPolylines)
            {
                if (p?.Segments != null)
                    prevSegments.AddRange(p.Segments);
            }

            report.TotalPlanSegments = planSegments.Count;
            report.TotalPreviousSegments = prevSegments.Count;

            double tol = CoverageTolerance > 0 ? CoverageTolerance : 0.1;

            // 3. 双向执行同源覆盖比较 CompareCoverage
            // 3.1 正向 Compare(Plan, Previous)：找出同源覆盖段 CoveredSegments 与规划新增段 MissingSegments
            report.PlanToPreviousCoverage = CompareCoverageWithIndex(
                planSegments,
                ctx.PreviousSegmentIndex,
                prevSegments.Count,
                tol);

            // 3.2 反向 Compare(Previous, Plan)：找出上版被覆盖段与上版缺失/被删减段 MissingSegments
            var planSegIndex = BuildSpatialIndex(planSegments);
            report.PreviousToPlanCoverage = CompareCoverageWithIndex(
                prevSegments,
                planSegIndex,
                planSegments.Count,
                tol);

            // 4. 识别同源管线配对（Plan Pipe <-> Previous Pipe）并执行 19 项属性继承与核心属性对比
            _prevHandleToPlanPipeIds.Clear();

            foreach (EnrichedPlanPipeInfo planInfo in planPipes)
            {
                if (progress != null && progress.Token.IsCancellationRequested)
                    break;

                CadPolyline planPoly = planInfo.Polyline;
                if (planPoly?.Segments == null || planPoly.Segments.Count == 0)
                    continue;

                // 统计该规划管线与各候选上版管线的共线覆盖总长度
                var overlapByPrevHandle = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

                foreach (CadSegment planSeg in planPoly.Segments)
                {
                    if (planSeg == null || planSeg.Length <= tol || planSeg.Geometry == null)
                        continue;

                    var queryEnv = new Envelope(planSeg.Geometry.EnvelopeInternal);
                    queryEnv.ExpandBy(tol);
                    IList<CadSegment> candSegs = ctx.PreviousSegmentIndex.Query(queryEnv);
                    if (candSegs == null || candSegs.Count == 0)
                        continue;

                    var intervalsByPrevPipe = new Dictionary<string, List<SegmentInterval>>(StringComparer.OrdinalIgnoreCase);

                    foreach (CadSegment prevSeg in candSegs)
                    {
                        if (prevSeg == null || prevSeg.Length <= tol)
                            continue;
                        if (!CadMath.IsCollinear(planSeg, prevSeg, tol))
                            continue;

                        SegmentInterval interval = CadMath.ProjectSegmentToBase(planSeg, prevSeg);
                        if (!CadMath.HasOverlapWithUnitInterval(interval, tol))
                            continue;

                        SegmentInterval clipped = CadMath.ClipToUnitInterval(interval);
                        if (Math.Abs(clipped.End - clipped.Start) <= 1e-12)
                            continue;

                        string prevHandle = prevSeg.SourceHandles != null && prevSeg.SourceHandles.Count > 0
                            ? prevSeg.SourceHandles[0]
                            : string.Empty;
                        if (string.IsNullOrWhiteSpace(prevHandle))
                            continue;

                        if (!intervalsByPrevPipe.TryGetValue(prevHandle, out var list))
                        {
                            list = new List<SegmentInterval>();
                            intervalsByPrevPipe[prevHandle] = list;
                        }
                        list.Add(clipped);
                    }

                    foreach (var kvp in intervalsByPrevPipe)
                    {
                        var merged = IntervalUtils.MergeIntervals(kvp.Value, tol);
                        double coveredRatio = merged.Sum(iv => Math.Max(0.0, iv.End - iv.Start));
                        double coveredLen = coveredRatio * planSeg.Length;
                        if (coveredLen > tol)
                        {
                            overlapByPrevHandle[kvp.Key] =
                                (overlapByPrevHandle.TryGetValue(kvp.Key, out double acc) ? acc : 0.0) + coveredLen;
                        }
                    }
                }

                if (overlapByPrevHandle.Count == 0)
                    continue;

                // 按重合长度从大到小遍历同源上版管线
                int subIdx = 0;
                foreach (var matchKvp in overlapByPrevHandle.OrderByDescending(x => x.Value))
                {
                    string prevHandle = matchKvp.Key;
                    double overlapLen = matchKvp.Value;
                    if (!ctx.PreviousPolylinesByHandle.TryGetValue(prevHandle, out CadPolyline prevPoly) || prevPoly == null)
                        continue;

                    double planCovRatio = planPoly.Length > 1e-6 ? Math.Min(1.0, overlapLen / planPoly.Length) : 0.0;
                    double prevCovRatio = prevPoly.Length > 1e-6 ? Math.Min(1.0, overlapLen / prevPoly.Length) : 0.0;

                    // 过滤仅在端点交叉或重叠极短的非同源管线
                    if (overlapLen < Math.Max(tol * 5.0, 1.0) && planCovRatio < 0.2 && prevCovRatio < 0.2)
                        continue;

                    if (!planInfo.PipeId.IsNull)
                    {
                        if (!_prevHandleToPlanPipeIds.TryGetValue(prevHandle, out HashSet<ObjectId> linkedSet))
                        {
                            linkedSet = new HashSet<ObjectId>();
                            _prevHandleToPlanPipeIds[prevHandle] = linkedSet;
                        }
                        linkedSet.Add(planInfo.PipeId);
                    }

                    // 提取上版管线挂载的 19 项属性并同步继承至规划管线上下文
                    var inherited19 = Extract19AttributesDictionary(prevPoly);
                    InheritPreviousAttributesToPlanPipe(planInfo, inherited19, prevHandle);

                    double? planWidth = planInfo.TextDiameter;
                    double? planWidthMax = planPoly.GetAttributeDouble("WIDTH_MAX");
                    double? planSlope = planInfo.TextSlope;
                    double? planTextLen = planInfo.TextLength;

                    double? prevWidth = prevPoly.GetAttributeDouble("WIDTH");
                    double? prevYWidth = prevPoly.GetAttributeDouble("YWIDTH");
                    double? prevSlope = prevPoly.GetAttributeDouble("PD");
                    double? prevLen = prevPoly.GetAttributeDouble("LEN");
                    string prevGhzt = prevPoly.GetAttributeString("GHZT") ?? string.Empty;

                    bool isDiamConsistent = true;
                    bool isSlopeConsistent = true;
                    var diffs = new List<string>();

                    if (planWidth.HasValue && prevWidth.HasValue)
                    {
                        bool exactMatch = Math.Abs(planWidth.Value - prevWidth.Value) <= DefaultDiameterToleranceMm;
                        bool rangeMatch = planWidthMax.HasValue &&
                                          prevWidth.Value >= Math.Min(planWidth.Value, planWidthMax.Value) - DefaultDiameterToleranceMm &&
                                          prevWidth.Value <= Math.Max(planWidth.Value, planWidthMax.Value) + DefaultDiameterToleranceMm;
                        if (!exactMatch && !rangeMatch)
                        {
                            isDiamConsistent = false;
                            diffs.Add($"管径WIDTH(规划DN{planWidth.Value:0} vs 上版DN{prevWidth.Value:0})");
                        }
                    }

                    if (planSlope.HasValue && prevSlope.HasValue)
                    {
                        if (Math.Abs(planSlope.Value - prevSlope.Value) > DefaultSlopeTolerance)
                        {
                            isSlopeConsistent = false;
                            diffs.Add($"坡度PD(规划i={planSlope.Value:0.##} vs 上版PD={prevSlope.Value:0.##})");
                        }
                    }

                    Point3d repLoc = GetMidPointOfPolyline(planPoly.Geometry);
                    Point3d prevMidLoc = GetMidPointOfPolyline(prevPoly.Geometry);
                    var helperLine = new MatchItem(
                        ObjectId.Null,
                        prevMidLoc,
                        repLoc.DistanceTo(prevMidLoc),
                        repLoc,
                        0.0,
                        0.0,
                        true);

                    var compItem = new HomologousPipeComparisonItem
                    {
                        PlanPipeId = planInfo.PipeId,
                        PlanHandle = planInfo.Handle,
                        PlanLayerName = planInfo.LayerName,
                        PlanCategoryId = planInfo.ResolvedCategoryId,
                        PlanCategoryName = planInfo.ResolvedCategoryName,
                        PlanLength = planPoly.Length,
                        PlanDiameter = planWidth,
                        PlanSlope = planSlope,
                        PlanTextLength = planTextLen,
                        PlanRawLabel = planInfo.RawLabelText,
                        PreviousHandle = prevHandle,
                        PreviousLayerName = prevPoly.LayerName,
                        PreviousLength = prevPoly.Length,
                        PreviousDiameter = prevWidth,
                        PreviousOriginalDiameter = prevYWidth,
                        PreviousSlope = prevSlope,
                        PreviousAttrLength = prevLen,
                        PreviousStatusText = prevGhzt,
                        OverlapLength = overlapLen,
                        PlanCoverageRatio = planCovRatio,
                        PreviousCoverageRatio = prevCovRatio,
                        Inherited19Attributes = inherited19,
                        AttributeDifferences = diffs,
                        IsDiameterConsistent = isDiamConsistent,
                        IsSlopeConsistent = isSlopeConsistent,
                        RepresentativeLocation = repLoc,
                        HelperLine = helperLine
                    };

                    report.HomologousPairs.Add(compItem);

                    if (diffs.Count == 0)
                    {
                        report.ConsistentHomologousPairs.Add(compItem);
                    }
                    else
                    {
                        report.AttributeMismatchPairs.Add(compItem);

                        string diffSummary = string.Join("；", diffs);
                        string desc =
                            $"同源管线属性不一致：规划管 [{planInfo.Handle}]({planInfo.ResolvedCategoryName}, '{planInfo.RawLabelText}') " +
                            $"与同源上版管 [{prevHandle}]({prevPoly.LayerName}, 重合={overlapLen:F1}m) 存在属性差异：{diffSummary}";

                        string issueId = $"{RuleId}:{planInfo.Handle}:ATTR_{subIdx}";
                        CheckIssueItem attrIssue = CreateComparisonIssue(
                            issueId,
                            planInfo.PipeId,
                            planInfo.Handle,
                            planInfo.LayerName,
                            ProblemType.DirectionConflict,
                            ProblemLevel.Warning,
                            repLoc,
                            planWidth ?? prevWidth ?? 0.0,
                            desc,
                            helperLine,
                            compItem,
                            existingIssuesById);

                        report.AttributeMismatchIssues.Add(attrIssue);
                        report.AllIssueItems.Add(attrIssue);
                        if (!planInfo.PipeId.IsNull && !report.IssueMap.ContainsKey(planInfo.PipeId))
                        {
                            report.IssueMap[planInfo.PipeId] = attrIssue;
                        }
                    }

                    subIdx++;
                }
            }

            // 5. 将【规划相对上版未覆盖的新增/改线折线段】按所属规划管线聚合并生成问题子项
            var planUncoveredByHandle = report.PlanToPreviousCoverage.MissingSegments
                .Where(s => s != null && s.Length > Math.Max(tol * 2.0, 0.5))
                .GroupBy(s => s.SourceHandles != null && s.SourceHandles.Count > 0 ? s.SourceHandles[0] : "UNKNOWN", StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var grp in planUncoveredByHandle)
            {
                string planHandle = grp.Key;
                double uncoveredLen = grp.Sum(s => s.Length);
                ctx.PlanPipesByHandle.TryGetValue(planHandle, out EnrichedPlanPipeInfo planInfo);

                ObjectId planId = planInfo?.PipeId ?? ObjectId.Null;
                string layerName = planInfo?.LayerName ?? grp.First().LayerName ?? string.Empty;
                string catName = planInfo?.ResolvedCategoryName ?? "规划管";
                double totalLen = planInfo?.Polyline?.Length ?? uncoveredLen;
                double? width = planInfo?.TextDiameter;

                CadSegment firstSeg = grp.OrderByDescending(s => s.Length).First();
                Point3d midPt = new Point3d(
                    (firstSeg.Start.X + firstSeg.End.X) * 0.5,
                    (firstSeg.Start.Y + firstSeg.End.Y) * 0.5,
                    0.0);

                string issueId = $"{RuleId}:{planHandle}:NEW_SEG";
                string desc =
                    $"规划相对上版新增/改线管段：[{catName}] 管线 [{planHandle}] 存在 {grp.Count()} 段共 {uncoveredLen:F1}m (总长 {totalLen:F1}m) 未被上版数据覆盖";

                CheckIssueItem newSegIssue = CreateComparisonIssue(
                    issueId,
                    planId,
                    planHandle,
                    layerName,
                    ProblemType.NoAdjacentItems,
                    ProblemLevel.Warning,
                    midPt,
                    width ?? 0.0,
                    desc,
                    null,
                    grp.ToList(),
                    existingIssuesById);

                report.PlanUncoveredSegmentIssues.Add(newSegIssue);
                report.AllIssueItems.Add(newSegIssue);
                if (!planId.IsNull && !report.IssueMap.ContainsKey(planId))
                {
                    report.IssueMap[planId] = newSegIssue;
                }
            }

            // 6. 将【上版相对规划缺失/被删减的折线段】按所属上版管线聚合并生成问题子项（支持 TargetObjectId == ObjectId.Null 坐标缩放）
            var prevMissingByHandle = report.PreviousToPlanCoverage.MissingSegments
                .Where(s => s != null && s.Length > Math.Max(tol * 2.0, 0.5))
                .GroupBy(s => s.SourceHandles != null && s.SourceHandles.Count > 0 ? s.SourceHandles[0] : "UNKNOWN", StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var grp in prevMissingByHandle)
            {
                string prevHandle = grp.Key;
                double missingLen = grp.Sum(s => s.Length);
                ctx.PreviousPolylinesByHandle.TryGetValue(prevHandle, out CadPolyline prevPoly);

                string prevLayer = prevPoly?.LayerName ?? grp.First().LayerName ?? "0污水-上版";
                double prevTotalLen = prevPoly?.Length ?? missingLen;
                double? prevWidth = prevPoly?.GetAttributeDouble("WIDTH");
                string prevGhzt = prevPoly?.GetAttributeString("GHZT") ?? string.Empty;

                CadSegment firstSeg = grp.OrderByDescending(s => s.Length).First();
                Point3d midPt = new Point3d(
                    (firstSeg.Start.X + firstSeg.End.X) * 0.5,
                    (firstSeg.Start.Y + firstSeg.End.Y) * 0.5,
                    0.0);

                string issueId = $"{RuleId}:PREV_{prevHandle}:MISSING";
                string desc =
                    $"上版管段在新版规划中缺失：上版 [{prevLayer}] 管线 [{prevHandle}] (DN{prevWidth ?? 0:0}, {prevGhzt}) 有 {grp.Count()} 段共 {missingLen:F1}m (总长 {prevTotalLen:F1}m) 未被规划数据覆盖";

                CheckIssueItem missingIssue = CreateComparisonIssue(
                    issueId,
                    ObjectId.Null,
                    $"上版_{prevHandle}",
                    prevLayer,
                    ProblemType.NoAdjacentItems,
                    ProblemLevel.Warning,
                    midPt,
                    prevWidth ?? 0.0,
                    desc,
                    null,
                    grp.ToList(),
                    existingIssuesById);

                report.PreviousMissingSegmentIssues.Add(missingIssue);
                report.AllIssueItems.Add(missingIssue);
            }

            return report;
        }

        private void EnsurePreviousDatasetLoaded(
            PipeDatasetContext ctx,
            AcadDb.Database explicitPreviousDb,
            bool forceReload)
        {
            if (!forceReload && ctx.IsPreviousDataCacheValid && ctx.PreviousPolylines != null && ctx.PreviousPolylines.Count > 0)
            {
                return;
            }

            var prevPipeLayers = CadConfig.PreviousData.PipeLayers ?? new List<string>
            {
                "0污水-待补测现状管",
                "0污水-规划新建",
                "0污水-规划改建",
                "0污水-已核实需新增"
            };
            var prevAttrLayers = CadConfig.PreviousData.AttributeLayers ?? new List<string> { "0污水-属性" };

            AcadDb.Database prevDb = explicitPreviousDb;
            bool shouldDispose = false;

            try
            {
                if (prevDb == null)
                {
                    if (ctx.PreviousDataSource == null || !ctx.PreviousDataSource.IsValid)
                    {
                        _dataSourceService.TryAutoResolveDataSources(_db, ctx, needPreviousData: true, needSurveyData: false);
                    }

                    if (ctx.PreviousDataSource != null && ctx.PreviousDataSource.IsValid)
                    {
                        prevDb = _dataSourceService.OpenOrReadDatabase(ctx.PreviousDataSource, out shouldDispose);
                    }
                }

                if (prevDb == null)
                {
                    ctx.UpdatePreviousDataset(new List<CadPolyline>());
                    return;
                }

                // 构建上版 ws 属性块 Handle -> 上版管线 Handle 映射（支撑编辑 ws 块内属性时的增量追踪）
                _prevBlockHandleToPrevPipeHandle.Clear();
                PipeSpatialMatchSummary prevMatchSummary = _spatialMatcher.MatchDatabaseBlockAttributes(
                    prevDb,
                    prevPipeLayers,
                    prevAttrLayers);
                if (prevMatchSummary?.MatchByPipeId != null)
                {
                    foreach (var match in prevMatchSummary.MatchByPipeId.Values)
                    {
                        if (match?.Pipe != null &&
                            match.MatchedAttachment != null &&
                            !string.IsNullOrWhiteSpace(match.Pipe.Handle) &&
                            !string.IsNullOrWhiteSpace(match.MatchedAttachment.Handle))
                        {
                            _prevBlockHandleToPrevPipeHandle[match.MatchedAttachment.Handle] = match.Pipe.Handle;
                        }
                    }
                }

                List<CadPolyline> prevPolylines = GeometryHelper.CollectPolylinesFromLayers(
                    prevDb,
                    prevPipeLayers,
                    _geometryFactory,
                    "previous") ?? new List<CadPolyline>();

                ctx.UpdatePreviousDataset(prevPolylines);
            }
            finally
            {
                if (shouldDispose && prevDb != null)
                {
                    prevDb.Dispose();
                }
            }
        }

        private void RebuildPlanTextMap(PipeSpatialMatchSummary textSummary)
        {
            _planTextToPlanPipeIds.Clear();
            if (textSummary?.MatchByPipeId == null)
                return;

            foreach (var match in textSummary.MatchByPipeId.Values)
            {
                if (match?.Pipe == null || match.Pipe.PipeId.IsNull)
                    continue;

                ObjectId pipeId = match.Pipe.PipeId;
                if (match.MatchedAttachment != null && !match.MatchedAttachment.AttachmentId.IsNull)
                {
                    ObjectId attId = match.MatchedAttachment.AttachmentId;
                    if (!_planTextToPlanPipeIds.TryGetValue(attId, out HashSet<ObjectId> set))
                    {
                        set = new HashSet<ObjectId>();
                        _planTextToPlanPipeIds[attId] = set;
                    }
                    set.Add(pipeId);
                }
            }
        }

        private static bool HasAll19Attributes(CadPolyline poly)
        {
            if (poly?.Attributes == null)
                return false;

            foreach (string tag in Standard19AttributeTags)
            {
                if (!poly.Attributes.ContainsKey(tag))
                    return false;
            }
            return true;
        }

        private static Dictionary<string, string> Extract19AttributesDictionary(CadPolyline prevPoly)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (prevPoly?.Attributes == null)
                return dict;

            foreach (string tag in Standard19AttributeTags)
            {
                if (prevPoly.Attributes.TryGetValue(tag, out object val) && val != null)
                {
                    dict[tag] = val.ToString();
                }
            }
            return dict;
        }

        private static void InheritPreviousAttributesToPlanPipe(
            EnrichedPlanPipeInfo planInfo,
            Dictionary<string, string> inherited19,
            string prevHandle)
        {
            if (planInfo?.Polyline == null || inherited19 == null)
                return;

            if (planInfo.Polyline.Attributes == null)
            {
                planInfo.Polyline.Attributes = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            }

            planInfo.Polyline.Attributes["HOMOLOGOUS_PREV_HANDLE"] = prevHandle;
            foreach (var kvp in inherited19)
            {
                planInfo.Polyline.Attributes[$"PREV_{kvp.Key}"] = kvp.Value;
                if (!planInfo.Polyline.Attributes.ContainsKey(kvp.Key))
                {
                    planInfo.Polyline.Attributes[kvp.Key] = kvp.Value;
                }
            }
        }

        private CheckIssueItem CreateComparisonIssue(
            string issueId,
            ObjectId targetId,
            string targetHandle,
            string layerName,
            ProblemType problemType,
            ProblemLevel level,
            Point3d location,
            double diameter,
            string description,
            MatchItem helperLine,
            object payload,
            Dictionary<string, CheckIssueItem> existingIssuesById)
        {
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
            if (helperLine != null)
            {
                helperLines.Add(helperLine);
            }

            return new CheckIssueItem
            {
                IssueId = issueId,
                RuleId = RuleId,
                RuleName = RuleName,
                Type = problemType,
                Level = level,
                TargetObjectId = targetId,
                TargetHandle = targetHandle,
                LayerName = layerName,
                Location = location,
                Diameter = diameter,
                Description = description,
                Remark = remark,
                Status = status,
                PossibleMatches = helperLines,
                RulePayload = payload
            };
        }

        private static Point3d GetMidPointOfPolyline(LineString line)
        {
            if (line == null || line.IsEmpty || line.Coordinates == null || line.Coordinates.Length == 0)
                return Point3d.Origin;

            Coordinate[] coords = line.Coordinates;
            if (coords.Length == 1)
                return new Point3d(coords[0].X, coords[0].Y, 0.0);

            int midIdx = coords.Length / 2;
            Coordinate a = coords[Math.Max(0, midIdx - 1)];
            Coordinate b = coords[midIdx];
            return new Point3d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, 0.0);
        }

        /// <summary>
        /// 执行方向性比较。
        /// </summary>
        public CompareResult CompareCoverage(
            IList<CadSegment> baseSegments,
            IList<CadSegment> targetSegments,
            double tolerance)
        {
            var targetIndex = BuildSpatialIndex(targetSegments ?? Array.Empty<CadSegment>());
            return CompareCoverageWithIndex(
                baseSegments ?? Array.Empty<CadSegment>(),
                targetIndex,
                targetSegments?.Count ?? 0,
                tolerance);
        }

        /// <summary>
        /// 使用预构建的 STRtree 空间索引执行方向性覆盖比较（避免重复构建空间索引）。
        /// </summary>
        public CompareResult CompareCoverageWithIndex(
            IList<CadSegment> baseSegments,
            STRtree<CadSegment> targetIndex,
            int targetSegmentCount,
            double tolerance)
        {
            var result = new CompareResult
            {
                BaseSegmentCount = baseSegments?.Count ?? 0,
                TargetSegmentCount = targetSegmentCount
            };

            if (baseSegments == null || baseSegments.Count == 0)
                return result;

            foreach (var baseSeg in baseSegments)
            {
                if (baseSeg == null || baseSeg.Length <= tolerance || baseSeg.Geometry == null)
                    continue;

                // 查询包围盒附近的候选线段。
                var queryEnv = new Envelope(baseSeg.Geometry.EnvelopeInternal);
                queryEnv.ExpandBy(tolerance);

                IList<CadSegment> candidates = targetIndex != null
                    ? targetIndex.Query(queryEnv)
                    : Array.Empty<CadSegment>();

                var coveredIntervals = new List<SegmentInterval>();

                foreach (var targetSeg in candidates)
                {
                    if (targetSeg == null || targetSeg.Length <= tolerance)
                        continue;

                    // 第一步：判断是否近似共线。
                    if (!Geometry.CadMath.IsCollinear(baseSeg, targetSeg, tolerance))
                        continue;

                    // 第二步：把 targetSeg 投影到 baseSeg 参数轴上。
                    var interval = Geometry.CadMath.ProjectSegmentToBase(baseSeg, targetSeg);

                    // 第三步：判断投影区间是否和 [0,1] 有重叠。
                    if (!Geometry.CadMath.HasOverlapWithUnitInterval(interval, tolerance))
                        continue;

                    // 第四步：裁剪到 [0,1]。
                    var clipped = Geometry.CadMath.ClipToUnitInterval(interval);

                    // 避免极小区间。
                    if (System.Math.Abs(clipped.End - clipped.Start) <= 1e-12)
                        continue;

                    coveredIntervals.Add(clipped);
                }

                // 合并 target 对 base 的所有覆盖区间。
                var mergedCovered = IntervalUtils.MergeIntervals(coveredIntervals, tolerance);

                // 把已覆盖区间转成实际重叠线段。
                foreach (var coveredInterval in mergedCovered)
                {
                    var coveredSeg = Geometry.CadMath.CreateSubSegment(
                        baseSeg,
                        coveredInterval,
                        _geometryFactory);

                    if (coveredSeg.Length > tolerance)
                    {
                        result.CoveredSegments.Add(coveredSeg);
                    }
                }

                // 求 baseSeg 上未被覆盖的区间。
                var missingIntervals = IntervalUtils.GetMissingIntervalsInUnit(mergedCovered, tolerance);

                // 把缺失区间转成实际线段。
                foreach (var missingInterval in missingIntervals)
                {
                    var missingSeg = Geometry.CadMath.CreateSubSegment(
                        baseSeg,
                        missingInterval,
                        _geometryFactory);

                    if (missingSeg.Length > tolerance)
                    {
                        result.MissingSegments.Add(missingSeg);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 对目标线段集合建立 STRtree 空间索引。
        /// </summary>
        private STRtree<CadSegment> BuildSpatialIndex(IList<CadSegment> segments)
        {
            var index = new STRtree<CadSegment>();

            if (segments != null)
            {
                foreach (var seg in segments)
                {
                    if (seg?.Geometry == null)
                        continue;

                    index.Insert(seg.Geometry.EnvelopeInternal, seg);
                }
            }

            index.Build();

            return index;
        }
    }

    public static class CadSegmentExtractionService
    {
        public static List<CadSegment> ExtractSegmentsFromDatabaseModelSpace(
            AcadDb.Database db,
            string targetLayer,
            string datasetName,
            GeometryFactory geometryFactory,
            double arcMaxSegmentLength,
            Matrix3d transform,
            string sourceFilePath,
            bool isFromSideDatabase)
        {
            var result = new List<CadSegment>();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt =
                    (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

                BlockTableRecord modelSpace =
                    (BlockTableRecord)tr.GetObject(
                        bt[BlockTableRecord.ModelSpace],
                        OpenMode.ForRead);

                foreach (ObjectId id in modelSpace)
                {
                    if (!id.IsValid || id.IsErased)
                        continue;

                    Entity entity = tr.GetObject(id, OpenMode.ForRead) as Entity;

                    if (entity == null)
                        continue;

                    if (!(entity is Autodesk.AutoCAD.DatabaseServices.Line) && !(entity is Polyline))
                        continue;

                    if (!string.Equals(entity.Layer, targetLayer, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var segs = PolylineExtractor.ExtractSegmentsFromEntity(
                        entity,
                        datasetName,
                        geometryFactory,
                        arcMaxSegmentLength,
                        transform,
                        sourceFilePath,
                        isFromSideDatabase);

                    result.AddRange(segs);
                }

                tr.Commit();
            }

            return result;
        }

        public static List<CadSegment> ExtractSegmentsFromBlockReference(
            AcadDb.Database db,
            Transaction tr,
            BlockReference blockRef,
            string targetLayer,
            string datasetName,
            GeometryFactory geometryFactory,
            double arcMaxSegmentLength)
        {
            var result = new List<CadSegment>();

            Matrix3d blockTransform = blockRef.BlockTransform;

            BlockTableRecord blockDef =
                (BlockTableRecord)tr.GetObject(
                    blockRef.BlockTableRecord,
                    OpenMode.ForRead);

            foreach (ObjectId id in blockDef)
            {
                if (!id.IsValid || id.IsErased)
                    continue;

                Entity entity = tr.GetObject(id, OpenMode.ForRead) as Entity;

                if (entity == null)
                    continue;

                if (!(entity is Autodesk.AutoCAD.DatabaseServices.Line) && !(entity is Polyline))
                    continue;

                if (!string.Equals(entity.Layer, targetLayer, StringComparison.OrdinalIgnoreCase))
                    continue;

                var segs = PolylineExtractor.ExtractSegmentsFromEntity(
                    entity,
                    datasetName,
                    geometryFactory,
                    arcMaxSegmentLength,
                    blockTransform,
                    db.Filename,
                    false);

                result.AddRange(segs);
            }

            return result;
        }
    }
}
