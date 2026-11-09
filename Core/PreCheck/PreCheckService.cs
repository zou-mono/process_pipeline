using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using NetTopologySuite.Geometries;
using process_pipeline.Forms;
using process_pipeline.Utils;
using AcadDb = Autodesk.AutoCAD.DatabaseServices;

namespace process_pipeline.Core.PreCheck
{
    /// <summary>
    /// 统一数据预核查核心服务（Stage 4 Commit 4.1）。
    /// 
    /// 核心职责：
    /// 1. 基础环境与阻断级预核查：校验目标图层存在性、有效实体非空（如管线 LINE/LWPOLYLINE/POLYLINE、箭头/属性块 INSERT、标注 TEXT/MTEXT）；
    /// 2. 跨图纸平面坐标范围（Extents3d / Envelope）对齐校验：计算主图纸与参考图纸（上版数据 / 勘测数据）有效实体外包框的重叠率，
    ///    防止因坐标系不一致（如地方独立坐标 vs CGCS2000 投影坐标）导致空间匹配全部落空；
    /// 3. 统一封装流向箭头核查（FlowArrowService）与多数据源综合核查（MatchPipe / CompareSegment）的前置校验入口，
    ///    在交互式 CAD 下自动弹窗提示阻断原因，在 accoreconsole 无头测试下自动静默输出日志。
    /// </summary>
    public static class PreCheckService
    {
        /// <summary>
        /// 校验指定数据库中的一组或多组图层及有效实体数量要求。
        /// 单次遍历 ModelSpace 完成全部图层组的实体计数，避免多次重复扫描。
        /// </summary>
        public static PreCheckSummary ValidateLayerGroups(
            AcadDb.Database db,
            IEnumerable<LayerGroupRequirement> requirements,
            string customBlockingTitle = null)
        {
            var summary = new PreCheckSummary();
            if (db == null)
            {
                summary.BlockingItems.Add(new PreCheckItem
                {
                    Code = "DB_NULL",
                    Severity = PreCheckSeverity.Blocking,
                    Title = customBlockingTitle ?? "目标 CAD 数据库为空，无法执行核查。",
                    Description = "[Hint]请确认当前文档或外部 DWG 数据源已正确加载。"
                });
                return summary;
            }

            var reqList = (requirements ?? Enumerable.Empty<LayerGroupRequirement>())
                .Where(r => r != null && r.LayerNames != null && r.LayerNames.Count > 0)
                .ToList();

            if (reqList.Count == 0)
                return summary;

            var details = reqList.Select(r => new LayerGroupCheckDetail { Requirement = r }).ToList();

            using (OpenCloseTransaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                LayerTable lt = tr.GetObject(db.LayerTableId, OpenMode.ForRead) as LayerTable;
                if (lt != null)
                {
                    foreach (LayerGroupCheckDetail detail in details)
                    {
                        foreach (string layerName in detail.Requirement.LayerNames)
                        {
                            if (!string.IsNullOrWhiteSpace(layerName) && lt.Has(layerName.Trim()))
                            {
                                detail.ExistingLayersInTable.Add(layerName.Trim());
                            }
                        }
                    }
                }

                BlockTable bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                if (bt != null && bt.Has(BlockTableRecord.ModelSpace))
                {
                    BlockTableRecord ms = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
                    if (ms != null)
                    {
                        // 预处理每个图层组的快速查找集合
                        var groupLayerSets = details.Select(d => new HashSet<string>(
                            d.Requirement.LayerNames.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()),
                            StringComparer.OrdinalIgnoreCase)).ToList();

                        var groupDxfSets = details.Select(d =>
                            d.Requirement.AllowedDxfNames != null && d.Requirement.AllowedDxfNames.Count > 0
                                ? new HashSet<string>(d.Requirement.AllowedDxfNames, StringComparer.OrdinalIgnoreCase)
                                : null).ToList();

                        foreach (ObjectId objId in ms)
                        {
                            if (objId.IsNull || objId.IsErased)
                                continue;

                            string dxfName = objId.ObjectClass?.DxfName ?? string.Empty;

                            // 仅当至少一个图层组允许该 DXF 类型时才打开实体读取 Layer
                            bool anyDxfMatch = false;
                            for (int i = 0; i < details.Count; i++)
                            {
                                if (groupDxfSets[i] == null || groupDxfSets[i].Contains(dxfName))
                                {
                                    anyDxfMatch = true;
                                    break;
                                }
                            }
                            if (!anyDxfMatch)
                                continue;

                            Entity ent = tr.GetObject(objId, OpenMode.ForRead) as Entity;
                            if (ent == null || ent.IsErased || string.IsNullOrWhiteSpace(ent.Layer))
                                continue;

                            string entLayer = ent.Layer.Trim();
                            for (int i = 0; i < details.Count; i++)
                            {
                                if (groupDxfSets[i] != null && !groupDxfSets[i].Contains(dxfName))
                                    continue;

                                if (groupLayerSets[i].Contains(entLayer))
                                {
                                    details[i].ValidEntityCount++;
                                    if (!details[i].EntityCountByLayer.ContainsKey(entLayer))
                                        details[i].EntityCountByLayer[entLayer] = 0;
                                    details[i].EntityCountByLayer[entLayer]++;
                                }
                            }
                        }
                    }
                }

                tr.Commit();
            }

