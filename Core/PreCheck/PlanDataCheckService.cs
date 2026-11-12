using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using NetTopologySuite.Geometries;
using process_pipeline.Core.Config;
using process_pipeline.Core.DataSource;
using process_pipeline.Forms;
using process_pipeline.Geometry;
using process_pipeline.Models;
using process_pipeline.Utils;
using AcadDb = Autodesk.AutoCAD.DatabaseServices;

namespace process_pipeline.Core.PreCheck
{
    /// <summary>
    /// 规划数据自身合规核查与属性预组装服务（Stage 4 Commit 4.1 & 4.2）。
    /// 
    /// 核心职责：
    /// 1. 颜色优先 + 图层名双重一致性核查：
    ///    - 按“颜色优先级高于图层名”原则判定每根规划管线的业务类别（规划新建管 ACI=1 红 / 现状管 ACI=5 蓝 / 规划改扩建管 ACI=6 洋红）；
    ///    - 当图元有效颜色与所属图层标准颜色不一致（如 3-污水管-现状 中的 4 条绿色 ACI=3 管线：Handle 139, 213, 241, 242）时，生成待核查问题项；
    /// 2. 退化几何核查：
    ///    - 自动识别规划图层中长度为 0（或近似为 0）的退化闭合线/零长度线（如 Handle=184）；
    /// 3. 残留旧图层核查：
    ///    - 扫描规划图纸中残留的上版图层图元（如 0污水-已核实需新增 的多段线 10D, 10F, 1A3 与 0污水-属性 的 ws 属性块 4EA, 517, 52C）；
    /// 4. 富化属性写入上下文：
    ///    - 将所有有效规划管线的分类结果（CATEGORY_ID / GHZT）及提取出的 CadPolyline 同步写入 <see cref="PipeDatasetContext"/>；
    /// 5. 实现 <see cref="ICheckRule"/> 接口：
    ///    - 支持在主核查面板中展示，并在用户修改管线颜色、切换图层或删除残留/退化图元后毫秒级增量刷新状态。
    /// </summary>
    public class PlanDataCheckService : CadBase<Dictionary<ObjectId, CheckIssueItem>>, ICheckRule
    {
        public const string DefaultRuleId = "CheckPlan";
        public const string DefaultRuleName = "规划数据合规核查";

        /// <summary>
        /// 判定退化零长度管线的长度阈值（米）。
        /// </summary>
        public const double DefaultDegenerateLengthTolerance = 1e-3;

        /// <summary>
        /// 判定残留旧图层与完整合图模式的实体数量分界阈值（默认 50；小于等于该值视为残留脏数据报错）。
        /// </summary>
        public const int DefaultLegacyResidualMaxThreshold = 50;

        private readonly AcadDb.Database _db;
        private readonly Editor _ed;
        private readonly GeometryFactory _geometryFactory =
            NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();

        public string RuleId => DefaultRuleId;
        public string RuleName => DefaultRuleName;

        /// <summary>
        /// 规划数据图层与颜色分类配置（默认读取 Config.json 中的 CadConfig.PlanningData）。
        /// </summary>
        public PlanningDataSettings Settings { get; set; } = CadConfig.PlanningData;

        /// <summary>
        /// 退化几何长度判定容差（米，默认 0.001m）。
        /// </summary>
        public double DegenerateLengthTolerance { get; set; } = DefaultDegenerateLengthTolerance;

        /// <summary>
        /// 是否核查规划图纸中残留的上版数据图层（0污水-*）。
        /// </summary>
        public bool CheckLegacyResidualLayers { get; set; } = true;

        /// <summary>
        /// 在批量全量核查调度时，是否静默子规则自带的模态阻断弹窗（由顶层 CheckPipelineCoordinator 统一汇总弹窗，Stage 6 Commit 6.4）。
        /// </summary>
        public bool SuppressBlockingDialog { get; set; } = false;

        /// <summary>
        /// 最近一次执行规划自检生成的完整结构化报告。
        /// </summary>
        public PlanDataCheckReport LastReport { get; private set; }

