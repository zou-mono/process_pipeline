using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using NetTopologySuite.Geometries;
using NLog;
using process_pipeline.Commands;
using process_pipeline.Core;
using process_pipeline.Core.DataSource;
using process_pipeline.Core.PreCheck;
using process_pipeline.Geometry;
using process_pipeline.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Cad.Plugin
{
    /// <summary>
    /// 阶段六 Commit 6.2：规划数据与可编辑上版数据同源覆盖、19 项业务属性对比及双向编辑增量感知无 UI 自动化测试。
    /// 命令：test_compare_segment
    /// </summary>
    public class Test_Compare_Segment : CadCommandBase
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private int _passCount;
        private int _failCount;

        [CommandMethod("test_compare_segment")]
        public override void Execute()
        {
            _passCount = 0;
            _failCount = 0;

            Logger.Info("========== 开始执行 [test_compare_segment] 规划与可编辑上版数据同源覆盖及 19 项属性双向增量核查测试 ==========");

            string projectRoot = FindProjectRoot();
            string prevDwgPath = Path.Combine(projectRoot, "Data", "上版数据.dwg");
            string planDwgPath = Path.Combine(projectRoot, "Data", "新版数据.dwg");

            AssertTrue(File.Exists(prevDwgPath), $"上版数据图纸文件存在: {prevDwgPath}");
            AssertTrue(File.Exists(planDwgPath), $"规划数据图纸文件存在: {planDwgPath}");

            if (!File.Exists(prevDwgPath) || !File.Exists(planDwgPath))
            {
                Logger.Error("测试图纸缺失，中止 test_compare_segment。");
                return;
            }

            try
            {
                using (Database prevDb = new Database(false, true))
                using (Database planDb = new Database(false, true))
                {
                    prevDb.ReadDwgFile(prevDwgPath, FileOpenMode.OpenForReadAndAllShare, true, "");
                    prevDb.CloseInput(true);

                    planDb.ReadDwgFile(planDwgPath, FileOpenMode.OpenForReadAndAllShare, true, "");
                    planDb.CloseInput(true);

                    var session = DocumentSessionManager.GetOrCreateSession(planDb);
                    session.DatasetContext.ClearSessionData();
                    var datasetContext = session.DatasetContext;

                    datasetContext.PreviousDataSource = new DataSourceDescriptor
                    {
                        Kind = DataSourceKind.OpenMdiDocument,
                        InMemoryDatabase = prevDb,
                        FilePath = prevDwgPath,
                        DisplayName = "上版数据.dwg"
                    };

                    var compareSvc = new CompareSegmentService(planDb)
                    {
                        OverridePreviousDatabase = prevDb
                    };

                    // =========================================================================
                    // 场景 1 & 2：验证双向同源折线段覆盖 (CompareCoverage) 与 19 项属性提取/对比
                    // =========================================================================
                    Logger.Info("--- 场景 1 & 2：验证规划数据与上版数据同源覆盖比较及 19 项业务属性继承/对比 ---");

                    var swCheck = Stopwatch.StartNew();
                    Dictionary<ObjectId, CheckIssueItem> initialIssues = compareSvc.CheckAll();
                    swCheck.Stop();

                    CompareSegmentCheckReport report = compareSvc.LastReport;
                    AssertTrue(report != null, $"CompareSegmentService.CheckAll 成功生成结构化核查报告 (耗时={swCheck.ElapsedMilliseconds}ms)");

                    if (report != null)
                    {
                        Logger.Info(
                            $"  [数据集规模统计] 规划多段线={report.TotalPlanPolylines} 条 ({report.TotalPlanSegments} 折线段), " +
                            $"上版多段线={report.TotalPreviousPolylines} 条 ({report.TotalPreviousSegments} 折线段, " +
                            $"具备完整19项属性={report.PreviousPolylinesWithAll19Attrs} 条)");

                        Logger.Info(
                            $"  [双向同源覆盖统计] " +
                            $"Compare(Plan,Prev): 同源覆盖段={report.PlanToPreviousCoverage.CoveredSegments.Count}, 规划新增/改线段={report.PlanToPreviousCoverage.MissingSegments.Count}; " +
                            $"Compare(Prev,Plan): 上版被保留段={report.PreviousToPlanCoverage.CoveredSegments.Count}, 上版缺失/删减段={report.PreviousToPlanCoverage.MissingSegments.Count}");

                        Logger.Info(
                            $"  [同源管线与19项属性对比] 同源配对总数={report.HomologousPairs.Count}, " +
                            $"属性一致={report.ConsistentHomologousPairs.Count}, 属性不符(WIDTH/PD)={report.AttributeMismatchPairs.Count}, " +
                            $"规划新增管段问题={report.PlanUncoveredSegmentIssues.Count}, 上版缺失管段问题={report.PreviousMissingSegmentIssues.Count}, " +
                            $"总问题项(AllIssueItems)={report.AllIssueItems.Count}");

                        foreach (var mm in report.AttributeMismatchPairs.Take(10))
                        {
                            Logger.Info(
                                $"    [同源属性差异明细] 规划管=[{mm.PlanHandle}]({mm.PlanCategoryName}, '{mm.PlanRawLabel}') <-> " +
                                $"上版管=[{mm.PreviousHandle}]({mm.PreviousLayerName}, 重合={mm.OverlapLength:F1}m) -> " +
                                $"{string.Join("；", mm.AttributeDifferences)}");
                        }

                        AssertTrue(report.TotalPlanPolylines == 256 && report.TotalPreviousPolylines == 317,
                            $"准确加载 256 条规划管线与 317 条上版管线 (Plan={report.TotalPlanPolylines}, Prev={report.TotalPreviousPolylines})");

                        AssertTrue(report.PreviousPolylinesWithAll19Attrs == 317,
                            $"全部 317 条上版管线均完整提取并挂载 ws 块的 19 项业务属性 (实际={report.PreviousPolylinesWithAll19Attrs})");

                        AssertTrue(
                            report.PlanToPreviousCoverage.CoveredSegments.Count > 0 &&
                            report.PlanToPreviousCoverage.MissingSegments.Count > 0 &&
                            report.PreviousToPlanCoverage.CoveredSegments.Count > 0 &&
                            report.PreviousToPlanCoverage.MissingSegments.Count > 0,
                            "正向 Compare(Plan,Prev) 与反向 Compare(Prev,Plan) 均准确识别同源覆盖段与新增/缺失折线段");

                        AssertTrue(
                            report.HomologousPairs.Count > 0 &&
                            report.HomologousPairs.All(h => h.Inherited19Attributes != null && h.Inherited19Attributes.Count == 19),
                            $"全部 {report.HomologousPairs.Count} 组同源配对管线均完整继承上版 19 项标准属性 (QSBG..SHAPE_TYPE)");

                        AssertTrue(report.ConsistentHomologousPairs.Count > 0 && report.AttributeMismatchPairs.Count > 0,
                            $"准确区分同源属性一致管线 ({report.ConsistentHomologousPairs.Count} 组) 与同源属性差异管线 ({report.AttributeMismatchPairs.Count} 组)");

                        AssertTrue(
                            report.PreviousMissingSegmentIssues.Count > 0 &&
                            report.PreviousMissingSegmentIssues.All(i =>
                                i.TargetObjectId.IsNull &&
                                (Math.Abs(i.Location.X) > 1.0 || Math.Abs(i.Location.Y) > 1.0)),
                            $"全部 {report.PreviousMissingSegmentIssues.Count} 条上版缺失管段问题均支持 TargetObjectId == ObjectId.Null 跨图存储并具备有效定位坐标");

                        AssertTrue(session.IssuesById.Count == report.AllIssueItems.Count,
                            $"DocumentCheckSession.IssuesById 完整保存全部 {report.AllIssueItems.Count} 条核查问题（含同管线多子项及 ObjectId.Null 上版缺失项，零键冲突）");
                    }

                    // =========================================================================
                    // 场景 3：验证双向编辑增量感知（方向 A：修改规划文字；方向 B：修改上版 ws 属性块字段）
                    // =========================================================================
                    Logger.Info("--- 场景 3：验证 CompareSegmentService 双向编辑增量感知（修改规划数据 & 修改可编辑上版数据） ---");

                    HomologousPipeComparisonItem testMismatch = report?.AttributeMismatchPairs.FirstOrDefault(m =>
                        !m.IsDiameterConsistent &&
                        m.PlanDiameter.HasValue &&
                        m.PreviousDiameter.HasValue &&
                        datasetContext.PlanPipesByHandle.TryGetValue(m.PlanHandle, out var pInfo) &&
                        !string.IsNullOrWhiteSpace(pInfo.MatchedTextHandle));

                    AssertTrue(testMismatch != null,
                        $"找到用于双向增量验证的同源属性差异管线对: 规划管=[{testMismatch?.PlanHandle}](DN{testMismatch?.PlanDiameter:0}) <-> 上版管=[{testMismatch?.PreviousHandle}](DN{testMismatch?.PreviousDiameter:0})");

                    if (testMismatch != null &&
                        datasetContext.PlanPipesByHandle.TryGetValue(testMismatch.PlanHandle, out EnrichedPlanPipeInfo planInfo))
                    {
                        string targetIssueId = $"CompareSegment:{testMismatch.PlanHandle}:ATTR_0";
                        ObjectId planTextId = FindObjectIdByHandle(planDb, planInfo.MatchedTextHandle);

                        // ---------------------------------------------------------------------
                        // 3.1 方向 A：修改【规划数据】的管径文字标注，使其与上版 ws 块一致
                        // ---------------------------------------------------------------------
                        string origPlanText = string.Empty;
                        string alignedPlanText = testMismatch.PreviousSlope.HasValue && testMismatch.PlanSlope.HasValue
                            ? $"d{testMismatch.PreviousDiameter.Value:0}-i{testMismatch.PreviousSlope.Value:0.##}"
                            : $"d{testMismatch.PreviousDiameter.Value:0}";

                        using (Transaction tr = planDb.TransactionManager.StartTransaction())
                        {
                            Entity txtEnt = tr.GetObject(planTextId, OpenMode.ForWrite) as Entity;
                            if (txtEnt is DBText dbTxt)
                            {
                                origPlanText = dbTxt.TextString;
                                dbTxt.TextString = alignedPlanText;
                            }
                            else if (txtEnt is MText mTxt)
                            {
                                origPlanText = mTxt.Contents;
                                mTxt.Contents = alignedPlanText;
                            }

                            var affectedFromPlanEdit = compareSvc.GetAffectedTargetIds(txtEnt).ToList();
                            AssertTrue(affectedFromPlanEdit.Contains(testMismatch.PlanPipeId),
                                $"[方向 A - 规划端编辑] GetAffectedTargetIds 准确追踪到修改规划文字 [{planInfo.MatchedTextHandle}] 受影响的规划管 [{testMismatch.PlanHandle}]");

                            tr.Commit();
                        }

                        var afterPlanEditIssues = compareSvc.CheckIncremental(
                            new List<ObjectId> { testMismatch.PlanPipeId },
                            initialIssues);

                        bool resolvedByPlanEdit =
                            session.IssuesById.TryGetValue(targetIssueId, out CheckIssueItem itemAfterPlanEdit) &&
                            itemAfterPlanEdit != null &&
                            itemAfterPlanEdit.Status == IssueReviewStatus.Resolved;

                        Logger.Info(
                            $"  [方向 A 增量验证] 修改规划文字 '{origPlanText}' -> '{alignedPlanText}' 后, " +
                            $"问题 [{targetIssueId}] 状态={itemAfterPlanEdit?.Status}, 备注={itemAfterPlanEdit?.Remark}");

                        AssertTrue(resolvedByPlanEdit,
                            $"[方向 A - 规划端编辑] 修改规划文字使管径对齐上版后，CheckIncremental 自动将 [{targetIssueId}] 归档为 Resolved");

                        // 还原规划文字，恢复 Pending 状态
                        using (Transaction tr = planDb.TransactionManager.StartTransaction())
                        {
                            Entity txtEnt = tr.GetObject(planTextId, OpenMode.ForWrite) as Entity;
                            if (txtEnt is DBText dbTxt)
                                dbTxt.TextString = origPlanText;
                            else if (txtEnt is MText mTxt)
                                mTxt.Contents = origPlanText;
                            tr.Commit();
                        }

                        compareSvc.CheckIncremental(new List<ObjectId> { testMismatch.PlanPipeId }, afterPlanEditIssues);
                        AssertTrue(
                            session.IssuesById.TryGetValue(targetIssueId, out CheckIssueItem restoredItem) &&
                            restoredItem?.Status == IssueReviewStatus.Pending,
                            $"[方向 A - 还原验证] 还原规划文字为 '{origPlanText}' 后，问题 [{targetIssueId}] 准确恢复为 Pending");

                        // ---------------------------------------------------------------------
                        // 3.2 方向 B：修改【可编辑上版数据】ws 属性块内的 AttributeReference (WIDTH / PD)
                        // ---------------------------------------------------------------------
                        var prevMatcher = new PipeSpatialMatcher();
                        var prevMatchSummary = prevMatcher.MatchDatabaseBlockAttributes(
                            prevDb,
                            new[] { "0污水-待补测现状管", "0污水-规划新建", "0污水-规划改建", "0污水-已核实需新增" },
                            new[] { "0污水-属性" });

                        ObjectId prevPipeObjId = FindObjectIdByHandle(prevDb, testMismatch.PreviousHandle);
                        PipeAttachmentMatchItem prevBlockMatch = null;
                        if (!prevPipeObjId.IsNull)
                        {
                            prevMatchSummary.MatchByPipeId.TryGetValue(prevPipeObjId, out prevBlockMatch);
                        }

                        ObjectId wsBlockId = prevBlockMatch?.MatchedAttachment?.AttachmentId ?? ObjectId.Null;
                        AssertTrue(!wsBlockId.IsNull && wsBlockId.IsValid,
                            $"[方向 B - 上版端编辑] 定位上版管 [{testMismatch.PreviousHandle}] 配对的 ws 属性块 Handle=[{prevBlockMatch?.MatchedAttachment?.Handle}]");

                        if (!wsBlockId.IsNull && wsBlockId.IsValid)
                        {
                            string origPrevWidthStr = string.Empty;
                            string origPrevSlopeStr = string.Empty;
                            string newPrevWidthStr = $"{testMismatch.PlanDiameter.Value:0}";
                            string newPrevSlopeStr = testMismatch.PlanSlope.HasValue ? $"{testMismatch.PlanSlope.Value:0.##}" : null;

                            using (Transaction tr = prevDb.TransactionManager.StartTransaction())
                            {
                                BlockReference wsBr = tr.GetObject(wsBlockId, OpenMode.ForRead) as BlockReference;
                                AttributeReference widthAttrRef = null;
                                AttributeReference pdAttrRef = null;

                                if (wsBr != null)
                                {
                                    foreach (ObjectId attId in wsBr.AttributeCollection)
                                    {
                                        AttributeReference ar = tr.GetObject(attId, OpenMode.ForWrite) as AttributeReference;
                                        if (ar == null) continue;
                                        if (string.Equals(ar.Tag, "WIDTH", StringComparison.OrdinalIgnoreCase))
                                        {
                                            widthAttrRef = ar;
                                            origPrevWidthStr = ar.TextString;
                                            ar.TextString = newPrevWidthStr;
                                        }
                                        else if (newPrevSlopeStr != null && string.Equals(ar.Tag, "PD", StringComparison.OrdinalIgnoreCase))
                                        {
                                            pdAttrRef = ar;
                                            origPrevSlopeStr = ar.TextString;
                                            ar.TextString = newPrevSlopeStr;
                                        }
                                    }
                                }

                                AssertTrue(widthAttrRef != null,
                                    $"[方向 B - 上版端编辑] 成功修改上版 ws 属性块内 WIDTH 字段 ('{origPrevWidthStr}' -> '{newPrevWidthStr}')");

                                // 验证修改上版 ws 块内 AttributeReference 时：
                                // 1. 自动使 PipeDatasetContext 的上版动态缓存失效 (IsPreviousDataCacheValid = false)
                                // 2. 反向查出同源关联的规划管线 ObjectId
                                var affectedFromPrevEdit = compareSvc.GetAffectedTargetIds(widthAttrRef).ToList();

                                AssertTrue(!datasetContext.IsPreviousDataCacheValid,
                                    "[方向 B - 上版端编辑] 修改上版 ws 属性块字段后，自动触发 PipeDatasetContext.InvalidatePreviousData() 使缓存失效");

                                AssertTrue(affectedFromPrevEdit.Contains(testMismatch.PlanPipeId),
                                    $"[方向 B - 上版端编辑] GetAffectedTargetIds 通过上版 ws 块反向追踪到同源规划管 [{testMismatch.PlanHandle}]");

                                tr.Commit();
                            }

                            var swPrevInc = Stopwatch.StartNew();
                            compareSvc.CheckIncremental(new List<ObjectId> { testMismatch.PlanPipeId }, initialIssues);
                            swPrevInc.Stop();

                            session.IssuesById.TryGetValue(targetIssueId, out CheckIssueItem itemAfterPrevEdit);
                            bool resolvedByPrevEdit =
                                datasetContext.IsPreviousDataCacheValid &&
                                itemAfterPrevEdit != null &&
                                itemAfterPrevEdit.Status == IssueReviewStatus.Resolved;

                            Logger.Info(
                                $"  [方向 B 增量验证] 修改上版 ws 块 WIDTH='{newPrevWidthStr}' 后, " +
                                $"上版缓存已重建={datasetContext.IsPreviousDataCacheValid}, 问题 [{targetIssueId}] 状态={itemAfterPrevEdit?.Status}, " +
                                $"耗时={swPrevInc.ElapsedMilliseconds}ms, 备注={itemAfterPrevEdit?.Remark}");

                            AssertTrue(resolvedByPrevEdit,
                                $"[方向 B - 上版端编辑] 修改上版 ws 属性块字段后，CheckIncremental 自动重载上版数据并将 [{targetIssueId}] 归档为 Resolved");

                            // 还原上版 ws 块字段
                            using (Transaction tr = prevDb.TransactionManager.StartTransaction())
                            {
                                BlockReference wsBr = tr.GetObject(wsBlockId, OpenMode.ForRead) as BlockReference;
                                if (wsBr != null)
                                {
                                    foreach (ObjectId attId in wsBr.AttributeCollection)
                                    {
                                        AttributeReference ar = tr.GetObject(attId, OpenMode.ForWrite) as AttributeReference;
                                        if (ar == null) continue;
                                        if (string.Equals(ar.Tag, "WIDTH", StringComparison.OrdinalIgnoreCase))
                                        {
                                            ar.TextString = origPrevWidthStr;
                                            compareSvc.GetAffectedTargetIds(ar);
                                        }
                                        else if (newPrevSlopeStr != null && string.Equals(ar.Tag, "PD", StringComparison.OrdinalIgnoreCase))
                                        {
                                            ar.TextString = origPrevSlopeStr;
                                        }
                                    }
                                }
                                tr.Commit();
                            }

                            compareSvc.CheckIncremental(new List<ObjectId> { testMismatch.PlanPipeId }, initialIssues);
                            AssertTrue(
                                session.IssuesById.TryGetValue(targetIssueId, out CheckIssueItem restoredAfterPrev) &&
                                restoredAfterPrev?.Status == IssueReviewStatus.Pending,
                                $"[方向 B - 还原验证] 还原上版 ws 属性块字段为 '{origPrevWidthStr}' 后，问题 [{targetIssueId}] 准确恢复为 Pending");
                        }

                        // ============================================================
                        // 场景 4：测试 DataSourceSettingsViewModel 多数据源配置与 ucMainCheckWindow 工具栏核查调度（Commit 6.3）
                        // ============================================================
                        Logger.Info("--- 场景 4：测试 DataSourceSettingsViewModel 与 ucMainCheckWindow 工具栏 [数据源]/[单项核查]/[一键全量核查] (Commit 6.3) ---");

                        string surveyDwgPath = Path.Combine(projectRoot, "Data", "勘测管.dwg");
                        var mockMdiList = new List<process_pipeline.Forms.MdiDocumentOptionItem>
                        {
                            new process_pipeline.Forms.MdiDocumentOptionItem
                            {
                                DisplayName = "上版数据.dwg (内存标签页)",
                                FilePath = prevDwgPath,
                                Database = prevDb,
                                PreviousLayerCurveCount = 317,
                                SurveyLayerCurveCount = 0
                            }
                        };

                        var dsVm = new process_pipeline.Forms.DataSourceSettingsViewModel(
                            planDb,
                            datasetContext,
                            new DataSourceService(),
                            mockMdiList);

                        AssertTrue(dsVm.PlanningCurveCount >= 256,
                            $"[DataSourceSettingsViewModel] 自动识别当前激活主图规划管线曲线数量 >= 256 (实际={dsVm.PlanningCurveCount})");

                        // 切换上版数据为 MDI 已打开文档模式，勘测数据为外部 DWG 文件模式并执行 ConfirmCommand
                        dsVm.SelectedPreviousMdiDoc = mockMdiList[0];
                        dsVm.IsPreviousOpenMdi = true;
                        dsVm.SurveyExternalFilePath = surveyDwgPath;
                        dsVm.IsSurveyExternalFile = true;
                        dsVm.ConfirmCommand.Execute(null);

                        AssertTrue(
                            dsVm.DialogConfirmed &&
                            datasetContext.PreviousDataSource.Kind == DataSourceKind.OpenMdiDocument &&
                            datasetContext.SurveyDataSource.Kind == DataSourceKind.ExternalDwgFile &&
                            string.Equals(CadConfig.DataSources.LastSurveyDataDwgPath, Path.GetFullPath(surveyDwgPath), StringComparison.OrdinalIgnoreCase),
                            "[DataSourceSettingsViewModel] 支持 MDI 文档与外部 DWG 模式切换，ConfirmCommand 同步更新会话上下文与 Config.json 记忆路径");

                        // 验证 MainCheckWindowViewModel 顶部工具栏命令绑定：[数据源]、[单项核查]、[一键全量核查]
                        var mainVm = new process_pipeline.Forms.MainCheckWindowViewModel();
                        int dialogLaunchCount = 0;
                        mainVm.CustomDataSourceDialogLauncher = vm =>
                        {
                            dialogLaunchCount++;
                            vm.PreviousExternalFilePath = prevDwgPath;
                            vm.SurveyExternalFilePath = surveyDwgPath;
                            return vm.ConfirmAndSave();
                        };

                        mainVm.ExecuteOpenDataSourceSettings(planDb);
                        AssertTrue(dialogLaunchCount == 1,
                            "[ucMainCheckWindow 工具栏] 点击 [数据源] 按钮正常触发 DataSourceSettingsViewModel 配置流程");

                        // 测试 [单项核查]：选择 PlanDataCheck 单项执行
                        mainVm.SelectedSingleRule = mainVm.AvailableSingleRules.FirstOrDefault(r =>
                            string.Equals(r.RuleId, PlanDataCheckService.DefaultRuleId, StringComparison.OrdinalIgnoreCase));
                        var singleRuleIssues = mainVm.ExecuteRunSingleCheck(planDb, null, showPalette: false);
                        AssertTrue(
                            singleRuleIssues != null && singleRuleIssues.Count >= 11,
                            $"[ucMainCheckWindow 工具栏] 执行 [单项核查 - 规划数据自检] 成功返回 {singleRuleIssues?.Count ?? 0} 条记录");

                        // 测试 [一键全量核查]：顺序执行全部 5 项规则并汇总展示
                        var fullSummary = mainVm.ExecuteRunFullCheck(planDb, null, showPalette: false);
                        Logger.Info(
                            $"  [一键全量核查汇总] 执行规则数={fullSummary.ExecutedRuleIds.Count} ({string.Join(", ", fullSummary.ExecutedRuleIds)}), " +
                            $"总问题条目数={fullSummary.AllMergedIssues.Count}, 面板表格行数={mainVm.Rows.Count}, 耗时={fullSummary.ElapsedMilliseconds}ms");

                        AssertTrue(
                            fullSummary.ExecutedRuleIds.Count == 5 &&
                            session.ActiveRules.Count == 5 &&
                            mainVm.Rows.Count == fullSummary.AllMergedIssues.Count &&
                            mainVm.Rows.Count > 1000,
                            $"[ucMainCheckWindow 工具栏] 执行 [一键全量核查] 成功串联全部 5 项核查规则并汇聚 {mainVm.Rows.Count} 条记录至主核查面板");
                    }
                }
            }
            catch (System.Exception ex)
            {
                _failCount++;
                Logger.Error(ex, "[test_compare_segment] 执行过程中发生未捕获异常");
            }

            if (_failCount == 0)
            {
                Logger.Info($"========== [test_compare_segment] 测试全部通过：通过 {_passCount} 项，失败 0 项。 ==========");
            }
            else
            {
                Logger.Error($"========== [test_compare_segment] 测试完成：通过 {_passCount} 项，失败 {_failCount} 项。 ==========");
            }
        }

        private static ObjectId FindObjectIdByHandle(Database db, string handleStr)
        {
            if (db == null || string.IsNullOrWhiteSpace(handleStr))
                return ObjectId.Null;

            try
            {
                long val = Convert.ToInt64(handleStr.Trim(), 16);
                Handle h = new Handle(val);
                if (db.TryGetObjectId(h, out ObjectId id))
                {
                    return id;
                }
            }
            catch
            {
                // 忽略非法句柄
            }
            return ObjectId.Null;
        }

        private void AssertTrue(bool condition, string message)
        {
            if (condition)
            {
                _passCount++;
                Logger.Info($"  [PASS] {message}");
            }
            else
            {
                _failCount++;
                Logger.Error($"  [FAIL] {message}");
            }
        }

        private static string FindProjectRoot()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 6; i++)
            {
                if (File.Exists(Path.Combine(dir, "process_pipeline.csproj")) ||
                    Directory.Exists(Path.Combine(dir, "Data")))
                {
                    return dir;
                }
                DirectoryInfo parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            return @"D:\Codes\Tools\process_pipeline";
        }
    }
}