            summary.LayerGroupDetails.AddRange(details);

            foreach (LayerGroupCheckDetail detail in details)
            {
                if (!detail.IsPassed)
                {
                    var req = detail.Requirement;
                    string entityHint = !string.IsNullOrWhiteSpace(req.EntityTypeHint)
                        ? req.EntityTypeHint
                        : (req.AllowedDxfNames != null && req.AllowedDxfNames.Count > 0
                            ? string.Join("/", req.AllowedDxfNames)
                            : "有效图形实体");

                    string code = detail.ExistingLayersInTable.Count == 0 ? "LAYER_MISSING" : "ENTITY_EMPTY";
                    string desc =
                        $"[Hint]{req.GroupTitle}要求：\n" +
                        $"1.Entity类型必须是{entityHint};\n" +
                        $"2.Layer名称必须满足：{string.Join("、", req.LayerNames)}.";

                    summary.BlockingItems.Add(new PreCheckItem
                    {
                        Code = code,
                        Severity = PreCheckSeverity.Blocking,
                        Title = customBlockingTitle ?? $"缺少合规的{req.GroupTitle}，无法进行检查.",
                        Description = desc
                    });
                }
            }

            return summary;
        }

        /// <summary>
        /// 流向箭头核查（FlowArrowService）专用前置预核查（Stage 4 Commit 4.1 & 4.2）。
        /// 支持直接传入已加载的管线数与箭头数，或扫描 Database 校验规划管线图层与流向箭头图层非空。
        /// </summary>
        public static PreCheckSummary ValidateFlowArrowPreConditions(
            AcadDb.Database db,
            IEnumerable<string> pipeLayers = null,
            IEnumerable<string> arrowLayers = null,
            int? preloadedPipeCount = null,
            int? preloadedArrowCount = null,
            bool showDialogOnError = false)
        {
            var effectivePipeLayers = (pipeLayers ?? CadConfig.PipeLayers ?? new HashSet<string>())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();
            var effectiveArrowLayers = (arrowLayers ?? CadConfig.ArrowLayers ?? new HashSet<string>())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            string title = "没有合规的管线数据和箭头数据，无法进行检查.";

            if (preloadedPipeCount.HasValue && preloadedArrowCount.HasValue)
            {
                var summary = new PreCheckSummary();
                if (preloadedPipeCount.Value <= 0 || preloadedArrowCount.Value <= 0)
                {
                    string desc =
                        $"[Hint]管线数据要求：\n" +
                        $"1.Entity类型必须是Line或者Polyline;\n" +
                        $"2.Layer名称必须满足：{string.Join("、", effectivePipeLayers)}.\n" +
                        $"[Hint]箭头数据要求：\n" +
                        $"1.Entity类型必须是BlockReference(块参照)或者Line/Polyline;\n" +
                        $"2.Layer名称必须满足：{string.Join("、", effectiveArrowLayers)}.";

                    summary.BlockingItems.Add(new PreCheckItem
                    {
                        Code = "FLOW_ARROW_INPUT_EMPTY",
                        Severity = PreCheckSeverity.Blocking,
                        Title = title,
                        Description = desc
                    });
                }

                if (summary.HasBlockingIssues && showDialogOnError && !Config.ConfigManager.IsHeadlessOrSuppressedUi)
                {
                    CadDialogService.ShowMessage(
                        summary.FormatBlockingDialogMessage(title),
                        type: MessageBoxType.Error);
                }

                return summary;
            }

            var requirements = new List<LayerGroupRequirement>
            {
                new LayerGroupRequirement
                {
                    GroupTitle = "管线数据",
                    LayerNames = effectivePipeLayers,
                    AllowedDxfNames = new List<string> { "LINE", "LWPOLYLINE", "POLYLINE" },
                    EntityTypeHint = "Line或者Polyline",
                    MinRequiredEntityCount = 1
                },
                new LayerGroupRequirement
                {
                    GroupTitle = "箭头数据",
                    LayerNames = effectiveArrowLayers,
                    AllowedDxfNames = new List<string> { "INSERT", "LINE", "LWPOLYLINE", "POLYLINE" },
                    EntityTypeHint = "BlockReference或者Line/Polyline",
                    MinRequiredEntityCount = 1
                }
            };

            PreCheckSummary result = ValidateLayerGroups(db, requirements, title);
            if (result.HasBlockingIssues && showDialogOnError && !Config.ConfigManager.IsHeadlessOrSuppressedUi)
            {
                CadDialogService.ShowMessage(
                    result.FormatBlockingDialogMessage(title),
                    type: MessageBoxType.Error);
            }

            return result;
        }