        public PlanDataCheckService(AcadDb.Database db, Editor ed = null) : base(db, ed)
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
        /// 执行规划数据全量合规自检并同步富化上下文（ICheckRule.CheckAll）。
        /// </summary>
        public Dictionary<ObjectId, CheckIssueItem> CheckAll(ProgressContext context = null)
        {
            var session = DocumentSessionManager.GetOrCreateSession(_db);
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

            PlanDataCheckReport report = InspectPlanningData(
                Settings ?? CadConfig.PlanningData,
                session?.DatasetContext,
                existingSameRuleIssues,
                context);

            LastReport = report;

            if (report.TotalScannedPlanCurves == 0 && report.LegacyResidualItems.Count == 0 && _ed != null)
            {
                if (!SuppressBlockingDialog && !ConfigManager.IsHeadlessOrSuppressedUi)
                {
                    var planLayers = (Settings ?? CadConfig.PlanningData)?.GetAllPipeLayers() ?? new HashSet<string>();
                    CadDialogService.ShowMessage(
                        "[Title]当前文档未找到可核查的规划管线数据。\n\n" +
                        "[Hint]CHECKPLAN 命令针对当前激活文档核查规划管线颜色/图层一致性、退化几何与残留图层：\n" +
                        $"1.Entity类型必须是Line或者Polyline;\n" +
                        $"2.规划管线图层名称：{string.Join("、", planLayers)}.",
                        type: MessageBoxType.Warning);
                }
                return null;
            }

            session?.RegisterActiveRule(this);
            session?.MergeRuleIssues(RuleId, report.IssueMap, isIncremental: false);
            return report.IssueMap;
        }

        protected override void OnSuccess(Dictionary<ObjectId, CheckIssueItem> result, bool bOnlyUpdate = false)
        {
            var session = DocumentSessionManager.GetOrCreateSession(_db);

            if (bOnlyUpdate)
            {
                var merged = session != null
                    ? session.MergeRuleIssues(RuleId, result ?? new Dictionary<ObjectId, CheckIssueItem>(), isIncremental: true)
                    : (result ?? new Dictionary<ObjectId, CheckIssueItem>());
                if (session != null && session.IssuesById.Count > 0)
                {
                    palCheckPipe.Instance.UpdateIssueItems(session.GetAllIssueItems(), isIncremental: true);
                }
                else
                {
                    palCheckPipe.Instance.UpdateIncrementalData(merged);
                }
                return;
            }

            session?.RegisterActiveRule(this);
            if (result == null || result.Count == 0)
            {
                Ed?.WriteMessage($"\n[{RuleName}] 检查通过：当前文档共 {LastReport?.NormalCompliantPipeCount ?? 0} 条规划管线全部符合颜色与图层标准规范，无退化或残留旧图层异常。\n");
                if (session != null)
                {
                    session.MergeRuleIssues(RuleId, new Dictionary<ObjectId, CheckIssueItem>(), isIncremental: false);
                    if (palCheckPipe.Instance.IsVisible)
                    {
                        palCheckPipe.Instance.UpdateIssueItems(session.GetAllIssueItems(), isIncremental: false, activeRuleId: RuleId);
                    }
                }
                return;
            }

            int colorMismatchCount = LastReport?.ColorLayerMismatchItems.Count ?? 0;
            int degenerateCount = LastReport?.DegenerateGeometryItems.Count ?? 0;
            int legacyCount = LastReport?.LegacyResidualItems.Count ?? 0;
            Ed?.WriteMessage(
                $"\n[{RuleName}] 核查完成：共检出 {result.Count} 项待核查问题（颜色与图层冲突：{colorMismatchCount} 项，退化零长度几何：{degenerateCount} 项，残留旧图层图元：{legacyCount} 项）。\n");

            var mergedAll = session != null
                ? session.MergeRuleIssues(RuleId, result, isIncremental: false)
                : result;
            if (session != null && session.IssuesById.Count > 0)
            {
                palCheckPipe.Instance.ShowIssueItems(session.GetAllIssueItems(), RuleId);
            }
            else
            {
                palCheckPipe.Instance.Show(mergedAll);
            }
        }

