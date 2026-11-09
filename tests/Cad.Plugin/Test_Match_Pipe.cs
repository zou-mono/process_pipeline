using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using NetTopologySuite.Geometries;
using NLog;
using process_pipeline.Commands;
using process_pipeline.Core;
using process_pipeline.Core.Config;
using process_pipeline.Core.DataSource;
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
    /// 阶段六 Commit 6.1：三类规划管线（新建、现状、改扩建）与只读合并勘测管分段匹配及管径一致性核查无 UI 自动化测试。
    /// 命令：test_match_pipe
    /// </summary>
    public class Test_Match_Pipe : CadCommandBase
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private int _passCount;
        private int _failCount;

        [CommandMethod("test_match_pipe")]
        public override void Execute()
        {
            _passCount = 0;
            _failCount = 0;

            Logger.Info("========== 开始执行 [test_match_pipe] 规划管与只读合并勘测管分段匹配及管径核查测试 ==========");

            string projectRoot = FindProjectRoot();
            string surveyDwgPath = Path.Combine(projectRoot, "Data", "勘测管.dwg");
            string planDwgPath = Path.Combine(projectRoot, "Data", "新版数据.dwg");

            AssertTrue(File.Exists(surveyDwgPath), $"勘测基准图纸文件存在: {surveyDwgPath}");
            AssertTrue(File.Exists(planDwgPath), $"规划数据图纸文件存在: {planDwgPath}");

            if (!File.Exists(surveyDwgPath) || !File.Exists(planDwgPath))
            {
                Logger.Error("测试图纸缺失，中止 test_match_pipe。");
                return;
            }

            try
            {
                PipeDatasetContext.ClearGlobalSurveyCache();

                using (Database surveyDb = new Database(false, true))
                using (Database planDb = new Database(false, true))
                {
                    surveyDb.ReadDwgFile(surveyDwgPath, FileOpenMode.OpenForReadAndAllShare, true, "");
                    surveyDb.CloseInput(true);

                    planDb.ReadDwgFile(planDwgPath, FileOpenMode.OpenForReadAndAllShare, true, "");
                    planDb.CloseInput(true);

                    var session = DocumentSessionManager.GetOrCreateSession(planDb);
                    session.DatasetContext.ClearSessionData();
                    var datasetContext = session.DatasetContext;

                    datasetContext.SurveyDataSource = new DataSourceDescriptor
                    {
                        Kind = DataSourceKind.ExternalDwgFile,
                        FilePath = surveyDwgPath,
                        DisplayName = "勘测管.dwg",
                        PipeLayers = new List<string> { "勘测管" },
                        AttachmentLayers = new List<string> { "勘测管" }
                    };

                    // =========================================================================
                    // 场景 1：验证只读勘测管按 WIDTH 拓扑合并与全局静态 STRtree 缓存
                    // =========================================================================
                    Logger.Info("--- 场景 1：验证只读勘测管（Data\\勘测管.dwg）按 WIDTH 拓扑合并与静态 STRtree 缓存 ---");

                    var swCache1 = Stopwatch.StartNew();
                    SurveyDatasetCacheEntry surveyCache1 = datasetContext.GetOrBuildSurveyCache(
                        surveyDb,
                        new[] { "勘测管" });
                    swCache1.Stop();

                    var swCache2 = Stopwatch.StartNew();
                    SurveyDatasetCacheEntry surveyCache2 = datasetContext.GetOrBuildSurveyCache(
                        surveyDb,
                        new[] { "勘测管" });
                    swCache2.Stop();

                    Logger.Info(
                        $"  [勘测管拓扑合并统计] 原始勘测管={surveyCache1.RawPolylines.Count} 条 -> " +
                        $"按 WIDTH 合并后={surveyCache1.MergedPolylines.Count} 条, " +
                        $"首次构建耗时={swCache1.ElapsedMilliseconds}ms, 二次命中静态缓存耗时={swCache2.ElapsedMilliseconds}ms");

                    AssertTrue(surveyCache1.RawPolylines.Count == 5823,
                        $"准确读取 Data\\勘测管.dwg 全部 5823 条原始勘测管 (实际={surveyCache1.RawPolylines.Count})");

                    AssertTrue(
                        surveyCache1.MergedPolylines.Count > 0 &&
                        surveyCache1.MergedPolylines.Count < surveyCache1.RawPolylines.Count,
                        $"PolylineGraphMerger 按管径 WIDTH 成功合并相邻同径勘测管 ({surveyCache1.RawPolylines.Count} -> {surveyCache1.MergedPolylines.Count})");

                    AssertTrue(
                        surveyCache1.MergedPolylines.All(p => p.GetAttributeDouble("WIDTH").HasValue && p.SourceHandles != null && p.SourceHandles.Count > 0),
                        "合并后的全部勘测管均完整保留管径 WIDTH 属性与原始 SourceHandles 溯源列表");

                    AssertTrue(ReferenceEquals(surveyCache1, surveyCache2),
                        "PipeDatasetContext.GetOrBuildSurveyCache 二次调用 100% 命中只读静态缓存实例");

                    // =========================================================================
                    // 场景 2：串联 PipeDatasetContext 验证三类规划管线与合并勘测管的分段匹配及管径核查
                    // =========================================================================
                    Logger.Info("--- 场景 2：验证三类规划管线（新建/现状/改扩建）与只读合并勘测管分段匹配及管径一致性核查 ---");

                    var matchPipeSvc = new MatchPipeService(planDb)
                    {
                        OverrideSurveyDatabase = surveyDb
                    };

                    var swCheck = Stopwatch.StartNew();
                    Dictionary<ObjectId, CheckIssueItem> issueMap = matchPipeSvc.CheckAll();
                    swCheck.Stop();

                    MatchPipeCheckReport report = matchPipeSvc.LastReport;
                    AssertTrue(report != null, $"MatchPipeService.CheckAll 成功生成结构化核查报告 (耗时={swCheck.ElapsedMilliseconds}ms)");

                    if (report != null)
                    {
                        Logger.Info(
                            $"  [规划管总体统计] 三类规划管总数={report.TotalPlanPipeCount}, " +
                            $"分段总记录数={report.AllSegmentItems.Count}, " +
                            $"跨越多个勘测管段的分段记录(MultiSegment)={report.MultiSegmentPipeItems.Count}");

                        foreach (var kvp in report.PlanCountByCategory)
                        {
                            string catId = kvp.Key;
                            int total = kvp.Value;
                            int matched = report.MatchedPipeCountByCategory.TryGetValue(catId, out int m) ? m : 0;
                            int unmatched = report.UnmatchedPipeCountByCategory.TryGetValue(catId, out int u) ? u : 0;
                            Logger.Info($"    [类别统计] {catId}: 总管线={total}, 匹配上勘测管={matched}, 未匹配勘测管={unmatched}");
                        }

                        Logger.Info(
                            $"  [管径核查结论汇总] 管径一致分段={report.DiameterConsistentItems.Count}, " +
                            $"管径不符分段={report.DiameterMismatchItems.Count}, " +
                            $"匹配段缺规划管径标注={report.MissingPlanLabelOnMatchedItems.Count}, " +
                            $"现状管未匹配勘测管={report.UnmatchedExistingPipeItems.Count}, " +
                            $"总问题子项(AllIssueItems)={report.AllIssueItems.Count}");

                        // 输出前 12 条管径不符明细
                        foreach (var mm in report.DiameterMismatchItems.Take(12))
                        {
                            string sHandles = mm.SurveyHandles != null ? string.Join(",", mm.SurveyHandles.Take(3)) : "-";
                            Logger.Info(
                                $"    [管径不符明细] 规划管=[{mm.PlanHandle}]({mm.CategoryName}, SubIdx={mm.SubIndex}) " +
                                $"规划DN{mm.PlanDiameter:0}('{mm.PlanRawLabel}') vs 勘测管=[{sHandles}] DN{mm.SurveyDiameter:0}, " +
                                $"区间=[{mm.TargetInterval?.Start:F1}~{mm.TargetInterval?.End:F1}m], 均距={mm.AverageDistance:F2}m");
                        }

                        // 输出前 8 条匹配上勘测管但缺规划文字标注的明细
                        foreach (var miss in report.MissingPlanLabelOnMatchedItems.Take(8))
                        {
                            string sHandles = miss.SurveyHandles != null ? string.Join(",", miss.SurveyHandles.Take(3)) : "-";
                            Logger.Info(
                                $"    [匹配段缺标注明细] 规划管=[{miss.PlanHandle}]({miss.CategoryName}, SubIdx={miss.SubIndex}) " +
                                $"无规划管径标注 -> 对应勘测管=[{sHandles}] DN{miss.SurveyDiameter:0}, " +
                                $"区间=[{miss.TargetInterval?.Start:F1}~{miss.TargetInterval?.End:F1}m]");
                        }

                        AssertTrue(
                            report.TotalPlanPipeCount == 256 &&
                            report.PlanCountByCategory.TryGetValue("PlanNew", out int newCnt) && newCnt == 13 &&
                            report.PlanCountByCategory.TryGetValue("Existing", out int existCnt) && existCnt == 228 &&
                            report.PlanCountByCategory.TryGetValue("PlanExpand", out int expandCnt) && expandCnt == 15,
                            $"全部三类规划管线（新建=13, 现状=228, 改扩建=15, 总计={report.TotalPlanPipeCount}）均已纳入管径匹配核查");

                        AssertTrue(report.DiameterConsistentItems.Count > 0,
                            $"成功识别出 {report.DiameterConsistentItems.Count} 处规划管径与勘测管径完全一致的合规管段");

                        AssertTrue(report.DiameterMismatchItems.Count > 0,
                            $"成功检出 {report.DiameterMismatchItems.Count} 处规划管径与勘测管径不一致的异常分段");

                        AssertTrue(report.MissingPlanLabelOnMatchedItems.Count > 0,
                            $"成功检出 {report.MissingPlanLabelOnMatchedItems.Count} 处已匹配勘测管但规划管缺失文字标注的分段（自动提示对应勘测管径）");

                        AssertTrue(report.UnmatchedExistingPipeItems.Count > 0,
                            $"成功检出 {report.UnmatchedExistingPipeItems.Count} 条未在勘测管中找到同源管段的规划现状管");

                        // 验证已匹配勘测管的异常项均已生成指向勘测管的瞬态辅助连线 (HelperLine)
                        var matchedIssues = report.AllIssueItems
                            .Where(i => i.RulePayload is MatchPipeSegmentCheckItem seg && seg.IsMatchedToSurvey)
                            .ToList();
                        AssertTrue(
                            matchedIssues.Count > 0 && matchedIssues.All(i => i.PossibleMatches != null && i.PossibleMatches.Count == 1),
                            $"全部 {matchedIssues.Count} 条已匹配勘测管的异常分段均已生成指向勘测管的瞬态辅助连线 (PossibleMatches.Count == 1)");

                        // 验证同一规划管存在多个不同 SubIndex 问题子项时在 DocumentCheckSession.IssuesById 中并存不覆盖
                        var multiSubIssueGroups = report.AllIssueItems
                            .GroupBy(i => i.TargetHandle, StringComparer.OrdinalIgnoreCase)
                            .Where(g => g.Count() > 1)
                            .ToList();

                        AssertTrue(
                            session.IssuesById.Count == report.AllIssueItems.Count,
                            $"DocumentCheckSession.IssuesById 按唯一 IssueId 完整保存全部 {report.AllIssueItems.Count} 条分段问题（其中单管多子项管线={multiSubIssueGroups.Count} 根，零覆盖丢失）");
                    }

                    // =========================================================================
                    // 场景 3：验证修改规划文字标注后 MatchPipeService.CheckIncremental 毫秒级增量重算与自动归档
                    // =========================================================================
                    Logger.Info("--- 场景 3：验证修改规划管径文字标注后 MatchPipeService.CheckIncremental 毫秒级增量复核与自动归档 ---");

                    // 挑选一条单段匹配（该管线仅匹配 1 段勘测管）且管径不符的规划管线，验证修改其关联文字标注后自动归档为 Resolved
                    var singleMismatchGroup = report?.DiameterMismatchItems
                        .GroupBy(x => x.PlanHandle, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault(g =>
                            g.Count() == 1 &&
                            report.AllSegmentItems.Count(s => string.Equals(s.PlanHandle, g.Key, StringComparison.OrdinalIgnoreCase)) == 1 &&
                            g.First().SurveyDiameter.HasValue &&
                            datasetContext.PlanPipesByHandle.TryGetValue(g.Key, out var pInfo) &&
                            !string.IsNullOrWhiteSpace(pInfo.MatchedTextHandle));

                    MatchPipeSegmentCheckItem targetMismatchSeg = singleMismatchGroup?.First();
                    AssertTrue(targetMismatchSeg != null,
                        $"找到可用于增量修改验证的管径不符规划管: Handle={targetMismatchSeg?.PlanHandle}, 规划DN{targetMismatchSeg?.PlanDiameter:0} vs 勘测DN{targetMismatchSeg?.SurveyDiameter:0}");

                    if (targetMismatchSeg != null &&
                        datasetContext.PlanPipesByHandle.TryGetValue(targetMismatchSeg.PlanHandle, out EnrichedPlanPipeInfo targetPlanInfo))
                    {
                        string targetIssueId = $"MatchPipe:{targetMismatchSeg.PlanHandle}:{targetMismatchSeg.SubIndex}";
                        string matchedTextHandle = targetPlanInfo.MatchedTextHandle;
                        ObjectId textObjId = FindObjectIdByHandle(planDb, matchedTextHandle);

                        AssertTrue(!textObjId.IsNull && textObjId.IsValid,
                            $"成功定位规划管 [{targetMismatchSeg.PlanHandle}] 关联的 DBText 文字图元 Handle=[{matchedTextHandle}]");

                        string originalText = string.Empty;
                        string fixedText = $"d{targetMismatchSeg.SurveyDiameter.Value:0}";

                        // 3.1 在内存中将 DBText 改为与勘测管一致的管径文本
                        using (Transaction tr = planDb.TransactionManager.StartTransaction())
                        {
                            Entity ent = tr.GetObject(textObjId, OpenMode.ForWrite) as Entity;
                            if (ent is DBText dbText)
                            {
                                originalText = dbText.TextString;
                                dbText.TextString = fixedText;
                            }
                            else if (ent is MText mText)
                            {
                                originalText = mText.Contents;
                                mText.Contents = fixedText;
                            }

                            var affectedIds = matchPipeSvc.GetAffectedTargetIds(ent).ToList();
                            AssertTrue(affectedIds.Contains(targetMismatchSeg.PlanPipeId),
                                $"MatchPipeService.GetAffectedTargetIds 准确追踪到修改文字 [{matchedTextHandle}] 受影响的规划管 [{targetMismatchSeg.PlanHandle}]");

                            tr.Commit();
                        }

                        // 置空 OverrideSurveyDatabase，验证 CheckIncremental 直接命中内存静态缓存而不重新打开 surveyDb
                        matchPipeSvc.OverrideSurveyDatabase = null;

                        var swInc = Stopwatch.StartNew();
                        Dictionary<ObjectId, CheckIssueItem> afterFixIssues = matchPipeSvc.CheckIncremental(
                            new List<ObjectId> { targetMismatchSeg.PlanPipeId },
                            issueMap);
                        swInc.Stop();

                        bool isResolvedInSession =
                            session.IssuesById.TryGetValue(targetIssueId, out CheckIssueItem resolvedItem) &&
                            resolvedItem != null &&
                            resolvedItem.Status == IssueReviewStatus.Resolved;

                        Logger.Info(
                            $"  [增量修复验证] 将文字 [{matchedTextHandle}] 从 '{originalText}' 改为 '{fixedText}' 后, " +
                            $"问题 [{targetIssueId}] 状态={resolvedItem?.Status}, 耗时={swInc.ElapsedMilliseconds}ms, 备注={resolvedItem?.Remark}");

                        AssertTrue(isResolvedInSession,
                            $"将规划文字标注改为 '{fixedText}' 后，CheckIncremental 自动将 [{targetIssueId}] 归档为 Resolved（已修复）状态");

                        // 3.2 还原 DBText 原始文本，验证增量重算恢复 Pending 状态
                        using (Transaction tr = planDb.TransactionManager.StartTransaction())
                        {
                            Entity ent = tr.GetObject(textObjId, OpenMode.ForWrite) as Entity;
                            if (ent is DBText dbText)
                            {
                                dbText.TextString = originalText;
                            }
                            else if (ent is MText mText)
                            {
                                mText.Contents = originalText;
                            }
                            tr.Commit();
                        }

                        matchPipeSvc.CheckIncremental(
                            new List<ObjectId> { targetMismatchSeg.PlanPipeId },
                            afterFixIssues);

                        bool isRestoredPending =
                            session.IssuesById.TryGetValue(targetIssueId, out CheckIssueItem restoredItem) &&
                            restoredItem != null &&
                            restoredItem.Status == IssueReviewStatus.Pending;

                        AssertTrue(isRestoredPending,
                            $"将规划文字标注还原为 '{originalText}' 后，CheckIncremental 准确将 [{targetIssueId}] 恢复为 Pending（待核查）状态");
                    }
                }
            }
            catch (System.Exception ex)
            {
                _failCount++;
                Logger.Error(ex, "[test_match_pipe] 执行过程中发生未捕获异常");
            }

            if (_failCount == 0)
            {
                Logger.Info($"========== [test_match_pipe] 测试全部通过：通过 {_passCount} 项，失败 0 项。 ==========");
            }
            else
            {
                Logger.Error($"========== [test_match_pipe] 测试完成：通过 {_passCount} 项，失败 {_failCount} 项。 ==========");
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
