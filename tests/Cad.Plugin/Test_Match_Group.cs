using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using NLog;
using process_pipeline.Commands;
using process_pipeline.Core;
using process_pipeline.Forms;
using process_pipeline.Geometry;
using process_pipeline.Models;

namespace Cad.Plugin
{
    /// <summary>
    /// 管线与属性块空间就近配对及 CAD Group 合法性核查无 UI 自动化测试（Stage 3 Commit 3.1）。
    /// 
    /// 覆盖场景：
    /// 1. 读取 Data\上版数据.dwg（317 条多段线 + 317 个 ws 属性块，GroupDictionary.Count == 0）：
    ///    - 验证双距离判据（外包框中心距离 + 曲线距离 + LEN 管长校验）对 317 条管线（含 95 条块距曲线 5m~159.7m 的大弯折管线）的 100% 配对能力；
    ///    - 验证配对提取出的 16+ 项业务属性字段完整性；
    ///    - 验证近距离并行管线歧义候选（PossibleMatches）检出能力。
    /// 2. 读取 Data\勘测管.dwg（5,823 条勘测管多段线 + 5,823 个 1 属性块，GroupDictionary.Count == 5823）：
    ///    - 模式 A（启用原生 Group 读取）：验证 5,823 个既有 CAD Group 全部 1:1 合法通过；
    ///    - 模式 B（忽略原生 Group 模拟未成组状态）：验证纯空间邻近算法对 5,823 条勘测管的配对准确率与 WIDTH/HEITH 属性提取。
    /// </summary>
    public class Test_Match_Group : CadCommandBase
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        [CommandMethod("test_match_group")]
        public override void Execute()
        {
            Logger.Info("========== 开始执行 [test_match_group] 管线-属性块空间配对与 Group 核查测试 ==========");

            string prevDwgPath = @"D:\Codes\Tools\process_pipeline\Data\上版数据.dwg";
            string surveyDwgPath = @"D:\Codes\Tools\process_pipeline\Data\勘测管.dwg";

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

            var matcher = new PipeSpatialMatcher();

            // ====================================================================
            // 场景 1：测试 Data\上版数据.dwg（317 条管线 + 317 个 ws 块，无原生 Group）
            // ====================================================================
            Logger.Info($"--- 场景 1：测试上版数据空间双判据配对 ({prevDwgPath}) ---");
            AssertTrue(File.Exists(prevDwgPath), $"上版数据文件存在: {prevDwgPath}");

            if (File.Exists(prevDwgPath))
            {
                var prevPipeLayers = new[]
                {
                    "0污水-待补测现状管",
                    "0污水-规划新建",
                    "0污水-规划改建",
                    "0污水-已核实需新增"
                };
                var prevAttrLayers = new[] { "0污水-属性" };

                using (var prevDb = new Database(false, true))
                {
                    prevDb.ReadDwgFile(prevDwgPath, FileOpenMode.OpenForReadAndAllShare, true, "");
                    prevDb.CloseInput(true);

                    PipeSpatialMatchSummary prevSummary = matcher.MatchDatabaseBlockAttributes(
                        prevDb,
                        prevPipeLayers,
                        prevAttrLayers,
                        PipeSpatialMatchOptions.FromConfig());

                    Logger.Info(
                        $"  [统计] 上版管线总数={prevSummary.TotalPipeCount}, ws属性块总数={prevSummary.TotalAttachmentCount}, " +
                        $"原生Group数={prevSummary.TotalExistingGroupCount}, 唯一配对={prevSummary.UniqueMatches.Count}, " +
                        $"近距歧义候选={prevSummary.ConflictMatches.Count}, 未配对管线={prevSummary.UnmatchedPipes.Count}, " +
                        $"孤立属性块={prevSummary.OrphanAttachments.Count}");

                    AssertTrue(prevSummary.TotalPipeCount == 317,
                        $"上版数据四图层共读取到 317 条管线 (实际={prevSummary.TotalPipeCount})");
                    AssertTrue(prevSummary.TotalAttachmentCount == 317,
                        $"上版数据 '0污水-属性' 图层共读取到 317 个 ws 属性块 (实际={prevSummary.TotalAttachmentCount})");
                    AssertTrue(prevSummary.TotalExistingGroupCount == 0,
                        $"上版数据原始 GroupDictionary 数量为 0 (实际={prevSummary.TotalExistingGroupCount})");

                    int totalMatchedPrev = prevSummary.UniqueMatches.Count + prevSummary.ConflictMatches.Count;
                    AssertTrue(totalMatchedPrev == 317 && prevSummary.UnmatchedPipes.Count == 0 && prevSummary.OrphanAttachments.Count == 0,
                        $"317 条上版管线与 317 个 ws 块完成 100% 空间配对，0 遗漏、0 孤立块 (配对总数={totalMatchedPrev})");

                    // 统计大弯折管线（GIS 导出块距曲线 > 5.0m，最大达 159.7m）是否全部通过外包框中心判据成功配对，
                    // 以及 3 条人工新增管线（0污水-已核实需新增，距曲线 4.2m~8.9m）是否通过曲线邻近判据成功配对
                    var allMatchedItems = prevSummary.UniqueMatches.Concat(prevSummary.ConflictMatches).ToList();
                    var gisLargeBendItems = allMatchedItems
                        .Where(m => m.Channel == SpatialMatchChannel.ExtentsCenterMatch && m.DistanceToCurve > 5.0)
                        .ToList();
                    double maxDistToCurve = allMatchedItems.Max(m => m.DistanceToCurve);
                    Logger.Info(
                        $"  [大弯折与分渠道统计] GIS大弯折管线(距曲线>5m)={gisLargeBendItems.Count} 条 " +
                        $"(最大距曲线={maxDistToCurve:F2}m, 外包框中心命中={prevSummary.ExtentsCenterMatchedCount}, 曲线邻近命中={prevSummary.CurveProximityMatchedCount})");

                    AssertTrue(gisLargeBendItems.Count >= 90 && gisLargeBendItems.All(m => m.DistanceToExtentsCenter <= 1.5),
                        $"GIS 大弯折管线 ({gisLargeBendItems.Count} 条, 最大距曲线 {maxDistToCurve:F1}m) 全部通过外包框中心判据精准捕获");

                    // 验证属性字典字段完整性（WIDTH, HEITH, QSBG, MDBG, PD, LEN, GHZT, LX 等）
                    int validAttrCount = prevSummary.MatchedAttributesByPipeHandle.Values
                        .Count(attrs => attrs.ContainsKey("WIDTH") && attrs.ContainsKey("LEN") && attrs.ContainsKey("GHZT") && attrs.ContainsKey("LX"));
                    AssertTrue(validAttrCount == 317,
                        $"全部 317 条上版管线均已成功挂载 WIDTH/LEN/GHZT/LX 等核心业务属性 (实际={validAttrCount})");

                    if (prevSummary.ConflictMatches.Count > 0)
                    {
                        foreach (var conflict in prevSummary.ConflictMatches.Take(5))
                        {
                            Logger.Info(
                                $"  [歧义明细] 管线[{conflict.Pipe.Handle}] <-> 首选块[{conflict.MatchedAttachment.Handle}], " +
                                $"候选数={conflict.PossibleMatches.Count}, 说明: {conflict.Description}");
                        }
                    }

                    // 生成可视化核验图纸：Data\上版数据_配对结果预览.dwg
                    string visualOutputDwgPath = @"D:\Codes\Tools\process_pipeline\Data\上版数据_配对结果预览.dwg";
                    int drawnLinkCount = GenerateVisualCheckDwg(prevDwgPath, visualOutputDwgPath, prevSummary);
                    AssertTrue(File.Exists(visualOutputDwgPath) && drawnLinkCount == 317,
                        $"已生成可视化配对连线图纸 [{visualOutputDwgPath}]，共绘制 {drawnLinkCount} 组醒目配对连线与标记");
                }
            }

            // ====================================================================
            // 场景 2：测试 Data\勘测管.dwg（5,823 条管线 + 5,823 个 1 块，含 5,823 个原生 Group）
            // ====================================================================
            Logger.Info($"--- 场景 2：测试勘测管数据 Group 合法性校验与无 Group 空间配对 ({surveyDwgPath}) ---");
            AssertTrue(File.Exists(surveyDwgPath), $"勘测管数据文件存在: {surveyDwgPath}");

            if (File.Exists(surveyDwgPath))
            {
                var surveyLayers = new[] { "勘测管" };

                using (var surveyDb = new Database(false, true))
                {
                    surveyDb.ReadDwgFile(surveyDwgPath, FileOpenMode.OpenForReadAndAllShare, true, "");
                    surveyDb.CloseInput(true);

                    // 2A：验证既有 5,823 个 CAD Group 的 1:1 合法性
                    var groupCheckOpts = PipeSpatialMatchOptions.FromConfig();
                    groupCheckOpts.UseExistingCadGroups = true;

                    PipeSpatialMatchSummary groupSummary = matcher.MatchDatabaseBlockAttributes(
                        surveyDb,
                        surveyLayers,
                        surveyLayers,
                        groupCheckOpts);

                    Logger.Info(
                        $"  [模式 A - 原生 Group 校验] 管线总数={groupSummary.TotalPipeCount}, 属性块总数={groupSummary.TotalAttachmentCount}, " +
                        $"合法 1:1 Group 数={groupSummary.ValidGroupPairCount}, 异常 Group 数={groupSummary.InvalidGroupCount}");

                    AssertTrue(groupSummary.TotalPipeCount == 5823 && groupSummary.TotalAttachmentCount == 5823,
                        $"勘测管图纸共读取到 5823 条管线与 5823 个属性块 (管线={groupSummary.TotalPipeCount}, 块={groupSummary.TotalAttachmentCount})");
                    AssertTrue(groupSummary.ValidGroupPairCount == 5823 && groupSummary.InvalidGroupCount == 0,
                        $"勘测管全部 5823 个原生 CAD Group 均通过 1:1 合法性核查 (合法={groupSummary.ValidGroupPairCount}, 异常={groupSummary.InvalidGroupCount})");

                    int boxCulvertCount = groupSummary.MatchedAttributesByPipeHandle.Values
                        .Count(attrs => attrs.TryGetValue("HEITH", out object h) && h != null && h.ToString().Trim() != "0" && h.ToString().Trim() != "");
                    AssertTrue(boxCulvertCount == 128,
                        $"准确识别勘测管中 HEITH != '0' 的渠箱记录共 128 条 (实际={boxCulvertCount})");

                    // 2B：忽略原生 Group，模拟未成组状态下对 5,823 条勘测管的纯空间配对
                    var spatialOnlyOpts = PipeSpatialMatchOptions.FromConfig();
                    spatialOnlyOpts.UseExistingCadGroups = false;
                    spatialOnlyOpts.UseXDataLinks = false;

                    PipeSpatialMatchSummary spatialSummary = matcher.MatchDatabaseBlockAttributes(
                        surveyDb,
                        surveyLayers,
                        surveyLayers,
                        spatialOnlyOpts);

                    int totalSpatialMatched = spatialSummary.UniqueMatches.Count + spatialSummary.ConflictMatches.Count;
                    Logger.Info(
                        $"  [模式 B - 模拟未成组纯空间配对] 配对成功={totalSpatialMatched}/5823 " +
                        $"(唯一={spatialSummary.UniqueMatches.Count}, 邻近歧义={spatialSummary.ConflictMatches.Count}, 未匹配={spatialSummary.UnmatchedPipes.Count})");

                    AssertTrue(totalSpatialMatched == 5823 && spatialSummary.UnmatchedPipes.Count == 0,
                        $"模拟无 Group 状态下，5823 条勘测管通过纯空间距离实现 100% 配对覆盖 (实际={totalSpatialMatched})");

                    // 2C：验证 MatchGroupService 在全部 5,823 条勘测管均已 1:1 绑定 Group 时检出 0 条异常；
                    //     而在模拟无 Group 状态（UseExistingCadGroups = false）下准确检出 10 条 Pending 空间冲突与 5,813 条 ToRecheck 待绑定项
                    var surveyMatchGroupSvc = new MatchGroupService(surveyDb);
                    var surveyGroupIssues = surveyMatchGroupSvc.CheckAll();
                    AssertTrue(surveyGroupIssues != null && surveyGroupIssues.Count == 0,
                        $"MatchGroupService 核查勘测管图纸（5823 个合法 Group）应返回 0 条问题 (实际={surveyGroupIssues?.Count ?? -1})");

                    surveyMatchGroupSvc.MatchOptions = spatialOnlyOpts;
                    var surveyUnboundIssues = surveyMatchGroupSvc.CheckAll();
                    int surveyPendingConflicts = surveyUnboundIssues.Values.Count(i => i.Status == IssueReviewStatus.Pending && i.PossibleMatches.Count >= 2);
                    int surveyToRecheckUniques = surveyUnboundIssues.Values.Count(i => i.Status == IssueReviewStatus.ToRecheck);
                    AssertTrue(surveyPendingConflicts == 10 && surveyToRecheckUniques == 5813,
                        $"MatchGroupService 在无 Group 勘测管上准确区分 10 条一对多冲突(Pending)与 5813 条唯一匹配(ToRecheck) (冲突={surveyPendingConflicts}, 唯一={surveyToRecheckUniques})");
                }
            }

            // ====================================================================
            // 场景 3：验证 Commit 3.2 GeometryHelper.CollectPolylinesFromLayer(s) 三级优先级属性提取
            // ====================================================================
            Logger.Info("--- 场景 3：验证 GeometryHelper.CollectPolylinesFromLayers 三级优先级属性提取 ---");
            if (File.Exists(prevDwgPath))
            {
                var prevPipeLayers = new[]
                {
                    "0污水-待补测现状管",
                    "0污水-规划新建",
                    "0污水-规划改建",
                    "0污水-已核实需新增"
                };

                using (var prevDb = new Database(false, true))
                {
                    prevDb.ReadDwgFile(prevDwgPath, FileOpenMode.OpenForReadAndAllShare, true, "");
                    prevDb.CloseInput(true);

                    var gf = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
                    List<CadPolyline> collectedPrevPolylines = GeometryHelper.CollectPolylinesFromLayers(
                        prevDb,
                        prevPipeLayers,
                        gf,
                        "previous");

                    int withCoreAttrsCount = collectedPrevPolylines.Count(p =>
                        p?.Attributes != null &&
                        p.Attributes.ContainsKey("WIDTH") &&
                        p.Attributes.ContainsKey("LEN") &&
                        p.Attributes.ContainsKey("GHZT") &&
                        p.Attributes.ContainsKey("LX"));

                    Logger.Info(
                        $"  [GeometryHelper 三级提取] 上版数据(无原生Group)提取 CadPolyline={collectedPrevPolylines.Count} 条, " +
                        $"成功挂载完整块属性={withCoreAttrsCount} 条");

                    AssertTrue(collectedPrevPolylines.Count == 317 && withCoreAttrsCount == 317,
                        $"GeometryHelper.CollectPolylinesFromLayers 在无原生 Group 的上版数据上自动通过空间配对挂载全部 317 条管线属性 (实际={withCoreAttrsCount}/317)");
                }
            }

            // ====================================================================
            // 场景 4：验证 Commit 3.2 MatchGroupService + BindPipeGroupOperation (Execute/Undo/Redo) + XData 二级恢复
            // ====================================================================
            Logger.Info("--- 场景 4：验证 MatchGroupService 与 BindPipeGroupOperation (Group + XData 可逆绑定与恢复) ---");
            if (File.Exists(prevDwgPath))
            {
                var prevPipeLayers = new[]
                {
                    "0污水-待补测现状管",
                    "0污水-规划新建",
                    "0污水-规划改建",
                    "0污水-已核实需新增"
                };
                var prevAttrLayers = new[] { "0污水-属性" };

                using (var editDb = new Database(false, true))
                {
                    editDb.ReadDwgFile(prevDwgPath, FileOpenMode.OpenForReadAndAllShare, true, "");
                    editDb.CloseInput(true);

                    var matchGroupSvc = new MatchGroupService(editDb);
                    Dictionary<ObjectId, CheckIssueItem> initialIssues = matchGroupSvc.CheckAll();

                    int pendingConflictCount = initialIssues.Values.Count(i => i.Status == IssueReviewStatus.Pending);
                    int toRecheckUniqueCount = initialIssues.Values.Count(i => i.Status == IssueReviewStatus.ToRecheck);
                    Logger.Info(
                        $"  [MatchGroupService 初始核查] 总记录={initialIssues.Count}, 待核查冲突={pendingConflictCount}, 待复核唯一匹配={toRecheckUniqueCount}");

                    AssertTrue(initialIssues.Count == 317 && pendingConflictCount == 0 && toRecheckUniqueCount == 317,
                        $"MatchGroupService 检出上版数据全部 317 条未建 Group 的唯一匹配项 (ToRecheck={toRecheckUniqueCount}, Pending={pendingConflictCount})");

                    bool allHaveAuxLines = initialIssues.Values.All(i => i.PossibleMatches != null && i.PossibleMatches.Count >= 1);
                    AssertTrue(allHaveAuxLines,
                        "所有待核查/待复核记录均已生成 PossibleMatches 瞬态辅助连线数据");

                    // 4.1 执行 BindPipeGroupOperation 批量绑定 317 组唯一匹配项（写入 CAD Group + PIPE_CHECK_LINK XData）
                    var workingIssues = new Dictionary<ObjectId, CheckIssueItem>(initialIssues);
                    BindPipeGroupOperation bindOp = matchGroupSvc.CreateBatchBindOperationForUniqueMatches(
                        workingIssues,
                        (restoredList, isUndo) =>
                        {
                            foreach (var pair in restoredList)
                            {
                                if (pair.Snapshot != null)
                                    workingIssues[pair.PipeId] = pair.Snapshot;
                            }
                        });

                    var undoRedoMgr = new HybridUndoRedoManager();
                    undoRedoMgr.ExecuteOperation(bindOp);

                    int resolvedInCallback = workingIssues.Values.Count(i => i.Status == IssueReviewStatus.Resolved);
                    AssertTrue(bindOp != null && bindOp.LastBoundCount == 317 && resolvedInCallback == 317,
                        $"BindPipeGroupOperation.Execute 成功绑定 317 组 CAD Group 与 XData，并将 317 条记录状态更新为 Resolved (实际={bindOp?.LastBoundCount})");

                    // 4.2 验证增量重算 CheckIncremental 自动识别已绑定的 317 组 Group
                    var afterIncremental = matchGroupSvc.CheckIncremental(new List<ObjectId>(), new Dictionary<ObjectId, CheckIssueItem>(initialIssues));
                    int resolvedAfterInc = afterIncremental.Values.Count(i => i.Status == IssueReviewStatus.Resolved);
                    int remainingPendingAfterInc = afterIncremental.Values.Count(i => i.Status != IssueReviewStatus.Resolved);
                    AssertTrue(resolvedAfterInc == 317 && remainingPendingAfterInc == 0,
                        $"MatchGroupService.CheckIncremental 增量重算准确归档全部 317 条已建 Group 记录 (已修复={resolvedAfterInc}, 剩余未修复={remainingPendingAfterInc})");

                    // 4.3 验证第二级 XData (PIPE_CHECK_LINK) 独立恢复能力（禁用 GroupDictionary 读取以模拟跨图复制丢失 Group）
                    var xdataOnlyOpts = PipeSpatialMatchOptions.FromConfig();
                    xdataOnlyOpts.UseExistingCadGroups = false;
                    xdataOnlyOpts.UseXDataLinks = true;

                    PipeSpatialMatchSummary xdataSummary = matcher.MatchDatabaseBlockAttributes(
                        editDb,
                        prevPipeLayers,
                        prevAttrLayers,
                        xdataOnlyOpts);

                    Logger.Info(
                        $"  [XData 恢复验证] 禁用 GroupDictionary 时，通过 XData ({PipeSpatialMatcher.XDataRegAppName}) 直接恢复配对={xdataSummary.XDataMatchedCount} 组");
                    AssertTrue(xdataSummary.XDataMatchedCount == 317,
                        $"模拟 GroupDictionary 丢失场景下，通过 XData (PIPE_CHECK_LINK) 100% 恢复全部 317 组配对 (实际={xdataSummary.XDataMatchedCount})");

                    // 4.4 验证 Undo() 回滚：擦除 317 个新建 Group、清除 PIPE_CHECK_LINK XData 并恢复 ToRecheck 状态
                    undoRedoMgr.Undo();
                    int toRecheckAfterUndo = workingIssues.Values.Count(i => i.Status == IssueReviewStatus.ToRecheck);
                    PipeSpatialMatchSummary afterUndoSummary = matcher.MatchDatabaseBlockAttributes(
                        editDb,
                        prevPipeLayers,
                        prevAttrLayers,
                        PipeSpatialMatchOptions.FromConfig());

                    Logger.Info(
                        $"  [Undo 回滚验证] Undo 后有效 Group 数={afterUndoSummary.ValidGroupPairCount}, XData 匹配数={afterUndoSummary.XDataMatchedCount}, 恢复 ToRecheck 记录={toRecheckAfterUndo}");
                    AssertTrue(afterUndoSummary.ValidGroupPairCount == 0 && afterUndoSummary.XDataMatchedCount == 0 && toRecheckAfterUndo == 317,
                        $"BindPipeGroupOperation.Undo 完整擦除 317 个 Group、清空 XData 并还原 317 条记录为 ToRecheck 状态");

                    // 4.5 验证 Redo() 重做：重新创建 317 个 Group 与 XData 并恢复 Resolved 状态
                    undoRedoMgr.Redo();
                    int resolvedAfterRedo = workingIssues.Values.Count(i => i.Status == IssueReviewStatus.Resolved);
                    PipeSpatialMatchSummary afterRedoSummary = matcher.MatchDatabaseBlockAttributes(
                        editDb,
                        prevPipeLayers,
                        prevAttrLayers,
                        PipeSpatialMatchOptions.FromConfig());

                    Logger.Info(
                        $"  [Redo 重做验证] Redo 后有效 Group 数={afterRedoSummary.ValidGroupPairCount}, Resolved 记录={resolvedAfterRedo}");
                    AssertTrue(afterRedoSummary.ValidGroupPairCount == 317 && resolvedAfterRedo == 317,
                        $"BindPipeGroupOperation.Redo 成功重建 317 个 1:1 Group 与 XData 并恢复 Resolved 状态");
                }
            }

            // ====================================================================
            // 场景 5：复现与验证 MdiActiveDocument (AppTransaction) + PaletteRefreshManager 实时监听下
            //         MainCheckWindowViewModel 多行选中批量执行 BindSelectedPipeGroups() 不崩溃且支持 Undo/Redo
            // ====================================================================
            Logger.Info("--- 场景 5：验证 MdiActiveDocument (AppTransaction) 激活监听器下多行批量绑定 Group 与 XData ---");
            if (Doc != null && Doc.Database != null)
            {
                var prevPipeLayers = new[]
                {
                    "0污水-待补测现状管",
                    "0污水-规划新建",
                    "0污水-规划改建",
                    "0污水-已核实需新增"
                };
                var prevAttrLayers = new[] { "0污水-属性" };

                var activeMatchSvc = new MatchGroupService(Doc.Database, Doc.Editor);
                var session = DocumentSessionManager.GetOrCreateSession(Doc.Database);
                session?.RegisterActiveRule(activeMatchSvc);

                var refreshMgr = new process_pipeline.Utils.PaletteRefreshManager();
                try
                {
                    refreshMgr.StartListening(Doc);

                    Dictionary<ObjectId, CheckIssueItem> activeIssues = activeMatchSvc.CheckAll();
                    if (activeIssues != null && activeIssues.Count > 0)
                    {
                        session?.MergeRuleIssues(MatchGroupService.DefaultRuleId, activeIssues, isIncremental: false);

                        var vm = new MainCheckWindowViewModel();
                        vm.UpdateFromIssues(activeIssues, isIncremental: false);

                        // 5.0 验证点击任意行（含 6B/6F/70 以外的 71、A1、B8、CE 等直管/折管行）均能按方案B生成轻量瞬态高亮（洋红管线 + 洋红属性块矩形外框 + 红色连线/锚圈）
                        var row71 = vm.Rows.FirstOrDefault(r => string.Equals(r.Id, "71", StringComparison.OrdinalIgnoreCase));
                        var rowB8 = vm.Rows.FirstOrDefault(r => string.Equals(r.Id, "B8", StringComparison.OrdinalIgnoreCase));
                        vm.SetSelectedRows(new[] { row71 }, syncCadSelection: true);
                        int transientsFor71 = process_pipeline.Utils.GraphicManager.ActiveTransientCount;
                        vm.SetSelectedRows(new[] { rowB8 }, syncCadSelection: true);
                        int transientsForB8 = process_pipeline.Utils.GraphicManager.ActiveTransientCount;
                        vm.SetSelectedRows(vm.Rows, syncCadSelection: true);
                        int transientsForAll317 = process_pipeline.Utils.GraphicManager.ActiveTransientCount;
                        AssertTrue(row71 != null && rowB8 != null && transientsFor71 >= 6 && transientsForB8 >= 6 && transientsForAll317 >= 317 * 6,
                            $"点击单行及大批量框选全部 317 行均完整生成管线(洋红)+属性块矩形外框(洋红)+辅助连线/锚圈(红)瞬态高亮 (71={transientsFor71}, B8={transientsForB8}, 全选317行={transientsForAll317})");

                        // 5.0.1 验证各状态节点下右键菜单状态切换按钮的上下文感知显隐，以及标记为“已忽略”后的 Undo / Redo 完整闭环
                        var statusTestRuleNode = vm.TreeNodes.FirstOrDefault(n => n.FilterKind == TreeFilterKind.RuleCategory);
                        var statusTestRecheckNode = statusTestRuleNode?.Children.FirstOrDefault(c => c.StatusFilter == IssueReviewStatus.ToRecheck);
                        var statusTestIgnoredNode = statusTestRuleNode?.Children.FirstOrDefault(c => c.StatusFilter == IssueReviewStatus.Ignored);

                        vm.SelectedTreeNode = statusTestRecheckNode;
                        vm.SetSelectedRows(new[] { row71 }, syncCadSelection: false);
                        AssertTrue(
                            vm.MarkToRecheckMenuVisibility == System.Windows.Visibility.Collapsed &&
                            vm.MarkPendingMenuVisibility == System.Windows.Visibility.Visible &&
                            vm.MarkIgnoredMenuVisibility == System.Windows.Visibility.Visible &&
                            vm.MarkResolvedMenuVisibility == System.Windows.Visibility.Visible,
                            "在【待复核】节点下右键菜单自动隐藏‘标记为【待复核】’，仅展示【待核查】【已忽略】【已修复】三个可切换状态");

                        vm.MarkIgnoredCommand.Execute(null);
                        AssertTrue(
                            row71.SourceIssue.Status == IssueReviewStatus.Ignored &&
                            !vm.GetVisibleRows().Contains(row71) &&
                            vm.UndoCommand.CanExecute(null),
                            "将 71 标记为【已忽略】后，该行立即从【待复核】节点移出，且 UndoCommand 处于激活可用状态");

                        vm.SelectedTreeNode = statusTestIgnoredNode;
                        vm.SetSelectedRows(new[] { row71 }, syncCadSelection: false);
                        AssertTrue(
                            vm.GetVisibleRows().Contains(row71) &&
                            vm.MarkIgnoredMenuVisibility == System.Windows.Visibility.Collapsed &&
                            vm.MarkPendingMenuVisibility == System.Windows.Visibility.Visible &&
                            vm.MarkToRecheckMenuVisibility == System.Windows.Visibility.Visible &&
                            vm.MarkResolvedMenuVisibility == System.Windows.Visibility.Visible,
                            "切换到【已忽略】节点后可看到 71，且右键菜单自动隐藏‘标记为【已忽略】’，仅展示可切换的其他状态");

                        vm.UndoCommand.Execute(null);
                        AssertTrue(
                            row71.SourceIssue.Status == IssueReviewStatus.ToRecheck &&
                            !vm.GetVisibleRows().Contains(row71) &&
                            vm.RedoCommand.CanExecute(null),
                            "在【已忽略】节点下执行 Undo 撤销后，71 状态立即恢复为【待复核】并从【已忽略】节点移出，RedoCommand 激活可用");

                        vm.RedoCommand.Execute(null);
                        AssertTrue(
                            row71.SourceIssue.Status == IssueReviewStatus.Ignored &&
                            vm.GetVisibleRows().Contains(row71),
                            "执行 Redo 重做后，71 状态再次变为【已忽略】并重新出现在【已忽略】节点中");

                        vm.UndoCommand.Execute(null);
                        vm.SelectedTreeNode = statusTestRuleNode;
                        AssertTrue(
                            row71.SourceIssue.Status == IssueReviewStatus.ToRecheck &&
                            vm.GetVisibleRows().Count == 317,
                            "再次 Undo 撤销并切回规则根节点后，全部 317 条记录完整恢复初始【待复核】状态");

                        // 5.1 先模拟用户在表格中多选前 25 行，执行右键“一键修复：批量绑定图属 Group 与 XData”
                        var firstBatchRows = vm.Rows.Take(25).ToList();
                        vm.SetSelectedRows(firstBatchRows, syncCadSelection: true);
                        AssertTrue(vm.CanBindSelectedGroup() && vm.GetBindableGroupTargetRows().Count == 25,
                            $"主面板多选 25 行时 CanBindSelectedGroup 为 true 且待绑定行数为 25 (实际={vm.GetBindableGroupTargetRows().Count})");

                        vm.BindSelectedPipeGroups();

                        PipeSpatialMatchSummary afterBatch25 = matcher.MatchDatabaseBlockAttributes(
                            Doc.Database,
                            prevPipeLayers,
                            prevAttrLayers,
                            PipeSpatialMatchOptions.FromConfig());

                        int resolvedCount25 = vm.Rows.Count(r => r.SourceIssue?.Status == IssueReviewStatus.Resolved);
                        Logger.Info(
                            $"  [AppTransaction 批量绑定 25 行] 有效 Group 数={afterBatch25.ValidGroupPairCount}, 面板已修复行数={resolvedCount25}");
                        AssertTrue(afterBatch25.ValidGroupPairCount == 25 && resolvedCount25 == 25,
                            $"MdiActiveDocument (AppTransaction) 下多选 25 行批量绑定成功，0 异常崩溃 (Group={afterBatch25.ValidGroupPairCount}, Resolved={resolvedCount25})");

                        // 5.2 继续多选剩余全部 292 行执行批量绑定
                        var remainingRows = vm.Rows.Where(r => r.SourceIssue?.Status != IssueReviewStatus.Resolved).ToList();
                        vm.SetSelectedRows(remainingRows, syncCadSelection: true);
                        vm.BindSelectedPipeGroups();

                        PipeSpatialMatchSummary afterAll317 = matcher.MatchDatabaseBlockAttributes(
                            Doc.Database,
                            prevPipeLayers,
                            prevAttrLayers,
                            PipeSpatialMatchOptions.FromConfig());

                        int resolvedCountAll = vm.Rows.Count(r => r.SourceIssue?.Status == IssueReviewStatus.Resolved);
                        AssertTrue(afterAll317.ValidGroupPairCount == 317 && resolvedCountAll == 317,
                            $"MdiActiveDocument (AppTransaction) 下继续多选剩余 292 行批量绑定成功，累计 317 组全部成组 (Group={afterAll317.ValidGroupPairCount})");

                        // 检查 AppTransaction 绑定后，管线与属性块的 PersistentReactorIds 是否包含 Group ObjectId，以及 PICKSTYLE 值
                        int pickStyleVal = Convert.ToInt32(Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("PICKSTYLE"));
                        int pipeWithGroupReactorCount = 0;
                        int attWithGroupReactorCount = 0;
                        int selectableGroupCount = 0;
                        using (OpenCloseTransaction trCheck = Doc.Database.TransactionManager.StartOpenCloseTransaction())
                        {
                            DBDictionary gDict = (DBDictionary)trCheck.GetObject(Doc.Database.GroupDictionaryId, OpenMode.ForRead);
                            foreach (DBDictionaryEntry entry in gDict)
                            {
                                Group g = trCheck.GetObject(entry.Value, OpenMode.ForRead) as Group;
                                if (g == null || g.IsErased) continue;
                                if (g.Selectable) selectableGroupCount++;
                                ObjectId[] mids = g.GetAllEntityIds();
                                if (mids != null && mids.Length == 2)
                                {
                                    DBObject e0 = trCheck.GetObject(mids[0], OpenMode.ForRead);
                                    DBObject e1 = trCheck.GetObject(mids[1], OpenMode.ForRead);
                                    var r0 = e0?.GetPersistentReactorIds();
                                    var r1 = e1?.GetPersistentReactorIds();
                                    if (r0 != null && r0.Contains(entry.Value)) pipeWithGroupReactorCount++;
                                    if (r1 != null && r1.Contains(entry.Value)) attWithGroupReactorCount++;
                                }
                            }
                        }
                        Logger.Info($"  [Group 反应器诊断] PICKSTYLE={pickStyleVal}, SelectableGroup={selectableGroupCount}, PipeReactorOK={pipeWithGroupReactorCount}/317, AttReactorOK={attWithGroupReactorCount}/317");
                        AssertTrue((pickStyleVal & 1) != 0 && selectableGroupCount == 317 && pipeWithGroupReactorCount == 317 && attWithGroupReactorCount == 317,
                            $"全部 317 组管线与属性块均已挂载 Group 持久反应器且已自动开启 PICKSTYLE 编组选择 (PICKSTYLE={pickStyleVal}, PipeReactor={pipeWithGroupReactorCount}, AttReactor={attWithGroupReactorCount})");

                        // 5.3 在 AppTransaction 环境下连续执行两次 Undo() 并验证 Redo()
                        vm.ExecuteUndo();
                        PipeSpatialMatchSummary afterUndo1 = matcher.MatchDatabaseBlockAttributes(
                            Doc.Database,
                            prevPipeLayers,
                            prevAttrLayers,
                            PipeSpatialMatchOptions.FromConfig());
                        AssertTrue(afterUndo1.ValidGroupPairCount == 25,
                            $"AppTransaction 环境下第一次 Undo 成功回滚 292 组，剩余 25 组 (实际={afterUndo1.ValidGroupPairCount})");

                        vm.ExecuteRedo();
                        PipeSpatialMatchSummary afterRedo1 = matcher.MatchDatabaseBlockAttributes(
                            Doc.Database,
                            prevPipeLayers,
                            prevAttrLayers,
                            PipeSpatialMatchOptions.FromConfig());
                        AssertTrue(afterRedo1.ValidGroupPairCount == 317,
                            $"AppTransaction 环境下 Redo 成功重新绑定 292 组，恢复 317 组 (实际={afterRedo1.ValidGroupPairCount})");

                        // 还原宿主文档状态
                        vm.ExecuteUndo();
                        vm.ExecuteUndo();

                        // 5.4 验证 Commit 7.4：点选管线或点选配对 ws 属性块（RelatedObjectIds / PossibleMatches）均能反向定位到对应行
                        int pipeLookupHitCount = 0;
                        int attLookupHitCount = 0;
                        foreach (PipeRowVm row in vm.Rows)
                        {
                            var byPipe = vm.FindRowsByObjectIds(new[] { row.TargetObjectId }, applyFilter: false);
                            if (byPipe.Contains(row))
                                pipeLookupHitCount++;

                            if (row.SourceIssue?.RelatedObjectIds != null && row.SourceIssue.RelatedObjectIds.Count > 0)
                            {
                                ObjectId attId = row.SourceIssue.RelatedObjectIds[0];
                                var byAtt = vm.FindRowsByObjectIds(new[] { attId }, applyFilter: false);
                                if (byAtt.Contains(row))
                                    attLookupHitCount++;
                            }
                        }
                        AssertTrue(pipeLookupHitCount == 317 && attLookupHitCount == 317,
                            $"Commit 7.4 图面双向反查索引验证通过：点选管线命中={pipeLookupHitCount}/317，点选 ws 属性块反向命中={attLookupHitCount}/317");

                        // 5.5 验证 Commit 7.5：仿 ArcGIS 10.5 属性表底部“[显示所有行] / [仅显示选中行]”双模式切换过滤
                        AssertTrue(vm.IsShowAllRows && !vm.IsShowSelectedOnly && vm.GetVisibleRows().Count == 317,
                            $"Commit 7.5 初始默认为【显示所有行】模式，可见行数=317 (实际={vm.GetVisibleRows().Count})");

                        var sampleSelected5 = vm.Rows.Take(5).ToList();
                        vm.SetSelectedRows(sampleSelected5, syncCadSelection: false);
                        vm.ShowSelectedRowsCommand.Execute(null);

                        int visibleInSelectedMode = vm.GetVisibleRows().Count;
                        string summarySelectedMode = vm.SelectionRecordSummaryText;
                        AssertTrue(vm.IsShowSelectedOnly && !vm.IsShowAllRows && visibleInSelectedMode == 5,
                            $"Commit 7.5 点击【仅显示选中行】后仅过滤展示选中的 5 行 (实际={visibleInSelectedMode}, 摘要='{summarySelectedMode}')");

                        vm.ShowAllRowsCommand.Execute(null);
                        int visibleInAllMode = vm.GetVisibleRows().Count;
                        string summaryAllMode = vm.SelectionRecordSummaryText;
                        AssertTrue(vm.IsShowAllRows && !vm.IsShowSelectedOnly && visibleInAllMode == 317 && summaryAllMode.Contains("已选中 5"),
                            $"Commit 7.5 点击【显示所有行】后恢复全部 317 行展示且保留选中状态 (实际={visibleInAllMode}, 摘要='{summaryAllMode}')");

                        // 5.6 验证 Commit 7.6：实例化真实 ucMainCheckWindow (验证 XAML InitializeComponent 解析无异常) 及左侧 TreeView 与右侧 DataGrid 跨节点联动
                        var realWin = new ucMainCheckWindow();
                        AssertTrue(realWin != null && realWin.DataContext is MainCheckWindowViewModel,
                            "ucMainCheckWindow XAML InitializeComponent() 实例化成功，0 XamlParseException 异常");

                        var testGrid = new System.Windows.Controls.DataGrid
                        {
                            SelectionMode = System.Windows.Controls.DataGridSelectionMode.Extended,
                            SelectionUnit = System.Windows.Controls.DataGridSelectionUnit.FullRow,
                            ItemsSource = vm.Rows
                        };
                        testGrid.SelectionChanged += (s, ev) => vm.SyncSelectedRowsFromGrid(syncCadSelection: false);
                        vm.AttachGrid(testGrid);

                        var ruleRootNode = vm.TreeNodes.FirstOrDefault(n => n.FilterKind == TreeFilterKind.RuleCategory);
                        var pendingChildNode = ruleRootNode?.Children.FirstOrDefault(c => c.StatusFilter == IssueReviewStatus.Pending);
                        var toRecheckChildNode = ruleRootNode?.Children.FirstOrDefault(c => c.StatusFilter == IssueReviewStatus.ToRecheck);
                        var ignoredChildNode = ruleRootNode?.Children.FirstOrDefault(c => c.StatusFilter == IssueReviewStatus.Ignored);

                        // 场景 A1：图面同时选中“一对匹配的管线和属性块”（Rows[0] 的管线 + Rows[0] 的配对属性块）-> 归并为 1 组（列表中仅 1 行被选中）
                        var singlePairSelected = vm.ApplyCadObjectIdSelection(new[]
                        {
                            vm.Rows[0].TargetObjectId,
                            vm.Rows[0].SourceIssue.RelatedObjectIds[0]
                        });
                        AssertTrue(singlePairSelected.Count == 1 && testGrid.SelectedItems.Count == 1 && vm.StatusText.Contains("1 组配对图属"),
                            $"Commit 7.6 图面同时选中一对匹配的管线与属性块时归并为 1 组，DataGrid 选中行数={testGrid.SelectedItems.Count} (状态栏='{vm.StatusText}')");

                        // 场景 A2：图面框选一堆图形与属性块（包含 2 组配对成功的 [管线+属性块]、1 根独立管线、1 个独立属性块，共 6 个 CAD 图元）-> 准确识别出 4 行并在 DataGrid 中全部保持选中
                        var mixedMultiSelected = vm.ApplyCadObjectIdSelection(new[]
                        {
                            vm.Rows[0].TargetObjectId,
                            vm.Rows[0].SourceIssue.RelatedObjectIds[0], // 配对组 #1 -> Rows[0]
                            vm.Rows[1].TargetObjectId,
                            vm.Rows[1].SourceIssue.RelatedObjectIds[0], // 配对组 #2 -> Rows[1]
                            vm.Rows[2].TargetObjectId,                  // 独立管线 -> Rows[2]
                            vm.Rows[3].SourceIssue.RelatedObjectIds[0]  // 独立属性块 -> Rows[3]
                        });
                        AssertTrue(mixedMultiSelected.Count == 4 && testGrid.SelectedItems.Count == 4 && vm.StatusText.Contains("2 组配对图属"),
                            $"Commit 7.6 图面混合多选（2组配对图属 + 1根独立管线 + 1个独立属性块 = 6图元）时，DataGrid 完整多选 4 行 (实际={testGrid.SelectedItems.Count}, 状态栏='{vm.StatusText}')");

                        // 场景 A3：左侧树停留在“待核查 (0)”节点时，在 CAD 图面点选属于“待复核”的管线与另一组的 ws 属性块 -> 自动切换树节点至“待复核”且 DataGrid 选中 2 行
                        vm.SelectedTreeNode = pendingChildNode;
                        AssertTrue(vm.GetVisibleRows().Count == 0,
                            $"Commit 7.6 切换至【待核查(0)】子节点时初始可见行数为 0 (实际={vm.GetVisibleRows().Count})");

                        var crossNodeSelected = vm.ApplyCadObjectIdSelection(new[]
                        {
                            vm.Rows[0].TargetObjectId,
                            vm.Rows[1].SourceIssue.RelatedObjectIds[0]
                        });
                        AssertTrue(ReferenceEquals(vm.SelectedTreeNode, toRecheckChildNode) && crossNodeSelected.Count == 2 && testGrid.SelectedItems.Count == 2 && vm.GetVisibleRows().Count == 317,
                            $"Commit 7.6 跨节点点选管线与非同组属性块时，左侧树自动从【待核查】切换至【待复核】且 DataGrid 完整选中 2 行 (当前节点={vm.SelectedTreeNode?.Name}, DataGrid选中={testGrid.SelectedItems.Count})");

                        // 场景 B：图面多选跨越同一规则下不同状态（如“已忽略”+“待复核”）时，左侧树自动上卷至一级父节点【图属关系与Group核查】且 DataGrid 选中 2 行
                        vm.SetSelectedRows(new[] { vm.Rows[0] }, syncCadSelection: false);
                        vm.ChangeSelectedRowStatus(IssueReviewStatus.Ignored);
                        vm.SelectedTreeNode = toRecheckChildNode;

                        var multiStatusSelected = vm.ApplyCadObjectIdSelection(new[]
                        {
                            vm.Rows[0].TargetObjectId,
                            vm.Rows[1].TargetObjectId
                        });
                        AssertTrue(ReferenceEquals(vm.SelectedTreeNode, ruleRootNode) && multiStatusSelected.Count == 2 && testGrid.SelectedItems.Count == 2,
                            $"Commit 7.6 图面多选跨【已忽略+待复核】子状态时，左侧树自动上卷至一级规则父节点 [{vm.SelectedTreeNode?.Name}] 且 DataGrid 完整选中 2 行 (DataGrid选中={testGrid.SelectedItems.Count})");

                        vm.ExecuteUndo(); // 还原 Rows[0] 为 ToRecheck

                        // 场景 C：在【仅显示选中行】模式下跨左侧不同根节点/子节点切换时，选中状态始终 100% 保持（仿 ArcGIS 属性表）
                        var layerRootNode = vm.TreeNodes.FirstOrDefault(n => n.FilterKind == TreeFilterKind.LayerRoot);
                        testGrid.SelectionChanged += (s, ev) => vm.OnGridSelectionChanged(ev);

                        vm.SelectedTreeNode = ruleRootNode;
                        vm.SetSelectedRows(vm.Rows.Take(5), syncCadSelection: false);
                        vm.ShowSelectedRowsCommand.Execute(null);
                        AssertTrue(vm.IsShowSelectedOnly && vm.GetVisibleRows().Count == 5 && testGrid.SelectedItems.Count == 5 && vm.SelectedRows.Count == 5,
                            $"Commit 7.6 进入【仅显示选中行】模式，当前显示 5 行且 DataGrid 保持 5 行全选中 (DataGrid选中={testGrid.SelectedItems.Count})");

                        // 从【图属关系与Group核查】根节点切换至【图层】根节点 -> 5 行应依然保持选中状态
                        vm.SelectedTreeNode = layerRootNode;
                        AssertTrue(vm.IsShowSelectedOnly && vm.GetVisibleRows().Count == 5 && testGrid.SelectedItems.Count == 5 && vm.SelectedRows.Count == 5,
                            $"Commit 7.6 在【仅显示选中行】模式下切换至【图层】根节点后，5 行依然保持全选中状态 (DataGrid选中={testGrid.SelectedItems.Count}, VM选中={vm.SelectedRows.Count})");

                        // 从【图层】根节点切换至【待复核】子节点 -> 5 行应依然保持选中状态
                        vm.SelectedTreeNode = toRecheckChildNode;
                        AssertTrue(vm.IsShowSelectedOnly && vm.GetVisibleRows().Count == 5 && testGrid.SelectedItems.Count == 5 && vm.SelectedRows.Count == 5,
                            $"Commit 7.6 在【仅显示选中行】模式下切换至【待复核】子节点后，5 行依然保持全选中状态 (DataGrid选中={testGrid.SelectedItems.Count}, VM选中={vm.SelectedRows.Count})");

                        // 再次切换回【图属关系与Group核查】根节点 -> 5 行依然保持选中状态
                        vm.SelectedTreeNode = ruleRootNode;
                        AssertTrue(vm.IsShowSelectedOnly && vm.GetVisibleRows().Count == 5 && testGrid.SelectedItems.Count == 5 && vm.SelectedRows.Count == 5,
                            $"Commit 7.6 在【仅显示选中行】模式下切回【图属关系与Group核查】根节点后，5 行依然保持全选中状态 (DataGrid选中={testGrid.SelectedItems.Count}, VM选中={vm.SelectedRows.Count})");

                        // 场景 D：在【仅显示选中行】模式下主动点击无交集的树节点（如“待核查 (0)”），保持【仅显示选中行】模式不变且右侧直接显示空行（0 行）；再切回有交集节点时，原 5 行恢复显示且保持选中状态
                        vm.SelectedTreeNode = pendingChildNode;
                        AssertTrue(vm.IsShowSelectedOnly && !vm.IsShowAllRows && vm.GetVisibleRows().Count == 0 && testGrid.SelectedItems.Count == 0,
                            $"Commit 7.6 在【仅显示选中行】模式下点击无交集的【待核查】树节点时，保持【仅显示选中行】模式不变且右侧显示 0 行 (可见={vm.GetVisibleRows().Count})");

                        vm.SelectedTreeNode = toRecheckChildNode;
                        AssertTrue(vm.IsShowSelectedOnly && vm.GetVisibleRows().Count == 5 && testGrid.SelectedItems.Count == 5 && vm.SelectedRows.Count == 5,
                            $"Commit 7.6 从空节点再切回【待复核】节点时，仍处于【仅显示选中行】模式，恢复显示 5 行且原 5 行选中状态完整保留 (可见={vm.GetVisibleRows().Count}, DataGrid选中={testGrid.SelectedItems.Count})");

                        vm.ShowAllRowsCommand.Execute(null);
                        AssertTrue(vm.IsShowAllRows && !vm.IsShowSelectedOnly && vm.GetVisibleRows().Count == 317 && testGrid.SelectedItems.Count == 5,
                            $"Commit 7.6 手动点击【显示所有行】后恢复显示 317 行且 5 行保持选中状态 (可见={vm.GetVisibleRows().Count}, DataGrid选中={testGrid.SelectedItems.Count})");

                        // 5.7 验证 Commit 7.7：全窗体 SVG 矢量图标体系、双色调主题自适应画刷及悬停 ToolTip 校验
                        string svgDir = @"D:\Codes\Tools\process_pipeline\Themes\Svg";
                        var svgFiles = Directory.Exists(svgDir) ? Directory.GetFiles(svgDir, "*.svg") : Array.Empty<string>();
                        AssertTrue(svgFiles.Length >= 16 && svgFiles.All(f => File.ReadAllText(f).Contains("<svg")),
                            $"Commit 7.7 Themes/Svg 目录下已生成 {svgFiles.Length} 个标准 AutoCAD 2019 风格 .svg 矢量图标资源");

                        var dsWin = new DataSourceSettingsWindow(new DataSourceSettingsViewModel(Doc.Database));
                        var msgWin = new CadMessageDialog("测试图标与主题", "测试消息内容", DialogConfig.Get(process_pipeline.Utils.MessageBoxType.Info));
                        var arrowCtrl = new ucMatchArrowResult();
                        process_pipeline.Themes.CadThemes.ApplyCadTheme(realWin, "Dark");
                        var darkPrimary = realWin.TryFindResource("Brush.Icon.Primary") as System.Windows.Media.SolidColorBrush;
                        var darkAccent = realWin.TryFindResource("Brush.Icon.Accent") as System.Windows.Media.SolidColorBrush;
                        var iconTplFullCheck = realWin.TryFindResource("IconTemplate.FullCheck") as System.Windows.Controls.ControlTemplate;
                        var iconTplShowSelected = realWin.TryFindResource("IconTemplate.TableShowSelected") as System.Windows.Controls.ControlTemplate;
                        var realGrid = realWin.FindName("dgIssues") as System.Windows.Controls.DataGrid;
                        var darkNativeUndo = realWin.TryFindResource("NativeIcon.UndoAction") as System.Windows.Media.ImageSource;
                        var darkNativeSave = realWin.TryFindResource("NativeIcon.ConfirmSave") as System.Windows.Media.ImageSource;
                        var darkNativeRefresh = realWin.TryFindResource("NativeIcon.Refresh") as System.Windows.Media.ImageSource;
                        var darkMenuBg = realGrid?.ContextMenu?.TryFindResource("Brush.ContextMenu.Background") as System.Windows.Media.SolidColorBrush;
                        var darkMenuUndo = realGrid?.ContextMenu?.TryFindResource("NativeIcon.UndoAction") as System.Windows.Media.ImageSource;

                        process_pipeline.Themes.CadThemes.ApplyCadTheme(realWin, "Light");
                        var lightPrimary = realWin.TryFindResource("Brush.Icon.Primary") as System.Windows.Media.SolidColorBrush;
                        var lightAccent = realWin.TryFindResource("Brush.Icon.Accent") as System.Windows.Media.SolidColorBrush;
                        var lightNativeUndo = realWin.TryFindResource("NativeIcon.UndoAction") as System.Windows.Media.ImageSource;
                        var lightNativeSave = realWin.TryFindResource("NativeIcon.ConfirmSave") as System.Windows.Media.ImageSource;
                        var lightMenuBg = realGrid?.ContextMenu?.TryFindResource("Brush.ContextMenu.Background") as System.Windows.Media.SolidColorBrush;
                        var lightMenuUndo = realGrid?.ContextMenu?.TryFindResource("NativeIcon.UndoAction") as System.Windows.Media.ImageSource;
                        string lightSvgDir = @"D:\Codes\Tools\process_pipeline\Themes\Svg\Light";
                        var lightSvgFiles = Directory.Exists(lightSvgDir) ? Directory.GetFiles(lightSvgDir, "*.svg") : Array.Empty<string>();

                        AssertTrue(
                            dsWin != null && msgWin != null && arrowCtrl != null &&
                            iconTplFullCheck != null && iconTplShowSelected != null &&
                            darkPrimary != null && lightPrimary != null &&
                            darkPrimary.Color != lightPrimary.Color &&
                            darkAccent != null && lightAccent != null &&
                            !string.IsNullOrWhiteSpace(vm.DataSourceToolTipText) &&
                            !string.IsNullOrWhiteSpace(vm.SingleRuleToolTipText),
                            $"Commit 7.7 全窗体矢量图标模板与 Dark/Light 双主题动态配色验证通过 (DarkPrimary={darkPrimary?.Color}, LightPrimary={lightPrimary?.Color})");

                        var colRemark = realWin.FindName("colRemark") as System.Windows.Controls.DataGridColumn;
                        var realVm = realWin.DataContext as MainCheckWindowViewModel;
                        realWin.UpdateIssueItems(vm.Rows.Take(5).Select(r => r.SourceIssue), isIncremental: false);
                        realVm?.SetSelectedRows(realVm.Rows.Take(1), syncCadSelection: false);
                        var copyMatrix = DataGridCopyHelper.BuildMatrix(realGrid);
                        string copiedTsv = DataGridCopyHelper.ToTsv(copyMatrix, includeHeader: true);

                        AssertTrue(
                            svgFiles.Length == 25 && lightSvgFiles.Length == 25 &&
                            darkNativeUndo != null && Math.Abs(darkNativeUndo.Width - 16.0) < 0.01 && Math.Abs(darkNativeUndo.Height - 16.0) < 0.01 &&
                            darkNativeSave != null && Math.Abs(darkNativeSave.Width - 16.0) < 0.01 &&
                            darkNativeRefresh != null && Math.Abs(darkNativeRefresh.Width - 16.0) < 0.01 &&
                            lightNativeUndo != null && lightNativeSave != null &&
                            !ReferenceEquals(darkNativeUndo, lightNativeUndo) &&
                            darkMenuBg != null && lightMenuBg != null && darkMenuBg.Color != lightMenuBg.Color &&
                            darkMenuUndo != null && lightMenuUndo != null && !ReferenceEquals(darkMenuUndo, lightMenuUndo) &&
                            realGrid != null && !realGrid.EnableColumnVirtualization &&
                            colRemark != null && colRemark.Width.IsAuto && colRemark.MinWidth >= 280.0 &&
                            !copyMatrix.IsEmpty && copyMatrix.Columns.Count == 6 &&
                            !copiedTsv.Contains("PipeRowVm") &&
                            !vm.Rows[0].Remark.Contains("右键可一键") &&
                            !vm.Rows[0].Remark.Contains("点击可查看") &&
                            vm.Rows[0].RemarkToolTipText.Contains("【问题描述】") &&
                            vm.Rows[0].RemarkToolTipText.Contains("【解决方案】") &&
                            vm.Rows[0].LocationToolTipText.StartsWith("位置坐标"),
                            $"AutoCAD 2019 原生图标库、ContextMenu 双主题、DataGrid 横向滚动列宽自适应、问题描述与解决方案分离及精简版剪贴板复制矩阵验证通过 (Columns={copyMatrix.Columns.Count}, CopiedStatus='{copyMatrix.Rows.FirstOrDefault()?[2]}')");

                        // 5.8 验证下拉菜单规则切换（MatchArrow 阻断后切换 MatchGroup）与多行批量“一键修复：翻转”无 AccessViolationException
                        var docSession = DocumentSessionManager.GetOrCreateSession(Doc.Database);
                        docSession.ClearAll();

                        var flowArrowOpt = realVm.AvailableSingleRules.FirstOrDefault(r =>
                            string.Equals(r.RuleId, CheckPipelineCoordinator.FlowArrowRuleId, StringComparison.OrdinalIgnoreCase));
                        var matchGroupOpt = realVm.AvailableSingleRules.FirstOrDefault(r =>
                            string.Equals(r.RuleId, MatchGroupService.DefaultRuleId, StringComparison.OrdinalIgnoreCase));

                        realVm.SelectedSingleRule = flowArrowOpt;
                        var arrowBlockedIssues = realVm.ExecuteRunSingleCheck(Doc.Database, Doc.Editor, showPalette: false);
                        AssertTrue(
                            (arrowBlockedIssues == null || arrowBlockedIssues.Count == 0) &&
                            !docSession.ActiveRules.ContainsKey(CheckPipelineCoordinator.FlowArrowRuleId),
                            $"上版数据.dwg 执行下拉项 MatchArrow 被前置拦截且未污染会话 ActiveRules (Count={arrowBlockedIssues?.Count ?? 0})");

                        realVm.SelectedSingleRule = matchGroupOpt;
                        var matchGroupAfterSwitch = realVm.ExecuteRunSingleCheck(Doc.Database, Doc.Editor, showPalette: false);
                        AssertTrue(
                            matchGroupAfterSwitch != null && matchGroupAfterSwitch.Count == 317 &&
                            realVm.Rows.Count == 317 &&
                            realVm.GetVisibleRows().Count == 317 &&
                            string.Equals(realVm.SelectedTreeNode?.RuleNameFilter, MatchGroupService.DefaultRuleName, StringComparison.OrdinalIgnoreCase),
                            $"上版数据.dwg 从下拉项 MatchArrow 切换至 MatchGroup 后成功检出并展示 317 条记录，且左侧树自动聚焦 [{realVm.SelectedTreeNode?.Name}] (Rows={realVm.Rows.Count}, Visible={realVm.GetVisibleRows().Count})");

                        // 验证案例 1：在 上版数据.dwg 已跑出 317 条 MatchGroup 后，误选跨图规则 MatchPipe / CompareSegment 执行单项核查时，不清空既有会话与表格
                        var matchPipeOpt = realVm.AvailableSingleRules.FirstOrDefault(r =>
                            string.Equals(r.RuleId, MatchPipeService.DefaultRuleId, StringComparison.OrdinalIgnoreCase));
                        realVm.SelectedSingleRule = matchPipeOpt;
                        var afterBlockedMatchPipe = realVm.ExecuteRunSingleCheck(Doc.Database, Doc.Editor, showPalette: false);
                        AssertTrue(
                            afterBlockedMatchPipe != null && afterBlockedMatchPipe.Count == 317 &&
                            realVm.Rows.Count == 317 &&
                            docSession.IssuesById.Count == 317 &&
                            !docSession.ActiveRules.ContainsKey(MatchPipeService.DefaultRuleId),
                            $"上版数据.dwg 在已有 317 条 MatchGroup 结果后执行不满足前置条件的跨图规则 MatchPipe，既有 317 条核查结果完整保留且未污染 ActiveRules (Rows={realVm.Rows.Count})");

                        // 验证多行批量“一键修复：翻转”及 Undo/Redo（消除 ads_regen AccessViolationException）
                        ObjectId curveId0 = realVm.Rows[0].TargetObjectId;
                        ObjectId curveId1 = realVm.Rows[1].TargetObjectId;
                        Point3d origStart0, origEnd0, origStart1, origEnd1;
                        using (OpenCloseTransaction trOrig = Doc.Database.TransactionManager.StartOpenCloseTransaction())
                        {
                            var c0 = (Curve)trOrig.GetObject(curveId0, OpenMode.ForRead);
                            var c1 = (Curve)trOrig.GetObject(curveId1, OpenMode.ForRead);
                            origStart0 = c0.StartPoint;
                            origEnd0 = c0.EndPoint;
                            origStart1 = c1.StartPoint;
                            origEnd1 = c1.EndPoint;
                        }

                        var flowIssue0 = new CheckIssueItem
                        {
                            IssueId = $"FlowArrow:{curveId0.Handle}:0",
                            RuleId = CheckPipelineCoordinator.FlowArrowRuleId,
                            RuleName = "流向箭头核查",
                            TargetObjectId = curveId0,
                            TargetHandle = curveId0.Handle.ToString(),
                            LayerName = realVm.Rows[0].Type,
                            Status = IssueReviewStatus.Pending,
                            Remark = "流向与箭头相反",
                            Description = "流向与箭头相反"
                        };
                        var flowIssue1 = new CheckIssueItem
                        {
                            IssueId = $"FlowArrow:{curveId1.Handle}:0",
                            RuleId = CheckPipelineCoordinator.FlowArrowRuleId,
                            RuleName = "流向箭头核查",
                            TargetObjectId = curveId1,
                            TargetHandle = curveId1.Handle.ToString(),
                            LayerName = realVm.Rows[1].Type,
                            Status = IssueReviewStatus.Pending,
                            Remark = "流向与箭头相反",
                            Description = "流向与箭头相反"
                        };

                        realWin.UpdateIssueItems(new[] { flowIssue0, flowIssue1 }, isIncremental: false);
                        realVm.ActivateRuleInView(CheckPipelineCoordinator.FlowArrowRuleId);
                        realVm.SetSelectedRows(realVm.Rows.ToList(), syncCadSelection: false);
                        AssertTrue(realVm.CanReverseSelectedPipe() && realVm.SelectedRows.Count == 2,
                            $"选中 2 条流向异常管线时右键菜单【一键修复：翻转】可用 (CanReverse={realVm.CanReverseSelectedPipe()})");

                        realVm.ReverseSelectedPipe();

                        Point3d revStart0, revEnd0, revStart1, revEnd1;
                        using (OpenCloseTransaction trRev = Doc.Database.TransactionManager.StartOpenCloseTransaction())
                        {
                            var c0 = (Curve)trRev.GetObject(curveId0, OpenMode.ForRead);
                            var c1 = (Curve)trRev.GetObject(curveId1, OpenMode.ForRead);
                            revStart0 = c0.StartPoint;
                            revEnd0 = c0.EndPoint;
                            revStart1 = c1.StartPoint;
                            revEnd1 = c1.EndPoint;
                        }

                        AssertTrue(
                            origStart0.DistanceTo(revEnd0) < 1e-4 &&
                            origEnd0.DistanceTo(revStart0) < 1e-4 &&
                            origStart1.DistanceTo(revEnd1) < 1e-4 &&
                            origEnd1.DistanceTo(revStart1) < 1e-4 &&
                            realVm.Rows.All(r => r.SourceIssue.Status == IssueReviewStatus.Resolved),
                            $"批量【一键修复：翻转】成功翻转 2 条管线起终点并更新状态为已修复，无 AccessViolationException");

                        realVm.ExecuteUndo();
                        Point3d undoStart0, undoEnd0;
                        using (OpenCloseTransaction trUndo = Doc.Database.TransactionManager.StartOpenCloseTransaction())
                        {
                            var c0 = (Curve)trUndo.GetObject(curveId0, OpenMode.ForRead);
                            undoStart0 = c0.StartPoint;
                            undoEnd0 = c0.EndPoint;
                        }
                        AssertTrue(
                            origStart0.DistanceTo(undoStart0) < 1e-4 &&
                            origEnd0.DistanceTo(undoEnd0) < 1e-4 &&
                            realVm.Rows.All(r => r.SourceIssue.Status == IssueReviewStatus.Pending),
                            $"批量翻转后执行 Undo 成功恢复管线起终点与待核查状态");

                        // Commit 6：验证核查报表导出（未核查文档拦截提示 + 按左侧树核查类型分 Sheet 导出 .xlsx + 带 UTF-8 BOM 导出 .csv）
                        using (var uncheckedDb = new Database(true, true))
                        {
                            realVm.SwitchActiveDatabase(uncheckedDb);
                            bool uncheckedExported = realVm.ExecuteExportReport();
                            AssertTrue(
                                !uncheckedExported &&
                                realVm.StatusText.Contains("当前文档尚未执行核查") &&
                                realVm.StatusText.Contains("一键全量核查"),
                                $"未执行核查的文档点击【导出报表】不弹窗且状态栏给出引导提示 (StatusText='{realVm.StatusText}')");
                        }

                        realVm.SwitchActiveDatabase(Doc.Database);

                        // 构造包含两个左侧树【核查类型】根节点（图属匹配核查 + 流向箭头核查）的核查列表并测试导出
                        var matchIssueForExport = new CheckIssueItem
                        {
                            IssueId = $"MatchGroup:{curveId0.Handle}:0",
                            RuleId = MatchGroupService.DefaultRuleId,
                            RuleName = MatchGroupService.DefaultRuleName,
                            TargetObjectId = curveId0,
                            TargetHandle = curveId0.Handle.ToString(),
                            LayerName = flowIssue0.LayerName,
                            Status = IssueReviewStatus.Pending,
                            Remark = "管线与配对属性块尚未建立 CAD Group 编组",
                            Description = "管线与配对属性块尚未建立 CAD Group 编组"
                        };
                        realWin.UpdateIssueItems(new[] { matchIssueForExport, flowIssue0, flowIssue1 }, isIncremental: false);

                        string tempDir = Path.Combine(Path.GetTempPath(), "process_pipeline_export_test_" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(tempDir);
                        try
                        {
                            string tempXlsxPath = Path.Combine(tempDir, "核查报表测试.xlsx");
                            bool xlsxExported = realVm.ExecuteExportReport(tempXlsxPath);
                            AssertTrue(
                                xlsxExported && File.Exists(tempXlsxPath) && new FileInfo(tempXlsxPath).Length > 0,
                                $"成功导出多 Sheet Excel 报表 (.xlsx): {Path.GetFileName(tempXlsxPath)} (StatusText='{realVm.StatusText}')");

                            string workbookXmlContent;
                            string sheet1XmlContent;
                            string sheet2XmlContent;
                            using (Package pkg = Package.Open(tempXlsxPath, FileMode.Open, FileAccess.Read))
                            {
                                PackagePart wbPart = pkg.GetPart(new Uri("/xl/workbook.xml", UriKind.Relative));
                                using (var sr = new StreamReader(wbPart.GetStream(), Encoding.UTF8))
                                {
                                    workbookXmlContent = sr.ReadToEnd();
                                }

                                PackagePart s1Part = pkg.GetPart(new Uri("/xl/worksheets/sheet1.xml", UriKind.Relative));
                                using (var sr = new StreamReader(s1Part.GetStream(), Encoding.UTF8))
                                {
                                    sheet1XmlContent = sr.ReadToEnd();
                                }

                                PackagePart s2Part = pkg.GetPart(new Uri("/xl/worksheets/sheet2.xml", UriKind.Relative));
                                using (var sr = new StreamReader(s2Part.GetStream(), Encoding.UTF8))
                                {
                                    sheet2XmlContent = sr.ReadToEnd();
                                }
                            }

                            AssertTrue(
                                workbookXmlContent.Contains("name=\"图属匹配核查\"") &&
                                workbookXmlContent.Contains("name=\"流向箭头核查\"") &&
                                sheet1XmlContent.Contains("管线与配对属性块尚未建立 CAD Group 编组") &&
                                sheet2XmlContent.Contains("流向与箭头相反"),
                                "Excel (.xlsx) 工作簿按左侧树根节点准确划分为【图属匹配核查】与【流向箭头核查】两个 Sheet 且 UTF-8 中文内容完整无乱码");

                            string tempCsvPath = Path.Combine(tempDir, "核查报表测试.csv");
                            bool csvExported = realVm.ExecuteExportReport(tempCsvPath);
                            byte[] csvBytes = File.ReadAllBytes(tempCsvPath);
                            bool hasUtf8Bom = csvBytes.Length >= 3 &&
                                              csvBytes[0] == 0xEF &&
                                              csvBytes[1] == 0xBB &&
                                              csvBytes[2] == 0xBF;
                            string csvText = Encoding.UTF8.GetString(csvBytes);
                            AssertTrue(
                                csvExported &&
                                hasUtf8Bom &&
                                csvText.Contains("序号,核查类型,编号,核查状态,所属图层,位置坐标,问题描述 / 备注,推荐解决方案") &&
                                csvText.Contains("图属匹配核查") &&
                                csvText.Contains("流向箭头核查") &&
                                csvText.Contains("流向与箭头相反"),
                                $"CSV (.csv) 报表包含标准 UTF-8 BOM (EF BB BF) 且中文内容完整无乱码 (Bytes={csvBytes.Length})");
                        }
                        finally
                        {
                            try { Directory.Delete(tempDir, recursive: true); } catch { }
                        }
                    }
                }
                finally
                {
                    refreshMgr.StopListening(Doc);
                }
            }

            string finalSummary = $"[test_match_group] 测试完成：通过 {passedChecks} 项，失败 {failedChecks} 项。";
            if (failedChecks == 0)
            {
                Logger.Info($"========== {finalSummary} ==========");
            }
            else
            {
                Logger.Error($"========== {finalSummary} ==========");
            }
        }

        /// <summary>
        /// 复制上版数据图纸并在其中绘制每组（管线 <-> ws属性块）的醒目配对连线与定位标记，同时绑定 CAD Group，
        /// 保存为独立预览 DWG 供人工在 AutoCAD 中直观检查配对准确性。
        /// </summary>
        private static int GenerateVisualCheckDwg(
            string sourceDwgPath,
            string targetDwgPath,
            PipeSpatialMatchSummary summary)
        {
            if (File.Exists(targetDwgPath))
            {
                File.Delete(targetDwgPath);
            }
            File.Copy(sourceDwgPath, targetDwgPath, overwrite: true);

            int drawnCount = 0;

            using (var db = new Database(false, true))
            {
                db.ReadDwgFile(targetDwgPath, FileOpenMode.OpenForReadAndWriteNoShare, true, "");
                db.CloseInput(true);

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForWrite);

                    // 图层 1：黄色（ACI=2）—— 外包框中心命中（含大弯折 L/U 型管线，从 ws 块连向管线最近投影点）
                    ObjectId layerCenterHitId = EnsureCheckLayer(lt, tr, "00_核查配对线_外包框中心命中_黄", colorIndex: 2);

                    // 图层 2：亮绿色（ACI=3）—— 曲线邻近命中（从 ws 块连向管线最近投影点）
                    ObjectId layerCurveHitId = EnsureCheckLayer(lt, tr, "00_核查配对线_曲线邻近命中_绿", colorIndex: 3);

                    // 图层 3：青色（ACI=4）—— 近距离歧义候选辅助线
                    ObjectId layerConflictId = EnsureCheckLayer(lt, tr, "00_核查配对线_近距歧义候选_青", colorIndex: 4);

                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    DBDictionary groupDict = (DBDictionary)tr.GetObject(db.GroupDictionaryId, OpenMode.ForWrite);

                    var allMatches = summary.UniqueMatches.Concat(summary.ConflictMatches).ToList();
                    foreach (PipeAttachmentMatchItem item in allMatches)
                    {
                        if (item?.Pipe == null || item.MatchedAttachment == null)
                            continue;

                        ObjectId targetLayerId = item.Status == SpatialMatchStatus.OneToManyConflict
                            ? layerConflictId
                            : (item.Channel == SpatialMatchChannel.ExtentsCenterMatch ? layerCenterHitId : layerCurveHitId);

                        short targetColor = item.Status == SpatialMatchStatus.OneToManyConflict
                            ? (short)4
                            : (item.Channel == SpatialMatchChannel.ExtentsCenterMatch ? (short)2 : (short)3);

                        Point3d blockPt = item.MatchedAttachment.Position;
                        Point3d pipePt = item.ClosestPointOnPipe;

                        // 若 ws 块本身已经紧贴在直管段上（DistanceToCurve < 0.3m），则除了在块位置画小圆圈外，
                        // 再从块位置向管线起点和终点方向各拉一条短指示线（或连向管线端点），确保直管段在图面上也能一眼看出配对关系
                        var createdIds = new ObjectIdCollection();

                        if (db.TryGetObjectId(new Handle(Convert.ToInt64(item.Pipe.Handle, 16)), out ObjectId pipeId) && !pipeId.IsNull)
                        {
                            createdIds.Add(pipeId);
                        }
                        if (db.TryGetObjectId(new Handle(Convert.ToInt64(item.MatchedAttachment.Handle, 16)), out ObjectId attId) && !attId.IsNull)
                        {
                            createdIds.Add(attId);
                        }

                        if (item.DistanceToCurve >= 0.05)
                        {
                            // 绘制从 ws 块插入点指向所配对管线最近点的醒目连线
                            var linkLine = new Line(blockPt, pipePt)
                            {
                                LayerId = targetLayerId,
                                ColorIndex = targetColor,
                                LineWeight = LineWeight.LineWeight030
                            };
                            ObjectId lineId = ms.AppendEntity(linkLine);
                            tr.AddNewlyCreatedDBObject(linkLine, true);
                            createdIds.Add(lineId);

                            // 在管线落点处绘制一个小圆圈（半径 0.8m），清晰标示连线锚定在哪根管线上
                            var anchorCircle = new Circle(pipePt, Vector3d.ZAxis, 0.8)
                            {
                                LayerId = targetLayerId,
                                ColorIndex = targetColor
                            };
                            ObjectId circleId = ms.AppendEntity(anchorCircle);
                            tr.AddNewlyCreatedDBObject(anchorCircle, true);
                            createdIds.Add(circleId);
                        }
                        else
                        {
                            // 当 ws 块恰好落在直管段本体上（距离 < 0.05m）时：
                            // 在块与管线重合点处绘制一个半径 1.2m 的醒目圆圈，并向该管线首尾端各拉一条指向线到 1/4 处，方便人工识别它绑定的是哪条直管
                            var onPipeCircle = new Circle(blockPt, Vector3d.ZAxis, 1.2)
                            {
                                LayerId = targetLayerId,
                                ColorIndex = targetColor,
                                LineWeight = LineWeight.LineWeight030
                            };
                            ObjectId circleId = ms.AppendEntity(onPipeCircle);
                            tr.AddNewlyCreatedDBObject(onPipeCircle, true);
                            createdIds.Add(circleId);

                            if (item.Pipe.Geometry != null && item.Pipe.Geometry.NumPoints >= 2)
                            {
                                var coords = item.Pipe.Geometry.Coordinates;
                                Point3d startPt = new Point3d(coords[0].X, coords[0].Y, 0);
                                Point3d quarterPt = new Point3d(
                                    blockPt.X + (startPt.X - blockPt.X) * 0.35,
                                    blockPt.Y + (startPt.Y - blockPt.Y) * 0.35,
                                    0);
                                if (blockPt.DistanceTo(quarterPt) > 0.05)
                                {
                                    var dirLine = new Line(blockPt, quarterPt)
                                    {
                                        LayerId = targetLayerId,
                                        ColorIndex = targetColor,
                                        LineWeight = LineWeight.LineWeight030
                                    };
                                    ObjectId dirLineId = ms.AppendEntity(dirLine);
                                    tr.AddNewlyCreatedDBObject(dirLine, true);
                                    createdIds.Add(dirLineId);
                                }
                            }
                        }

                        // 同时将（管线 + ws块 + 连线）绑定为 CAD Group，在 CAD 中开启编组选择(Ctrl+Shift+A)时点选任一对象即可整组高亮
                        string grpName = $"MATCH_{item.Pipe.Handle}_{item.MatchedAttachment.Handle}";
                        if (!groupDict.Contains(grpName) && createdIds.Count >= 2)
                        {
                            var grp = new Group($"管线[{item.Pipe.Handle}]-属性块[{item.MatchedAttachment.Handle}]", true);
                            groupDict.SetAt(grpName, grp);
                            tr.AddNewlyCreatedDBObject(grp, true);
                            grp.Append(createdIds);
                        }

                        drawnCount++;
                    }

                    tr.Commit();
                }

                db.SaveAs(targetDwgPath, DwgVersion.Current);
            }

            Logger.Info($"  [可视化输出] 已保存配对连线检查图纸: {targetDwgPath} (共 {drawnCount} 组连线与编组)");
            return drawnCount;
        }

        private static ObjectId EnsureCheckLayer(LayerTable lt, Transaction tr, string layerName, short colorIndex)
        {
            if (lt.Has(layerName))
                return lt[layerName];

            var ltr = new LayerTableRecord
            {
                Name = layerName,
                Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex),
                IsLocked = false,
                IsOff = false,
                IsFrozen = false
            };
            ObjectId id = lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
            return id;
        }
    }
}