        /// <summary>
        /// 执行规划数据自身合规核查，返回完整的结构化统计与异常明细报告（Stage 4 Commit 4.1）。
        /// </summary>
        public PlanDataCheckReport InspectPlanningData(
            PlanningDataSettings settings = null,
            PipeDatasetContext datasetContext = null,
            Dictionary<ObjectId, CheckIssueItem> existingSameRuleIssues = null,
            ProgressContext progress = null)
        {
            var cfg = settings ?? Settings ?? CadConfig.PlanningData ?? new PlanningDataSettings();
            var report = new PlanDataCheckReport();

            // 1. 构建图层 -> 规划类别规则映射，以及 ACI / RGB -> 规划类别规则映射
            var layerToRule = new Dictionary<string, PlanPipeCategoryRule>(StringComparer.OrdinalIgnoreCase);
            var aciToRule = new Dictionary<short, PlanPipeCategoryRule>();
            var rgbToRule = new Dictionary<string, PlanPipeCategoryRule>(StringComparer.OrdinalIgnoreCase);

            if (cfg.Categories != null)
            {
                foreach (PlanPipeCategoryRule cat in cfg.Categories)
                {
                    if (cat == null) continue;

                    if (!string.IsNullOrWhiteSpace(cat.CategoryId))
                    {
                        report.NormalCountByCategoryId[cat.CategoryId] = 0;
                        report.ResolvedCountByCategoryId[cat.CategoryId] = 0;
                    }

                    if (cat.ColorAci > 0 && !aciToRule.ContainsKey(cat.ColorAci))
                    {
                        aciToRule[cat.ColorAci] = cat;
                    }

                    if (!string.IsNullOrWhiteSpace(cat.ColorRgb))
                    {
                        rgbToRule[NormalizeRgbString(cat.ColorRgb)] = cat;
                    }

                    if (cat.PipeLayers != null)
                    {
                        foreach (string layer in cat.PipeLayers)
                        {
                            if (!string.IsNullOrWhiteSpace(layer))
                            {
                                string trimmed = layer.Trim();
                                layerToRule[trimmed] = cat;
                                report.NormalCountByLayer[trimmed] = 0;
                            }
                        }
                    }
                }
            }

            var legacyLayerSet = new HashSet<string>(
                (cfg.LegacyWarningLayers ?? new List<string>()).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()),
                StringComparer.OrdinalIgnoreCase);

            // 从规划图层集合中排除已在当前配置中显式作为规划层的图层
            foreach (string planLayer in layerToRule.Keys)
            {
                legacyLayerSet.Remove(planLayer);
            }

            var potentialLegacyEntities = new List<(ObjectId Id, string Handle, string Layer, string DxfName, string BlockName, short EffectiveAci, Point3d Location)>();