        /// <summary>
        /// 一键全量核查（CHECKPIPE）顶层统一前置预核查（Stage 6 Commit 6.4）：
        /// 一次性扫描当前激活主图的【规划管线图层】、【管径文字标注图层】与【流向箭头图层】，
        /// 当主图未加载任何有效规划管线时，将缺失提示合并为唯一一个综合对话框弹出并立即阻断后续规则，
        /// 彻底消除多个子规则连续弹出 3 个对话框的问题。
        /// </summary>
        public static PreCheckSummary ValidateFullPipelinePreConditions(
            AcadDb.Database db,
            bool showDialogOnBlocking = true)
        {
            var planPipeLayers = CadConfig.PlanningData.GetAllPipeLayers().Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            var planTextLayers = CadConfig.PlanningData.GetAllTextLayers().Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            var arrowLayers = CadConfig.PlanningData.GetArrowLayersSet().Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

            var requirements = new List<LayerGroupRequirement>
            {
                new LayerGroupRequirement
                {
                    GroupTitle = "规划管线数据",
                    LayerNames = planPipeLayers,
                    AllowedDxfNames = new List<string> { "LINE", "LWPOLYLINE", "POLYLINE" },
                    EntityTypeHint = "Line 或 Polyline（多段线）",
                    MinRequiredEntityCount = 1
                }
            };

            string title = "当前图纸未检测到合规的规划管线数据，无法执行综合核查。";
            PreCheckSummary summary = ValidateLayerGroups(db, requirements, title);

            if (summary.HasBlockingIssues)
            {
                // 将规划管线、管径文字标注、流向箭头的图层要求合并到同一条提示信息中，只弹一次窗
                summary.BlockingItems.Clear();
                string mergedDesc =
                    $"[Hint]1. 规划管线数据要求（核心必选）：\n" +
                    $"   • 图元类型：Line 或 Polyline（多段线）\n" +
                    $"   • 图层名称：{string.Join("、", planPipeLayers)}\n" +
                    $"[Hint]2. 管径文字标注要求（用于图属文字配对与管径提取）：\n" +
                    $"   • 图元类型：DBText（单行文字）或 MText（多行文字）\n" +
                    $"   • 图层名称：{string.Join("、", planTextLayers)}\n" +
                    $"[Hint]3. 流向箭头数据要求（用于管线流向一致性核查）：\n" +
                    $"   • 图元类型：BlockReference（块参照）或 Line/Polyline\n" +
                    $"   • 图层名称：{string.Join("、", arrowLayers)}";

                summary.BlockingItems.Add(new PreCheckItem
                {
                    Code = "FULL_CHECK_PLAN_DATA_EMPTY",
                    Severity = PreCheckSeverity.Blocking,
                    Title = title,
                    Description = mergedDesc
                });

                if (showDialogOnBlocking && !Config.ConfigManager.IsHeadlessOrSuppressedUi)
                {
                    CadDialogService.ShowMessage(
                        summary.FormatBlockingDialogMessage(title),
                        title: "管线综合核查 - 数据预检提示",
                        type: MessageBoxType.Warning);
                }
            }

            return summary;
        }

