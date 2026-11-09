using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using NetTopologySuite.Geometries;
using NLog;
using process_pipeline.Core;
using process_pipeline.Core.DataSource;
using process_pipeline.Core.PreCheck;
using process_pipeline.Models;

namespace Cad.Plugin
{
    /// <summary>
    /// 阶段四 Commit 4.1 无 UI 命令行自动化测试：
    /// 验证统一数据预核查模块（Core/PreCheck）与规划数据自身合规核查服务（PlanDataCheckService）。
    /// 
    /// 覆盖场景：
    /// 1. 通用预核查（PreCheckService）：图层存在性、有效实体非空校验及阻断级（Blocking）错误识别；
    /// 2. 跨图纸平面坐标范围（Extents3d / Envelope）对齐度校验（新版数据 vs 上版数据 vs 勘测管，以及坐标系偏离阻断模拟）；
    /// 3. 规划数据（Data\新版数据.dwg）自身合规核查：
    ///    - 三类规划管线（规划新建管、现状管、规划改扩建管）正常分类统计与富化上下文写入；
    ///    - 颜色与图层名不一致检出：3-污水管-现状 图层中 4 条绿色（ACI=3）管线（Handle: 139, 213, 241, 242）；
    ///    - 退化零长度几何检出：3-污水管-现状 图层中 1 条长度为 0 的退化闭合线（Handle: 184）；
    ///    - 残留旧图层图元检出：0污水-已核实需新增 的 3 条多段线（10D, 10F, 1A3）与 0污水-属性 的 3 个 ws 属性块（4EA, 517, 52C）；
    /// 4. “颜色优先级高于图层名”归类验证及图元颜色修复/清理后的 CheckIncremental 增量刷新验证。
    /// </summary>
    public class Test_Check_Plan : CadCommandBase
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        [CommandMethod("test_check_plan")]
        public override void Execute()
        {
            Logger.Info("========== 开始执行 [test_check_plan] 统一数据预核查与规划数据合规核查测试 ==========");

            int passedChecks = 0;
            int failedChecks = 0;

            void AssertTrue(bool condition, string checkName)
            {
                if (condition)
                {
                    passedChecks++;
                    Logger.Info($"  [PASS] {checkName}");
                }
                else
                {
                    failedChecks++;
                    Logger.Error($"  [FAIL] {checkName}");
                }
            }

            string dataDir = @"D:\Codes\Tools\process_pipeline\Data";
            string planDwgPath = Path.Combine(dataDir, "新版数据.dwg");
            string prevDwgPath = Path.Combine(dataDir, "上版数据.dwg");
            string surveyDwgPath = Path.Combine(dataDir, "勘测管.dwg");

            AssertTrue(File.Exists(planDwgPath), $"规划数据文件存在: {planDwgPath}");
            AssertTrue(File.Exists(prevDwgPath), $"上版数据文件存在: {prevDwgPath}");
            AssertTrue(File.Exists(surveyDwgPath), $"勘测数据文件存在: {surveyDwgPath}");

            if (!File.Exists(planDwgPath))
            {
                Logger.Error("缺少 Data\\新版数据.dwg，测试提前终止。");
                return;
            }

            // ====================================================================
            // 场景 1：验证通用预核查（图层存在性、有效实体非空与阻断级拦截）
            // ====================================================================
            Logger.Info("--- 场景 1：验证 PreCheckService 图层存在性、有效实体非空与阻断级拦截 ---");
            using (var planDb = new Database(false, true))
            {
                planDb.ReadDwgFile(planDwgPath, FileOpenMode.OpenForReadAndAllShare, true, "");
                planDb.CloseInput(true);

                // 1.1 校验流向箭头与规划管线前置条件（新版数据.dwg 应正常通过）
                PreCheckSummary arrowPreCheck = PreCheckService.ValidateFlowArrowPreConditions(
                    planDb,
                    showDialogOnError: false);

                int detectedPipes = arrowPreCheck.LayerGroupDetails.FirstOrDefault(d => d.Requirement.GroupTitle == "管线数据")?.ValidEntityCount ?? 0;
                int detectedArrows = arrowPreCheck.LayerGroupDetails.FirstOrDefault(d => d.Requirement.GroupTitle == "箭头数据")?.ValidEntityCount ?? 0;
                Logger.Info($"  [流向箭头前置预核查] 阻断项={arrowPreCheck.BlockingItems.Count}, 规划管线实体数={detectedPipes}, 流向箭头实体数={detectedArrows}");

                AssertTrue(!arrowPreCheck.HasBlockingIssues && detectedPipes > 200 && detectedArrows > 0,
                    $"新版数据.dwg 通过流向箭头前置预核查 (管线={detectedPipes}, 箭头={detectedArrows})");

                // 1.2 模拟缺失图层组阻断检测
                PreCheckSummary missingLayerCheck = PreCheckService.ValidateLayerGroups(planDb, new[]
                {
                    new LayerGroupRequirement
                    {
                        GroupTitle = "测试不存在图层",
                        LayerNames = new List<string> { "99-不存在的测试图层A", "99-不存在的测试图层B" },
                        AllowedDxfNames = new List<string> { "LWPOLYLINE" },
                        EntityTypeHint = "Polyline",
                        MinRequiredEntityCount = 1
                    }
                });

                string blockingMsg = missingLayerCheck.FormatBlockingDialogMessage();
                AssertTrue(missingLayerCheck.HasBlockingIssues &&
                           missingLayerCheck.BlockingItems[0].Code == "LAYER_MISSING" &&
                           blockingMsg.Contains("[Title]") &&
                           blockingMsg.Contains("[Hint]"),
                    "缺失目标图层时准确触发 LAYER_MISSING 阻断级错误并生成标准 [Title]/[Hint] 提示文本");

                // 1.3 模拟预加载实体数为 0 的阻断检测
                PreCheckSummary emptyInputCheck = PreCheckService.ValidateFlowArrowPreConditions(
                    planDb,
                    preloadedPipeCount: 0,
                    preloadedArrowCount: 15,
                    showDialogOnError: false);
                AssertTrue(emptyInputCheck.HasBlockingIssues && emptyInputCheck.BlockingItems[0].Code == "FLOW_ARROW_INPUT_EMPTY",
                    "当管线或箭头有效图元数量为 0 时准确触发 FLOW_ARROW_INPUT_EMPTY 阻断拦截");
            }

            // ====================================================================
            // 场景 2：验证跨图纸平面坐标范围（Extents3d / Envelope）对齐校验
            // ====================================================================
            Logger.Info("--- 场景 2：验证跨图纸平面坐标范围 (Extents3d) 对齐度校验 ---");
            if (File.Exists(prevDwgPath) && File.Exists(surveyDwgPath))
            {
                using (var planDb = new Database(false, true))
                using (var prevDb = new Database(false, true))
                using (var surveyDb = new Database(false, true))
                {
                    planDb.ReadDwgFile(planDwgPath, FileOpenMode.OpenForReadAndAllShare, true, "");
                    planDb.CloseInput(true);
                    prevDb.ReadDwgFile(prevDwgPath, FileOpenMode.OpenForReadAndAllShare, true, "");
                    prevDb.CloseInput(true);
                    surveyDb.ReadDwgFile(surveyDwgPath, FileOpenMode.OpenForReadAndAllShare, true, "");
                    surveyDb.CloseInput(true);

                    PreCheckSummary crossSummary = PreCheckService.ValidateCrossDatasetPreConditions(
                        planDb,
                        prevDb,
                        surveyDb,
                        showDialogOnError: false);

                    foreach (ExtentAlignmentResult detail in crossSummary.ExtentAlignmentDetails)
                    {
                        Logger.Info($"  [跨图范围校验] {detail.SummaryMessage}");
                    }

                    AssertTrue(!crossSummary.HasBlockingIssues && crossSummary.ExtentAlignmentDetails.Count == 2,
                        "新版数据、上版数据与勘测数据三图跨图前置预核查全部通过（0 阻断项）");

                    ExtentAlignmentResult planVsPrev = crossSummary.ExtentAlignmentDetails.FirstOrDefault(d => d.ReferenceName == "上版数据");
                    ExtentAlignmentResult planVsSurvey = crossSummary.ExtentAlignmentDetails.FirstOrDefault(d => d.ReferenceName == "勘测数据");

                    AssertTrue(planVsPrev != null && planVsPrev.IsAligned && planVsPrev.OverlapRatio >= 0.80,
                        $"规划数据 vs 上版数据平面坐标范围高度重合 (重叠率={planVsPrev?.OverlapRatio:P1}, 中心距={planVsPrev?.CenterDistance:F2}m)");

                    AssertTrue(planVsSurvey != null && planVsSurvey.IsAligned && planVsSurvey.OverlapRatio >= 0.80,
                        $"规划数据 vs 勘测数据平面坐标范围高度重合 (重叠率={planVsSurvey?.OverlapRatio:P1}, 中心距={planVsSurvey?.CenterDistance:F2}m)");

                    // 模拟坐标系不一致（例如地方独立坐标 [0~500, 0~500] vs CGCS2000 投影坐标）
                    var localCoordEnv = new Envelope(0, 500, 0, 500);
                    ExtentAlignmentResult misalignedResult = PreCheckService.ValidateCrossDrawingExtents(
                        planVsPrev.PrimaryEnvelope,
                        planVsPrev.PrimaryEntityCount,
                        localCoordEnv,
                        100,
                        "规划数据(CGCS2000)",
                        "错误坐标系参考图(独立坐标)");

                    Logger.Info($"  [模拟坐标系偏离] {misalignedResult.SummaryMessage.Replace("\n", " | ")}");
                    AssertTrue(!misalignedResult.IsAligned && misalignedResult.OverlapRatio == 0.0,
                        "当两张图纸坐标系不一致导致平面范围无交集时，准确判定 IsAligned=false 并阻断预警");
                }
            }

            // ====================================================================
            // 场景 3：验证规划数据（Data\新版数据.dwg）自身合规核查（颜色优先、退化线、残留图层）
            // ====================================================================
            Logger.Info("--- 场景 3：验证规划数据 (新版数据.dwg) 颜色优先、退化几何与残留图层核查 ---");
            using (var planDb = new Database(false, true))
            {
                planDb.ReadDwgFile(planDwgPath, FileOpenMode.OpenForReadAndAllShare, true, "");
                planDb.CloseInput(true);

                var datasetContext = new PipeDatasetContext();
                var planCheckSvc = new PlanDataCheckService(planDb);
                PlanDataCheckReport report = planCheckSvc.InspectPlanningData(
                    CadConfig.PlanningData,
                    datasetContext);

                int normalNew = report.NormalCountByCategoryId.TryGetValue("PlanNew", out int nNew) ? nNew : 0;
                int normalExisting = report.NormalCountByCategoryId.TryGetValue("Existing", out int nEx) ? nEx : 0;
                int normalExpand = report.NormalCountByCategoryId.TryGetValue("PlanExpand", out int nExp) ? nExp : 0;

                int cLayerNew2025 = report.NormalCountByLayer.TryGetValue("3-污水管-2025新建", out int l1) ? l1 : 0;
                int cLayerPressNew = report.NormalCountByLayer.TryGetValue("3-污水压力管-规划新建", out int l2) ? l2 : 0;
                int cLayerExisting = report.NormalCountByLayer.TryGetValue("3-污水管-现状", out int l3) ? l3 : 0;
                int cLayerPressEx = report.NormalCountByLayer.TryGetValue("3-污水压力管-现状", out int l4) ? l4 : 0;
                int cLayerExpand = report.NormalCountByLayer.TryGetValue("3-污水管-规划扩建", out int l5) ? l5 : 0;

                Logger.Info(
                    $"  [规划管线统计] 扫描曲线总数={report.TotalScannedPlanCurves}, 有效管线数={report.ValidPlanPipeCount}, " +
                    $"正常合规数={report.NormalCompliantPipeCount} " +
                    $"(规划新建={normalNew}[2025新建:{cLayerNew2025}, 压力新建:{cLayerPressNew}], " +
                    $"现状管={normalExisting}[现状:{cLayerExisting}, 压力现状:{cLayerPressEx}], " +
                    $"规划扩建={normalExpand}[规划扩建:{cLayerExpand}])");

                AssertTrue(cLayerNew2025 == 10 && cLayerPressNew == 3 && normalNew == 13,
                    $"规划新建管（红色 ACI=1）正常图元数准确：3-污水管-2025新建=10 条, 3-污水压力管-规划新建=3 条 (实际={cLayerNew2025}+{cLayerPressNew})");

                AssertTrue(cLayerExisting == 219 && cLayerPressEx == 5 && normalExisting == 224,
                    $"现状管（蓝色 ACI=5）非退化正常图元数准确：3-污水管-现状=219 条(另有 3 条零长度退化线共 222 条蓝色图元), 3-污水压力管-现状=5 条 (实际={cLayerExisting}+{cLayerPressEx})");

                AssertTrue(cLayerExpand == 15 && normalExpand == 15,
                    $"规划改扩建管（洋红色 ACI=6）正常图元数准确：3-污水管-规划扩建=15 条 (实际={cLayerExpand})");

                // 3.1 验证颜色与图层名不一致项：4 条绿色 (ACI=3) 现状管 (Handle: 139, 213, 241, 242)
                var mismatchHandles = report.ColorLayerMismatchItems
                    .Select(i => i.TargetHandle)
                    .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                Logger.Info($"  [颜色与图层不一致异常] 共检出 {report.ColorLayerMismatchItems.Count} 条，Handles=[{string.Join(", ", mismatchHandles)}]");
                foreach (PreCheckItem item in report.ColorLayerMismatchItems)
                {
                    Logger.Info($"    -> Handle={item.TargetHandle}, Layer={item.LayerName}, ACI={item.EffectiveColorAci}: {item.Description}");
                }

                var expectedGreenHandles = new HashSet<string>(new[] { "139", "213", "241", "242" }, StringComparer.OrdinalIgnoreCase);
                bool greenMatchExact = report.ColorLayerMismatchItems.Count == 4 &&
                                       report.ColorLayerMismatchItems.All(i =>
                                           expectedGreenHandles.Contains(i.TargetHandle) &&
                                           i.EffectiveColorAci == 3 &&
                                           i.LayerName == "3-污水管-现状");
                AssertTrue(greenMatchExact,
                    $"准确检出 3-污水管-现状 图层中 4 条绿色(ACI=3)颜色冲突管线: 139, 213, 241, 242 (实际=[{string.Join(", ", mismatchHandles)}])");

                // 3.2 验证退化零长度几何：检出 3-污水管-现状 中的退化零长度线 (含 Handle=184, 188, 1C8)
                var degHandles = report.DegenerateGeometryItems
                    .Select(i => i.TargetHandle)
                    .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                Logger.Info($"  [退化零长度几何异常] 共检出 {report.DegenerateGeometryItems.Count} 条，Handles=[{string.Join(", ", degHandles)}]");
                foreach (PreCheckItem item in report.DegenerateGeometryItems)
                {
                    Logger.Info($"    -> Handle={item.TargetHandle}, Layer={item.LayerName}: {item.Description}");
                }

                var expectedDegHandles = new HashSet<string>(new[] { "184", "188", "1C8" }, StringComparer.OrdinalIgnoreCase);
                AssertTrue(report.DegenerateGeometryItems.Count == 3 &&
                           degHandles.All(h => expectedDegHandles.Contains(h)) &&
                           degHandles.Any(h => string.Equals(h, "184", StringComparison.OrdinalIgnoreCase)),
                    $"准确检出 3-污水管-现状 图层中全部 3 条长度为 0 的退化线 (含闭合退化线 Handle=184，实际=[{string.Join(", ", degHandles)}])");

                // 3.3 验证残留旧版图层图元：0污水-已核实需新增 (10D, 10F, 1A3) 与 0污水-属性 (4EA, 517, 52C)
                var legacyLineHandles = report.LegacyResidualItems
                    .Where(i => i.LayerName == "0污水-已核实需新增")
                    .Select(i => i.TargetHandle)
                    .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var legacyBlockHandles = report.LegacyResidualItems
                    .Where(i => i.LayerName == "0污水-属性")
                    .Select(i => i.TargetHandle)
                    .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                Logger.Info(
                    $"  [残留旧版图层异常] 共检出 {report.LegacyResidualItems.Count} 条：" +
                    $"0污水-已核实需新增=[{string.Join(", ", legacyLineHandles)}], " +
                    $"0污水-属性=[{string.Join(", ", legacyBlockHandles)}]");

                var expectedLegacyLines = new HashSet<string>(new[] { "10D", "10F", "1A3" }, StringComparer.OrdinalIgnoreCase);
                var expectedLegacyBlocks = new HashSet<string>(new[] { "4EA", "517", "52C" }, StringComparer.OrdinalIgnoreCase);

                AssertTrue(legacyLineHandles.Count == 3 && legacyLineHandles.All(h => expectedLegacyLines.Contains(h)),
                    $"准确检出残留旧图层 [0污水-已核实需新增] 的 3 条多段线: 10D, 10F, 1A3 (实际=[{string.Join(", ", legacyLineHandles)}])");

                AssertTrue(legacyBlockHandles.Count == 3 && legacyBlockHandles.All(h => expectedLegacyBlocks.Contains(h)),
                    $"准确检出残留旧图层 [0污水-属性] 的 3 个 ws 属性块: 4EA, 517, 52C (实际=[{string.Join(", ", legacyBlockHandles)}])");

                AssertTrue(report.IssueMap.Count == 13,
                    $"规划数据自检共生成 13 条待核查 CheckIssueItem (4条颜色冲突 + 3条零长度退化线 + 6条残留图层, 实际={report.IssueMap.Count})");

                // 3.4 验证 PipeDatasetContext 富化属性写入
                int enrichedWithCategory = datasetContext.PlanPipesById.Values.Count(p =>
                    !string.IsNullOrWhiteSpace(p.ResolvedCategoryId) &&
                    p.Polyline != null &&
                    p.Polyline.Attributes != null &&
                    p.Polyline.Attributes.ContainsKey("CATEGORY_ID") &&
                    p.Polyline.Attributes.ContainsKey("GHZT"));

                AssertTrue(datasetContext.PlanPipesById.Count == 256 && enrichedWithCategory == 256,
                    $"全部 256 条非退化有效规划管线（252正常+4颜色冲突）均已挂载分类属性写入 PipeDatasetContext (实际={enrichedWithCategory}/256)");
            }

            // ====================================================================
            // 场景 4：验证“颜色优先级高于图层名”归类逻辑与图元修复后的 CheckIncremental 增量重算
            // ====================================================================
            Logger.Info("--- 场景 4：验证“颜色优先级高于图层名”判定与图元修复后的 CheckIncremental 增量刷新 ---");
            using (var editDb = new Database(false, true))
            {
                editDb.ReadDwgFile(planDwgPath, FileOpenMode.OpenForReadAndAllShare, true, "");
                editDb.CloseInput(true);

                var session = DocumentSessionManager.GetOrCreateSession(editDb);
                var planCheckSvc = new PlanDataCheckService(editDb);
                Dictionary<ObjectId, CheckIssueItem> initialIssues = planCheckSvc.CheckAll();

                // 4.1 将 3-污水管-现状 图层上的 Handle=139 管线颜色改为红色 (ACI=1，代表规划新建管)，验证颜色优先级高于图层名
                PreCheckItem item139 = planCheckSvc.LastReport.ColorLayerMismatchItems.First(i => i.TargetHandle == "139");
                using (Transaction tr = editDb.TransactionManager.StartTransaction())
                {
                    Entity ent139 = tr.GetObject(item139.TargetObjectId, OpenMode.ForWrite) as Entity;
                    ent139.Color = Color.FromColorIndex(ColorMethod.ByAci, 1); // 改为红色 ACI=1
                    tr.Commit();
                }

                PlanDataCheckReport afterRedReport = planCheckSvc.InspectPlanningData(CadConfig.PlanningData, session.DatasetContext);
                EnrichedPlanPipeInfo enriched139 = session.DatasetContext.PlanPipesByHandle["139"];
                Logger.Info(
                    $"  [颜色优先验证] Handle=139 (位于图层 {enriched139.LayerName}) 改赋红色(ACI=1)后 -> " +
                    $"判定类别={enriched139.ResolvedCategoryId}({enriched139.ResolvedCategoryName}), 颜色图层不一致标记={enriched139.HasColorLayerMismatch}");

                AssertTrue(enriched139.ResolvedCategoryId == "PlanNew" &&
                           enriched139.ResolvedCategoryName == "规划新建管" &&
                           enriched139.HasColorLayerMismatch,
                    "当管线位于现状层但颜色为红色(ACI=1)时，严格按“颜色优先级高于图层名”归类为[规划新建管]并保留颜色图层冲突提示");

                // 4.2 在内存中修复全部 13 处异常（4条绿线改回蓝色 ACI=5，擦除 3 条退化线与 6 个残留旧图层图元），验证 CheckIncremental 增量归档
                using (Transaction tr = editDb.TransactionManager.StartTransaction())
                {
                    foreach (PreCheckItem mismatch in planCheckSvc.LastReport.ColorLayerMismatchItems)
                    {
                        Entity ent = tr.GetObject(mismatch.TargetObjectId, OpenMode.ForWrite) as Entity;
                        if (ent != null)
                        {
                            ent.Color = Color.FromColorIndex(ColorMethod.ByAci, 5); // 恢复现状管标准蓝色 ACI=5
                        }
                    }

                    foreach (PreCheckItem deg in planCheckSvc.LastReport.DegenerateGeometryItems)
                    {
                        Entity ent = tr.GetObject(deg.TargetObjectId, OpenMode.ForWrite) as Entity;
                        ent?.Erase(true);
                    }

                    foreach (PreCheckItem legacy in planCheckSvc.LastReport.LegacyResidualItems)
                    {
                        Entity ent = tr.GetObject(legacy.TargetObjectId, OpenMode.ForWrite) as Entity;
                        ent?.Erase(true);
                    }

                    tr.Commit();
                }

                Dictionary<ObjectId, CheckIssueItem> afterIncremental = planCheckSvc.CheckIncremental(
                    initialIssues.Keys.ToList(),
                    initialIssues);

                int resolvedCount = afterIncremental.Values.Count(i => i.Status == IssueReviewStatus.Resolved);
                int remainingPending = afterIncremental.Values.Count(i => i.Status != IssueReviewStatus.Resolved);
                Logger.Info($"  [增量重算验证] 修复全部 13 处异常后，CheckIncremental 结果：已修复(Resolved)={resolvedCount}, 剩余未修复={remainingPending}");

                AssertTrue(resolvedCount == 13 && remainingPending == 0,
                    $"PlanDataCheckService.CheckIncremental 在颜色修正与残留清理后自动将全部 13 条问题状态更新为 Resolved (实际={resolvedCount}/13)");
            }

            string finalSummary = $"[test_check_plan] 测试完成：通过 {passedChecks} 项，失败 {failedChecks} 项。";
            if (failedChecks == 0)
            {
                Logger.Info($"========== {finalSummary} ==========");
            }
            else
            {
                Logger.Error($"========== {finalSummary} ==========");
            }
        }
    }
}