            using (OpenCloseTransaction tr = _db.TransactionManager.StartOpenCloseTransaction())
            {
                // 预先读取 LayerTable 中各图层的默认颜色（用于解析 ByLayer=256 的实体实际颜色）
                var layerColorMap = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
                LayerTable lt = tr.GetObject(_db.LayerTableId, OpenMode.ForRead) as LayerTable;
                if (lt != null)
                {
                    foreach (ObjectId layerId in lt)
                    {
                        if (layerId.IsNull || layerId.IsErased) continue;
                        LayerTableRecord ltr = tr.GetObject(layerId, OpenMode.ForRead) as LayerTableRecord;
                        if (ltr != null && !string.IsNullOrWhiteSpace(ltr.Name))
                        {
                            layerColorMap[ltr.Name.Trim()] = ltr.Color;
                        }
                    }
                }

                BlockTable bt = tr.GetObject(_db.BlockTableId, OpenMode.ForRead) as BlockTable;
                if (bt != null && bt.Has(BlockTableRecord.ModelSpace))
                {
                    BlockTableRecord ms = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
                    if (ms != null)
                    {
                        foreach (ObjectId objId in ms)
                        {
                            if (progress != null && progress.Token.IsCancellationRequested)
                                break;

                            if (objId.IsNull || objId.IsErased)
                                continue;

                            Entity ent = tr.GetObject(objId, OpenMode.ForRead) as Entity;
                            if (ent == null || ent.IsErased || string.IsNullOrWhiteSpace(ent.Layer))
                                continue;

                            string layerName = ent.Layer.Trim();
                            string handle = objId.Handle.ToString();
                            string dxfName = objId.ObjectClass?.DxfName ?? ent.GetRXClass()?.DxfName ?? string.Empty;

                            // 分支 A：位于规划管线标准图层上的图元
                            if (layerToRule.TryGetValue(layerName, out PlanPipeCategoryRule layerRule))
                            {
                                Curve curve = ent as Curve;
                                if (curve == null)
                                    continue;

                                report.TotalScannedPlanCurves++;

                                short effectiveAci = ResolveEffectiveColor(ent, layerColorMap, out string effectiveRgb);
                                Point3d basePt = GetEntityRepresentativePoint(ent);

                                // A1. 校验是否为退化零长度几何（如 Handle=184, 188, 1C8 长度为 0 的退化线）
                                if (IsDegenerateCurve(curve, DegenerateLengthTolerance, out double curveLength))
                                {
                                    int vCount = (curve is Polyline dpl) ? dpl.NumberOfVertices : 2;
                                    bool isClosed = (curve is Polyline cpl) && cpl.Closed;
                                    var degItem = new PreCheckItem
                                    {
                                        Code = "PLAN_DEGENERATE_GEOMETRY",
                                        Severity = PreCheckSeverity.Reviewable,
                                        PlanIssueKind = PlanIssueKind.DegenerateGeometry,
                                        Title = "退化零长度管线",
                                        Description = $"规划图层 [{layerName}] 存在退化零长度图元 (Handle={handle}, 顶点数={vCount}, 闭合={isClosed}, 长度={curveLength:F4}m)。",
                                        TargetObjectId = objId,
                                        TargetHandle = handle,
                                        LayerName = layerName,
                                        EffectiveColorAci = effectiveAci,
                                        Location = basePt
                                    };
                                    report.DegenerateGeometryItems.Add(degItem);
                                    AddToIssueMapWithHistory(report.IssueMap, degItem, existingSameRuleIssues);
                                    continue;
                                }

                                // A2. 有效管线：执行“颜色优先 + 图层名一致性”双重核查
                                report.ValidPlanPipeCount++;

                                PlanPipeCategoryRule colorRule = MatchRuleByColor(effectiveAci, effectiveRgb, aciToRule, rgbToRule);
                                PlanPipeCategoryRule resolvedRule = colorRule ?? layerRule;

                                bool isColorLayerConsistent =
                                    colorRule != null &&
                                    layerRule != null &&
                                    string.Equals(colorRule.CategoryId, layerRule.CategoryId, StringComparison.OrdinalIgnoreCase);

                                if (resolvedRule != null && !string.IsNullOrWhiteSpace(resolvedRule.CategoryId))
                                {
                                    if (!report.ResolvedCountByCategoryId.ContainsKey(resolvedRule.CategoryId))
                                        report.ResolvedCountByCategoryId[resolvedRule.CategoryId] = 0;
                                    report.ResolvedCountByCategoryId[resolvedRule.CategoryId]++;
                                }

                                if (isColorLayerConsistent)
                                {
                                    report.NormalCompliantPipeCount++;
                                    report.NormalCountByCategoryId[resolvedRule.CategoryId]++;
                                    report.NormalCountByLayer[layerName]++;
                                }
                                else
                                {
                                    string colorDesc = FormatAciColorDescription(effectiveAci, colorRule);
                                    string expectedDesc = FormatAciColorDescription(layerRule.ColorAci, layerRule);
                                    string issueDesc = colorRule == null
                                        ? $"管线颜色与图层不一致：图元实际颜色为 [{colorDesc}]，不属于标准三类管线颜色，且与所在图层 [{layerName}] 要求的 [{expectedDesc}] 冲突。"
                                        : $"管线颜色与图层不一致：图元颜色为 [{colorDesc}]，但位于图层 [{layerName}]（要求 [{expectedDesc}]），已按颜色优先归为 [{colorRule.CategoryName}]。";

                                    var mismatchItem = new PreCheckItem
                                    {
                                        Code = "PLAN_COLOR_LAYER_MISMATCH",
                                        Severity = PreCheckSeverity.Reviewable,
                                        PlanIssueKind = PlanIssueKind.ColorLayerMismatch,
                                        Title = "颜色与图层不一致",
                                        Description = issueDesc,
                                        TargetObjectId = objId,
                                        TargetHandle = handle,
                                        LayerName = layerName,
                                        EffectiveColorAci = effectiveAci,
                                        Location = basePt
                                    };
                                    report.ColorLayerMismatchItems.Add(mismatchItem);
                                    AddToIssueMapWithHistory(report.IssueMap, mismatchItem, existingSameRuleIssues);
                                }

                                // A3. 构建并同步 EnrichedPlanPipeInfo 至 PipeDatasetContext
                                CadPolyline extractedPoly = ExtractPlanCurveToCadPolyline(curve, objId, handle, layerName, effectiveAci);

                                EnrichedPlanPipeInfo enriched = datasetContext != null
                                    ? datasetContext.GetOrCreatePlanPipeInfo(objId, handle)
                                    : new EnrichedPlanPipeInfo { PipeId = objId, Handle = handle };

                                enriched.PipeId = objId;
                                enriched.Handle = handle;
                                enriched.LayerName = layerName;
                                enriched.ColorAci = effectiveAci;
                                enriched.ResolvedCategoryId = resolvedRule?.CategoryId ?? string.Empty;
                                enriched.ResolvedCategoryName = resolvedRule?.CategoryName ?? string.Empty;
                                enriched.HasColorLayerMismatch = !isColorLayerConsistent;
                                if (extractedPoly != null)
                                {
                                    enriched.Polyline = extractedPoly;
                                }
                                enriched.SyncToPolylineAttributes();

                                report.EnrichedPipes[objId] = enriched;
                            }
                            // 分支 B：位于残留旧版图层（0污水-*）上的图元
                            else if (CheckLegacyResidualLayers && legacyLayerSet.Contains(layerName))
                            {
                                short effectiveAci = ResolveEffectiveColor(ent, layerColorMap, out _);
                                Point3d basePt = GetEntityRepresentativePoint(ent);
                                string blockName = (ent as BlockReference)?.Name ?? string.Empty;

                                potentialLegacyEntities.Add((objId, handle, layerName, dxfName, blockName, effectiveAci, basePt));
                            }
                        }
                    }
                }

                tr.Commit();
            }