        /// <summary>
        /// 计算指定数据库中目标图层集合上所有有效实体的平面二维外包框（Envelope）。
        /// 自动过滤原点 (0,0) 附近的退化噪点与零面积无效图元。
        /// </summary>
        public static Envelope ComputeLayersPlanarEnvelope(
            AcadDb.Database db,
            IEnumerable<string> targetLayers,
            out int validEntityCount)
        {
            validEntityCount = 0;
            var env = new Envelope();
            if (db == null)
                return env;

            HashSet<string> layerFilter = null;
            if (targetLayers != null)
            {
                var list = targetLayers.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList();
                if (list.Count > 0)
                {
                    layerFilter = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
                }
            }

            using (OpenCloseTransaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                BlockTable bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                if (bt == null || !bt.Has(BlockTableRecord.ModelSpace))
                {
                    tr.Commit();
                    return env;
                }

                BlockTableRecord ms = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
                if (ms != null)
                {
                    foreach (ObjectId objId in ms)
                    {
                        if (objId.IsNull || objId.IsErased)
                            continue;

                        Entity ent = tr.GetObject(objId, OpenMode.ForRead) as Entity;
                        if (ent == null || ent.IsErased)
                            continue;

                        if (layerFilter != null)
                        {
                            if (string.IsNullOrWhiteSpace(ent.Layer) || !layerFilter.Contains(ent.Layer.Trim()))
                                continue;
                        }

                        if (ent is Curve curve)
                        {
                            if (!TryGetCurveLength(curve, out double len) || len <= 1e-4)
                                continue;
                        }

                        try
                        {
                            Extents3d ext = ent.GeometricExtents;
                            double minX = ext.MinPoint.X;
                            double minY = ext.MinPoint.Y;
                            double maxX = ext.MaxPoint.X;
                            double maxY = ext.MaxPoint.Y;

                            if (double.IsNaN(minX) || double.IsNaN(minY) || double.IsNaN(maxX) || double.IsNaN(maxY) ||
                                double.IsInfinity(minX) || double.IsInfinity(minY) || double.IsInfinity(maxX) || double.IsInfinity(maxY))
                            {
                                continue;
                            }

                            env.ExpandToInclude(minX, minY);
                            env.ExpandToInclude(maxX, maxY);
                            validEntityCount++;
                        }
                        catch
                        {
                            // 忽略个别无有效 GeometricExtents 的空块或退化实体
                        }
                    }
                }

                tr.Commit();
            }

            return env;
        }

        /// <summary>
        /// 校验两张 CAD 图纸在目标业务图层上的平面坐标范围（Extents3d / Envelope）是否对齐。
        /// 重叠率判据：IntersectionArea / Min(PrimaryArea, ReferenceArea) >= MinExtentOverlapRatio（默认来自 Config.json 0.05）。
        /// </summary>
        public static ExtentAlignmentResult ValidateCrossDrawingExtents(
            AcadDb.Database primaryDb,
            IEnumerable<string> primaryLayers,
            AcadDb.Database referenceDb,
            IEnumerable<string> referenceLayers,
            string primaryName = "规划数据",
            string referenceName = "参考数据",
            double? minOverlapRatio = null)
        {
            Envelope primaryEnv = ComputeLayersPlanarEnvelope(primaryDb, primaryLayers, out int primaryCount);
            Envelope refEnv = ComputeLayersPlanarEnvelope(referenceDb, referenceLayers, out int refCount);

            return ValidateCrossDrawingExtents(
                primaryEnv,
                primaryCount,
                refEnv,
                refCount,
                primaryName,
                referenceName,
                minOverlapRatio);
        }