            // 处理残留旧图层图元（当数量不超过合图阈值时，全部列为残留旧图层问题项）
            if (CheckLegacyResidualLayers &&
                potentialLegacyEntities.Count > 0 &&
                potentialLegacyEntities.Count <= DefaultLegacyResidualMaxThreshold)
            {
                foreach (var legacy in potentialLegacyEntities)
                {
                    string entityKindText = !string.IsNullOrWhiteSpace(legacy.BlockName)
                        ? $"属性块参照({legacy.BlockName})"
                        : $"曲线图元({legacy.DxfName})";

                    var legacyItem = new PreCheckItem
                    {
                        Code = "PLAN_LEGACY_RESIDUAL",
                        Severity = PreCheckSeverity.Reviewable,
                        PlanIssueKind = PlanIssueKind.LegacyResidualEntity,
                        Title = "残留旧图层图元",
                        Description = $"规划图纸中残留旧版图层 [{legacy.Layer}] 的{entityKindText} (Handle={legacy.Handle}, ACI={legacy.EffectiveAci})。",
                        TargetObjectId = legacy.Id,
                        TargetHandle = legacy.Handle,
                        LayerName = legacy.Layer,
                        EffectiveColorAci = legacy.EffectiveAci,
                        Location = legacy.Location
                    };

                    report.LegacyResidualItems.Add(legacyItem);
                    AddToIssueMapWithHistory(report.IssueMap, legacyItem, existingSameRuleIssues);
                }
            }