        /// <summary>
        /// 校验两个已计算的平面二维外包框（Envelope）是否对齐。
        /// </summary>
        public static ExtentAlignmentResult ValidateCrossDrawingExtents(
            Envelope primaryEnv,
            int primaryEntityCount,
            Envelope referenceEnv,
            int referenceEntityCount,
            string primaryName = "规划数据",
            string referenceName = "参考数据",
            double? minOverlapRatio = null)
        {
            double requiredRatio = minOverlapRatio ?? CadConfig.General.MinExtentOverlapRatio;
            if (requiredRatio <= 0.0)
                requiredRatio = 0.05;

            var result = new ExtentAlignmentResult
            {
                PrimaryName = primaryName ?? "规划数据",
                ReferenceName = referenceName ?? "参考数据",
                PrimaryEnvelope = primaryEnv ?? new Envelope(),
                ReferenceEnvelope = referenceEnv ?? new Envelope(),
                PrimaryEntityCount = primaryEntityCount,
                ReferenceEntityCount = referenceEntityCount,
                RequiredMinOverlapRatio = requiredRatio
            };

            if (primaryEnv == null || primaryEnv.IsNull || primaryEntityCount <= 0 ||
                referenceEnv == null || referenceEnv.IsNull || referenceEntityCount <= 0)
            {
                result.IsAligned = false;
                result.OverlapRatio = 0.0;
                result.IntersectionEnvelope = new Envelope();
                result.SummaryMessage =
                    $"[{result.PrimaryName}] (有效图元={primaryEntityCount}) 与 [{result.ReferenceName}] (有效图元={referenceEntityCount}) 存在空范围，无法计算坐标重叠度。";
                return result;
            }

            Coordinate c1 = primaryEnv.Centre;
            Coordinate c2 = referenceEnv.Centre;
            double dx = c1.X - c2.X;
            double dy = c1.Y - c2.Y;
            result.CenterDistance = Math.Sqrt(dx * dx + dy * dy);

            Envelope inter = primaryEnv.Intersection(referenceEnv);
            result.IntersectionEnvelope = inter ?? new Envelope();

            double area1 = Math.Max(0.0, primaryEnv.Width * primaryEnv.Height);
            double area2 = Math.Max(0.0, referenceEnv.Width * referenceEnv.Height);
            double minArea = Math.Min(area1, area2);

            double interArea = (inter != null && !inter.IsNull)
                ? Math.Max(0.0, inter.Width * inter.Height)
                : 0.0;

            double overlapRatio = 0.0;
            if (minArea > 1e-6)
            {
                overlapRatio = Math.Min(1.0, interArea / minArea);
            }
            else if (inter != null && !inter.IsNull)
            {
                overlapRatio = 1.0;
            }

            result.OverlapRatio = overlapRatio;
            result.IsAligned = overlapRatio >= requiredRatio;

            string pRange = $"X[{primaryEnv.MinX:F1}~{primaryEnv.MaxX:F1}], Y[{primaryEnv.MinY:F1}~{primaryEnv.MaxY:F1}]";
            string rRange = $"X[{referenceEnv.MinX:F1}~{referenceEnv.MaxX:F1}], Y[{referenceEnv.MinY:F1}~{referenceEnv.MaxY:F1}]";

            if (result.IsAligned)
            {
                result.SummaryMessage =
                    $"[{result.PrimaryName}] 与 [{result.ReferenceName}] 平面坐标范围对齐通过：重叠率={overlapRatio:P1} (>= 阈值 {requiredRatio:P1}), " +
                    $"中心距={result.CenterDistance:F2}m ({result.PrimaryName}: {pRange}; {result.ReferenceName}: {rRange})";
            }
            else
            {
                result.SummaryMessage =
                    $"[{result.PrimaryName}] 与 [{result.ReferenceName}] 平面坐标范围严重偏离（疑似坐标系不一致）！重叠率={overlapRatio:P1} (< 阈值 {requiredRatio:P1}), " +
                    $"中心距={result.CenterDistance:F2}m。\n" +
                    $"  - {result.PrimaryName}范围: {pRange}\n" +
                    $"  - {result.ReferenceName}范围: {rRange}";
            }

            return result;
        }

        /// <summary>
        /// 执行跨图多数据源（规划数据 vs 上版数据 / 勘测数据）完整前置预核查：
        /// 同时验证各数据源目标图层与有效实体非空，以及跨图平面坐标范围对齐度。
        /// </summary>
        public static PreCheckSummary ValidateCrossDatasetPreConditions(
            AcadDb.Database planDb,
            AcadDb.Database previousDb = null,
            AcadDb.Database surveyDb = null,
            bool showDialogOnError = false)
        {
            var summary = new PreCheckSummary();
            var planLayers = CadConfig.PlanningData.GetAllPipeLayers().ToList();

            // 1. 校验规划数据图层与实体
            var planCheck = ValidateLayerGroups(planDb, new[]
            {
                new LayerGroupRequirement
                {
                    GroupTitle = "规划管线数据",
                    LayerNames = planLayers,
                    AllowedDxfNames = new List<string> { "LINE", "LWPOLYLINE", "POLYLINE" },
                    EntityTypeHint = "Line或者Polyline",
                    MinRequiredEntityCount = 1
                }
            });
            summary.LayerGroupDetails.AddRange(planCheck.LayerGroupDetails);
            summary.BlockingItems.AddRange(planCheck.BlockingItems);

            // 2. 若提供了上版数据，校验上版数据图层与跨图坐标范围对齐
            if (previousDb != null)
            {
                var prevLayers = CadConfig.PreviousData.PipeLayers ?? new List<string>();
                var prevCheck = ValidateLayerGroups(previousDb, new[]
                {
                    new LayerGroupRequirement
                    {
                        GroupTitle = "上版管线数据",
                        LayerNames = prevLayers,
                        AllowedDxfNames = new List<string> { "LINE", "LWPOLYLINE", "POLYLINE" },
                        EntityTypeHint = "Line或者Polyline",
                        MinRequiredEntityCount = 1
                    }
                });
                summary.LayerGroupDetails.AddRange(prevCheck.LayerGroupDetails);
                summary.BlockingItems.AddRange(prevCheck.BlockingItems);

                if (!planCheck.HasBlockingIssues && !prevCheck.HasBlockingIssues)
                {
                    ExtentAlignmentResult alignPrev = ValidateCrossDrawingExtents(
                        planDb,
                        planLayers,
                        previousDb,
                        prevLayers,
                        "规划数据",
                        "上版数据");
                    summary.ExtentAlignmentDetails.Add(alignPrev);

                    if (!alignPrev.IsAligned)
                    {
                        summary.BlockingItems.Add(new PreCheckItem
                        {
                            Code = "EXTENT_MISMATCH_PREVIOUS",
                            Severity = PreCheckSeverity.Blocking,
                            Title = "规划数据与上版数据平面坐标范围不匹配，无法执行跨图比对。",
                            Description = "[Hint]" + alignPrev.SummaryMessage
                        });
                    }
                }
            }

            // 3. 若提供了勘测数据，校验勘测数据图层与跨图坐标范围对齐
            if (surveyDb != null)
            {
                var surveyLayers = CadConfig.SurveyData.PipeLayers ?? new List<string>();
                var surveyCheck = ValidateLayerGroups(surveyDb, new[]
                {
                    new LayerGroupRequirement
                    {
                        GroupTitle = "勘测管线数据",
                        LayerNames = surveyLayers,
                        AllowedDxfNames = new List<string> { "LINE", "LWPOLYLINE", "POLYLINE" },
                        EntityTypeHint = "Line或者Polyline",
                        MinRequiredEntityCount = 1
                    }
                });
                summary.LayerGroupDetails.AddRange(surveyCheck.LayerGroupDetails);
                summary.BlockingItems.AddRange(surveyCheck.BlockingItems);

                if (!planCheck.HasBlockingIssues && !surveyCheck.HasBlockingIssues)
                {
                    ExtentAlignmentResult alignSurvey = ValidateCrossDrawingExtents(
                        planDb,
                        planLayers,
                        surveyDb,
                        surveyLayers,
                        "规划数据",
                        "勘测数据");
                    summary.ExtentAlignmentDetails.Add(alignSurvey);

                    if (!alignSurvey.IsAligned)
                    {
                        summary.BlockingItems.Add(new PreCheckItem
                        {
                            Code = "EXTENT_MISMATCH_SURVEY",
                            Severity = PreCheckSeverity.Blocking,
                            Title = "规划数据与勘测数据平面坐标范围不匹配，无法执行跨图核查。",
                            Description = "[Hint]" + alignSurvey.SummaryMessage
                        });
                    }
                }
            }

            if (summary.HasBlockingIssues && showDialogOnError)
            {
                CadDialogService.ShowMessage(
                    summary.FormatBlockingDialogMessage(),
                    type: MessageBoxType.Error);
            }

            return summary;
        }

        internal static bool TryGetCurveLength(Curve curve, out double length)
        {
            length = 0.0;
            if (curve == null)
                return false;

            try
            {
                if (curve is Polyline pl)
                {
                    length = pl.Length;
                    return !double.IsNaN(length) && !double.IsInfinity(length);
                }
                if (curve is Line line)
                {
                    length = line.Length;
                    return !double.IsNaN(length) && !double.IsInfinity(length);
                }

                double dist = Math.Abs(curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam));
                length = dist;
                return !double.IsNaN(length) && !double.IsInfinity(length);
            }
            catch
            {
                return false;
            }
        }
    }
}