            LastReport = report;
            return report;
        }

        /// <summary>
        /// 增量重算（ICheckRule.CheckIncremental）：
        /// 当用户在 CAD 中修改了问题图元的颜色（如将绿色改为蓝色）、移动到正确图层或删除了残留/退化图元后，
        /// 自动重新评估并将已合规的记录状态更新为 Resolved。
        /// </summary>
        public Dictionary<ObjectId, CheckIssueItem> CheckIncremental(
            List<ObjectId> targetPipeIds,
            Dictionary<ObjectId, CheckIssueItem> existingIssues,
            ProgressContext progress = null)
        {
            var result = existingIssues != null
                ? new Dictionary<ObjectId, CheckIssueItem>(existingIssues)
                : new Dictionary<ObjectId, CheckIssueItem>();

            var session = DocumentSessionManager.GetOrCreateSession(_db);
            PlanDataCheckReport latestReport = InspectPlanningData(
                Settings ?? CadConfig.PlanningData,
                session?.DatasetContext,
                result,
                progress);

            var currentAbnormalIds = new HashSet<ObjectId>(latestReport.IssueMap.Keys);
            var existingKeys = result.Keys.ToList();

            foreach (ObjectId oldId in existingKeys)
            {
                CheckIssueItem oldItem = result[oldId];
                if (oldItem == null) continue;

                if (!string.IsNullOrWhiteSpace(oldItem.RuleId) &&
                    !string.Equals(oldItem.RuleId, RuleId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (oldId.IsNull || !oldId.IsValid || oldId.IsErased)
                {
                    oldItem.Status = IssueReviewStatus.Resolved;
                    oldItem.Description = "已修复：退化或残留图元已删除";
                    oldItem.Remark = "图元已删除（已归档）";
                    oldItem.PossibleMatches?.Clear();
                    continue;
                }

                if (!currentAbnormalIds.Contains(oldId))
                {
                    oldItem.Status = IssueReviewStatus.Resolved;
                    oldItem.Description = "已修复：规划管线颜色/图层或残留退化几何已合规";
                    oldItem.Remark = "颜色与图层已合规（已修复）";
                    oldItem.PossibleMatches?.Clear();
                }
            }

            foreach (var kvp in latestReport.IssueMap)
            {
                result[kvp.Key] = kvp.Value;
            }

            return result;
        }

        /// <summary>
        /// 判断数据库变更实体是否与规划数据自检相关（ICheckRule.IsRelevantChange）。
        /// </summary>
        public bool IsRelevantChange(Entity ent)
        {
            if (ent == null || string.IsNullOrWhiteSpace(ent.Layer))
                return false;

            var cfg = Settings ?? CadConfig.PlanningData;
            if (cfg == null)
                return false;

            string layer = ent.Layer.Trim();
            if (cfg.GetAllPipeLayers().Contains(layer))
                return true;

            if (cfg.LegacyWarningLayers != null &&
                cfg.LegacyWarningLayers.Any(l => string.Equals(l?.Trim(), layer, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// 当 CAD 数据库中的某个对象发生增、删、改时，判断该对象是否与本核查规则相关，
        /// 并返回受其影响、需要重新核查的 ObjectId 集合（ICheckRule.GetAffectedTargetIds）。
        /// </summary>
        public IEnumerable<ObjectId> GetAffectedTargetIds(DBObject changedObj)
        {
            if (changedObj == null || changedObj.ObjectId.IsNull)
                return Enumerable.Empty<ObjectId>();

            if (changedObj is Entity ent)
            {
                if (IsRelevantChange(ent))
                {
                    return new[] { ent.ObjectId };
                }

                // 若实体曾经在问题列表中（例如刚被移出规划图层或擦除），同样纳入重算范围
                var session = DocumentSessionManager.GetOrCreateSession(_db);
                if (session?.CurrentIssues != null && session.CurrentIssues.ContainsKey(ent.ObjectId))
                {
                    return new[] { ent.ObjectId };
                }
            }

            return Enumerable.Empty<ObjectId>();
        }

        private CadPolyline ExtractPlanCurveToCadPolyline(
            Curve curve,
            ObjectId objId,
            string handle,
            string layerName,
            short effectiveAci)
        {
            if (curve == null)
                return null;

            var attrs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["LAYER"] = layerName,
                ["COLOR_ACI"] = effectiveAci
            };

            if (curve is Polyline pl)
            {
                var cadPoly = GeometryHelper.ConvertAutoCadPolylineToPolyline(pl, objId, _geometryFactory, "plan", attrs);
                // 规划管线的管径 WIDTH 严格来源于关联文字标注（TextDiameter），移除由 Polyline.ConstantWidth 线宽回退写入的伪管径
                if (cadPoly?.Attributes != null && cadPoly.Attributes.ContainsKey("WIDTH"))
                {
                    cadPoly.Attributes["CONSTANT_LINE_WIDTH"] = cadPoly.Attributes["WIDTH"];
                    cadPoly.Attributes.Remove("WIDTH");
                }
                return cadPoly;
            }

            if (curve is Line line)
            {
                var c0 = new Coordinate(line.StartPoint.X, line.StartPoint.Y);
                var c1 = new Coordinate(line.EndPoint.X, line.EndPoint.Y);
                if (c0.Distance(c1) <= 1e-6)
                    return null;

                var lineGeom = _geometryFactory.CreateLineString(new[] { c0, c1 });
                var seg = new CadSegment
                {
                    Start = c0,
                    End = c1,
                    Geometry = lineGeom,
                    SourceHandles = new List<string> { handle },
                    SourceSegmentIndex = 0,
                    LayerName = layerName,
                    DatasetName = "plan"
                };

                return new CadPolyline
                {
                    SourceHandles = new List<string> { handle },
                    LayerName = layerName,
                    DatasetName = "plan",
                    Geometry = lineGeom,
                    Segments = new List<CadSegment> { seg },
                    Attributes = attrs
                };
            }

            return null;
        }

        private static void AddToIssueMapWithHistory(
            Dictionary<ObjectId, CheckIssueItem> issueMap,
            PreCheckItem preCheckItem,
            Dictionary<ObjectId, CheckIssueItem> existingSameRuleIssues)
        {
            if (preCheckItem == null || preCheckItem.TargetObjectId.IsNull)
                return;

            CheckIssueItem issue = preCheckItem.ToCheckIssueItem(DefaultRuleId);
            if (existingSameRuleIssues != null &&
                existingSameRuleIssues.TryGetValue(preCheckItem.TargetObjectId, out CheckIssueItem oldIssue) &&
                oldIssue != null &&
                oldIssue.Status == IssueReviewStatus.Ignored)
            {
                issue.Status = IssueReviewStatus.Ignored;
                issue.Remark = oldIssue.Remark;
            }

            issueMap[preCheckItem.TargetObjectId] = issue;
        }

        /// <summary>
        /// 解析实体的有效显示颜色（处理直接赋色 1~255、ByLayer=256 以及 RGB 真彩色）。
        /// </summary>
        public static short ResolveEffectiveColor(
            Entity ent,
            Dictionary<string, Color> layerColorMap,
            out string effectiveRgb)
        {
            effectiveRgb = string.Empty;
            if (ent == null)
                return 0;

            Color c = ent.Color;
            int rawAci = ent.ColorIndex;

            // 若实体为 ByLayer (256) 或 ByBlock (0)，回退读取所属图层的颜色
            if ((rawAci == 256 || rawAci == 0) &&
                !string.IsNullOrWhiteSpace(ent.Layer) &&
                layerColorMap != null &&
                layerColorMap.TryGetValue(ent.Layer.Trim(), out Color layerColor) &&
                layerColor != null)
            {
                c = layerColor;
                rawAci = layerColor.ColorIndex;
            }

            if (c != null && c.ColorMethod == ColorMethod.ByColor)
            {
                effectiveRgb = $"{c.Red},{c.Green},{c.Blue}";
            }

            return (short)rawAci;
        }

        private static PlanPipeCategoryRule MatchRuleByColor(
            short effectiveAci,
            string effectiveRgb,
            Dictionary<short, PlanPipeCategoryRule> aciToRule,
            Dictionary<string, PlanPipeCategoryRule> rgbToRule)
        {
            if (!string.IsNullOrWhiteSpace(effectiveRgb) &&
                rgbToRule != null &&
                rgbToRule.TryGetValue(NormalizeRgbString(effectiveRgb), out PlanPipeCategoryRule byRgb))
            {
                return byRgb;
            }

            if (effectiveAci > 0 &&
                aciToRule != null &&
                aciToRule.TryGetValue(effectiveAci, out PlanPipeCategoryRule byAci))
            {
                return byAci;
            }

            return null;
        }

        private static string NormalizeRgbString(string rgb)
        {
            if (string.IsNullOrWhiteSpace(rgb))
                return string.Empty;

            return rgb.Replace(" ", "").Trim();
        }

        private static bool IsDegenerateCurve(Curve curve, double tolerance, out double length)
        {
            length = 0.0;
            if (curve == null)
                return true;

            if (!PreCheckService.TryGetCurveLength(curve, out length))
                return true;

            if (length <= tolerance)
                return true;

            if (curve is Polyline pl && pl.NumberOfVertices < 2)
                return true;

            return false;
        }

        private static Point3d GetEntityRepresentativePoint(Entity ent)
        {
            if (ent == null)
                return Point3d.Origin;

            try
            {
                if (ent is BlockReference br)
                    return br.Position;

                if (ent is Curve curve)
                {
                    if (PreCheckService.TryGetCurveLength(curve, out double len) && len > 1e-4)
                    {
                        return curve.GetPointAtDist(len * 0.5);
                    }
                    return curve.StartPoint;
                }

                Extents3d ext = ent.GeometricExtents;
                return new Point3d(
                    (ext.MinPoint.X + ext.MaxPoint.X) * 0.5,
                    (ext.MinPoint.Y + ext.MaxPoint.Y) * 0.5,
                    0.0);
            }
            catch
            {
                try
                {
                    if (ent is Polyline pl && pl.NumberOfVertices > 0)
                        return pl.GetPoint3dAt(0);
                }
                catch
                {
                    // Ignore
                }
                return Point3d.Origin;
            }
        }

        private static string FormatAciColorDescription(short aci, PlanPipeCategoryRule matchedRule)
        {
            string colorName;
            switch (aci)
            {
                case 1:
                    colorName = "红色";
                    break;
                case 2:
                    colorName = "黄色";
                    break;
                case 3:
                    colorName = "绿色";
                    break;
                case 4:
                    colorName = "青色";
                    break;
                case 5:
                    colorName = "蓝色";
                    break;
                case 6:
                    colorName = "洋红色";
                    break;
                case 7:
                    colorName = "白/黑色";
                    break;
                default:
                    colorName = $"ACI={aci}";
                    break;
            }

            if (matchedRule != null && !string.IsNullOrWhiteSpace(matchedRule.CategoryName))
            {
                return $"ACI={aci} {colorName}({matchedRule.CategoryName})";
            }

            return $"ACI={aci} {colorName}";
        }
    }
}
