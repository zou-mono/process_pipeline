using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using process_pipeline.Commands;
using process_pipeline.Core.Config;
using process_pipeline.Core.DataSource;
using process_pipeline.Core.PreCheck;
using process_pipeline.Forms;
using process_pipeline.Models;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcadDb = Autodesk.AutoCAD.DatabaseServices;

namespace process_pipeline.Core
{
    /// <summary>
    /// 单项核查规则下拉选择项模型（供 ucMainCheckWindow 顶部工具栏绑定，Stage 6 Commit 6.3）。
    /// </summary>
    public class CheckRuleOptionItem
    {
        public string RuleId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string CommandName { get; set; } = string.Empty;
        public bool RequiresPreviousData { get; set; }
        public bool RequiresSurveyData { get; set; }

        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// 一键全量核查执行汇总结果（Stage 6 Commit 6.3）。
    /// </summary>
    public class FullPipelineCheckSummary
    {
        public bool PreCheckBlocked { get; set; }
        public bool DataSourcesReady { get; set; }
        public bool DialogShown { get; set; }
        public DataSourceDescriptor PreviousDataSource { get; set; }
        public DataSourceDescriptor SurveyDataSource { get; set; }
        public List<string> ExecutedRuleIds { get; } = new List<string>();
        public Dictionary<string, int> IssueCountByRuleId { get; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<CheckIssueItem> AllMergedIssues { get; set; } = Array.Empty<CheckIssueItem>();
        public long ElapsedMilliseconds { get; set; }
    }

    /// <summary>
    /// 管线核查统一调度协调器（Stage 6 Commit 6.3，MVVM 核心业务编排层）。
    /// 
    /// 职责说明：
    /// 1. 统一收拢 `CHECKPIPE`、`MATCHPIPE`、`COMPARESEGMENT` 以及 `ucMainCheckWindow` 工具栏
    ///    `[数据源]`、`[一键全量核查]`、`[单项核查]` 的业务调度逻辑，实现 UI 表现层与核查算法的彻底解耦。
    /// 2. 执行跨图核查规则前，先走 <see cref="DataSourceService"/> 三级智能嗅探（同图图层 -> 已打开 MDI 文档 -> Config.json 记忆路径）；
    ///    仅当无法自动确定有效数据源（或用户主动点击 `[数据源]` 按钮）时，才弹出 <see cref="DataSourceSettingsWindow"/>。
    /// </summary>
    public static class CheckPipelineCoordinator
    {
        public const string FlowArrowRuleId = "FlowArrow";

        private static readonly IReadOnlyList<CheckRuleOptionItem> _ruleOptions = new List<CheckRuleOptionItem>
        {
            new CheckRuleOptionItem
            {
                RuleId = PlanDataCheckService.DefaultRuleId,
                DisplayName = "规划数据自检 (CHECKPLAN)",
                CommandName = "CHECKPLAN",
                RequiresPreviousData = false,
                RequiresSurveyData = false
            },
            new CheckRuleOptionItem
            {
                RuleId = MatchGroupService.DefaultRuleId,
                DisplayName = "图属与文字配对 (MATCHGROUP)",
                CommandName = "MATCHGROUP",
                RequiresPreviousData = false,
                RequiresSurveyData = false
            },
            new CheckRuleOptionItem
            {
                RuleId = FlowArrowRuleId,
                DisplayName = "流向箭头核查 (MATCHARROW)",
                CommandName = "MATCHARROW",
                RequiresPreviousData = false,
                RequiresSurveyData = false
            },
            new CheckRuleOptionItem
            {
                RuleId = MatchPipeService.DefaultRuleId,
                DisplayName = "管段匹配核查 (MATCHPIPE)",
                CommandName = "MATCHPIPE",
                RequiresPreviousData = false,
                RequiresSurveyData = true
            },
            new CheckRuleOptionItem
            {
                RuleId = CompareSegmentService.DefaultRuleId,
                DisplayName = "线段覆盖核查 (COMPARESEGMENT)",
                CommandName = "COMPARESEGMENT",
                RequiresPreviousData = true,
                RequiresSurveyData = false
            }
        };

        /// <summary>
        /// 获取全部单项核查规则候选项。
        /// </summary>
        public static IReadOnlyList<CheckRuleOptionItem> GetAvailableRuleOptions() => _ruleOptions;

        /// <summary>
        /// 生成当前会话数据源状态的简短文本摘要（供工具栏提示与状态栏展示）。
        /// </summary>
        public static string FormatDataSourceSummary(PipeDatasetContext context)
        {
            if (context == null)
                return "数据源: 未初始化";

            string prevText = context.PreviousDataSource != null && context.PreviousDataSource.IsValid
                ? context.PreviousDataSource.DisplayName
                : "未就绪";
            string surveyText = context.SurveyDataSource != null && context.SurveyDataSource.IsValid
                ? context.SurveyDataSource.DisplayName
                : "未就绪";

            return $"数据源: 上版[{prevText}] | 勘测[{surveyText}]";
        }

        /// <summary>
        /// 主动打开多数据源配置窗口（供工具栏 `[数据源]` 按钮调用，支持测试环境注入自定义回调）。
        /// </summary>
        public static bool OpenDataSourceSettings(
            AcadDb.Database planDb = null,
            PipeDatasetContext datasetContext = null,
            Func<DataSourceSettingsViewModel, bool> customDialogLauncher = null)
        {
            AcadDb.Database targetDb = planDb ?? AcadApp.DocumentManager?.MdiActiveDocument?.Database;
            if (targetDb == null)
                return false;

            var session = DocumentSessionManager.GetOrCreateSession(targetDb);
            var ctx = datasetContext ?? session?.DatasetContext ?? new PipeDatasetContext();
            var dataSourceSvc = new DataSourceService();
            var vm = new DataSourceSettingsViewModel(targetDb, ctx, dataSourceSvc);

            if (customDialogLauncher != null)
            {
                return customDialogLauncher(vm);
            }

            if (ConfigManager.IsHeadlessOrSuppressedUi)
            {
                return vm.ConfirmAndSave();
            }

            var win = new DataSourceSettingsWindow(vm);
            try
            {
                win.SetOwner();
            }
            catch
            {
                // 忽略句柄绑定异常
            }

            bool? dialogResult = AcadApp.ShowModalWindow(win);
            return dialogResult == true || vm.DialogConfirmed;
        }

        /// <summary>
        /// 确保跨图核查所需的参考数据源就绪：
        /// 1. 先通过 <see cref="DataSourceService"/> 自动三级嗅探；
        /// 2. 若未能自动确定有效数据源，则按需弹出数据源配置窗口让用户确认或指定路径。
        /// </summary>
        public static bool EnsureDataSourcesReady(
            AcadDb.Database planDb,
            PipeDatasetContext datasetContext,
            bool needPreviousData,
            bool needSurveyData,
            out bool dialogShown,
            bool allowInteractiveDialog = true,
            Func<DataSourceSettingsViewModel, bool> customDialogLauncher = null)
        {
            dialogShown = false;
            if (planDb == null)
                return false;

            if (!needPreviousData && !needSurveyData)
                return true;

            var session = DocumentSessionManager.GetOrCreateSession(planDb);
            var ctx = datasetContext ?? session?.DatasetContext ?? new PipeDatasetContext();
            var dataSourceSvc = new DataSourceService();

            bool autoResolved = dataSourceSvc.TryAutoResolveDataSources(
                planDb,
                ctx,
                needPreviousData: needPreviousData,
                needSurveyData: needSurveyData);

            if (autoResolved)
            {
                dataSourceSvc.RememberDataSourcePaths(
                    needPreviousData ? ctx.PreviousDataSource : null,
                    needSurveyData ? ctx.SurveyDataSource : null,
                    saveImmediately: true);
                return true;
            }

            if (!allowInteractiveDialog && customDialogLauncher == null)
            {
                return false;
            }

            if (customDialogLauncher != null || !ConfigManager.IsHeadlessOrSuppressedUi)
            {
                dialogShown = true;
                bool confirmed = OpenDataSourceSettings(planDb, ctx, customDialogLauncher);
                if (!confirmed)
                    return false;
            }

            bool prevOk = !needPreviousData || (ctx.PreviousDataSource != null && ctx.PreviousDataSource.IsValid);
            bool surveyOk = !needSurveyData || (ctx.SurveyDataSource != null && ctx.SurveyDataSource.IsValid);
            return prevOk && surveyOk;
        }

        /// <summary>
        /// 根据规则 ID 创建对应的 <see cref="ICheckRule"/> 实例。
        /// </summary>
        public static ICheckRule CreateRuleInstance(
            string ruleId,
            AcadDb.Database db,
            Editor ed,
            bool suppressBlockingDialog = false)
        {
            if (db == null)
                return null;

            if (string.Equals(ruleId, PlanDataCheckService.DefaultRuleId, StringComparison.OrdinalIgnoreCase))
            {
                return new PlanDataCheckService(db, ed)
                {
                    SuppressBlockingDialog = suppressBlockingDialog
                };
            }

            if (string.Equals(ruleId, MatchGroupService.DefaultRuleId, StringComparison.OrdinalIgnoreCase))
            {
                return new MatchGroupService(db, ed)
                {
                    SuppressBlockingDialog = suppressBlockingDialog
                };
            }

            if (string.Equals(ruleId, FlowArrowRuleId, StringComparison.OrdinalIgnoreCase))
            {
                return new FlowArrowService(db, ed, useEditor: false)
                {
                    SuppressBlockingDialog = suppressBlockingDialog
                };
            }

            if (string.Equals(ruleId, MatchPipeService.DefaultRuleId, StringComparison.OrdinalIgnoreCase))
            {
                return new MatchPipeService(db, ed);
            }

            if (string.Equals(ruleId, CompareSegmentService.DefaultRuleId, StringComparison.OrdinalIgnoreCase))
            {
                var geometryFactory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
                return new CompareSegmentService(db, ed, geometryFactory);
            }

            return null;
        }

        /// <summary>
        /// 执行单项核查规则（支持单文档规则直接运行、跨图规则先自动嗅探再按需弹窗配置）。
        /// </summary>
        public static IReadOnlyList<CheckIssueItem> ExecuteSingleRule(
            AcadDb.Database db,
            Editor ed,
            string ruleId,
            out bool dialogShown,
            bool allowInteractiveDialog = true,
            bool showPalette = true,
            Func<DataSourceSettingsViewModel, bool> customDialogLauncher = null,
            ProgressContext progress = null)
        {
            dialogShown = false;
            if (db == null || string.IsNullOrWhiteSpace(ruleId))
                return Array.Empty<CheckIssueItem>();

            var session = DocumentSessionManager.GetOrCreateSession(db);
            var ctx = session?.DatasetContext ?? new PipeDatasetContext();

            CheckRuleOptionItem option = _ruleOptions.FirstOrDefault(r =>
                string.Equals(r.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));

            bool needPrev = option?.RequiresPreviousData ??
                            string.Equals(ruleId, CompareSegmentService.DefaultRuleId, StringComparison.OrdinalIgnoreCase);
            bool needSurvey = option?.RequiresSurveyData ??
                              string.Equals(ruleId, MatchPipeService.DefaultRuleId, StringComparison.OrdinalIgnoreCase);

            if (needPrev || needSurvey)
            {
                // 跨图规则前置拦截：先确认当前主图存在有效规划管线数据，无数据时只弹 1 个预检提示框并直接终止
                PreCheckSummary planPreCheck = PreCheckService.ValidateFullPipelinePreConditions(
                    db,
                    showDialogOnBlocking: allowInteractiveDialog && !ConfigManager.IsHeadlessOrSuppressedUi);

                if (planPreCheck.HasBlockingIssues)
                {
                    ed?.WriteMessage($"\n[{ruleId}] 当前图纸未检测到有效规划管线数据，已中止核查。\n");
                    return Array.Empty<CheckIssueItem>();
                }

                bool ready = EnsureDataSourcesReady(
                    db,
                    ctx,
                    needPreviousData: needPrev,
                    needSurveyData: needSurvey,
                    out dialogShown,
                    allowInteractiveDialog: allowInteractiveDialog,
                    customDialogLauncher: customDialogLauncher);

                if (!ready)
                {
                    ed?.WriteMessage($"\n[{ruleId}] 数据源未就绪，已跳过该跨图核查规则。\n");
                    return session?.GetAllIssueItems() ?? Array.Empty<CheckIssueItem>();
                }
            }

            ICheckRule rule = CreateRuleInstance(ruleId, db, ed, suppressBlockingDialog: false);
            if (rule == null)
                return session?.GetAllIssueItems() ?? Array.Empty<CheckIssueItem>();

            session?.RegisterActiveRule(rule);
            var ruleResult = rule.CheckAll(progress);
            if (ruleResult == null)
            {
                // 子规则因前置数据缺失返回 null 时直接终止
                return Array.Empty<CheckIssueItem>();
            }

            // FlowArrowService 的 CheckAll 仅返回字典，需显式同步合并至 session.IssuesById
            if (session != null &&
                string.Equals(rule.RuleId, FlowArrowRuleId, StringComparison.OrdinalIgnoreCase))
            {
                session.MergeRuleIssues(rule.RuleId, ruleResult, isIncremental: false);
            }

            var allIssues = session?.GetAllIssueItems() ??
                            (IReadOnlyList<CheckIssueItem>)(ruleResult.Values.ToList());

            if (showPalette && !ConfigManager.IsHeadlessOrSuppressedUi)
            {
                palCheckPipe.Instance.ShowIssueItems(allIssues);
            }

            return allIssues;
        }

        /// <summary>
        /// 执行一键全量综合核查（串联规划自检、图属文字配对、流向箭头、规划vs勘测管径匹配、规划vs上版覆盖与19项属性比对）。
        /// </summary>
        public static FullPipelineCheckSummary ExecuteFullPipelineCheck(
            AcadDb.Database db,
            Editor ed,
            bool allowInteractiveDialog = true,
            bool showPalette = true,
            Func<DataSourceSettingsViewModel, bool> customDialogLauncher = null,
            ProgressContext progress = null)
        {
            var summary = new FullPipelineCheckSummary();
            if (db == null)
                return summary;

            var sw = Stopwatch.StartNew();
            var session = DocumentSessionManager.GetOrCreateSession(db);
            var ctx = session?.DatasetContext ?? new PipeDatasetContext();

            // 0. 顶层统一前置预核查（Stage 6 Commit 6.4）：
            // 若当前主图未加载任何有效规划管线，仅弹出 1 个合并提示框并立即中止（Fail-Fast），杜绝连续弹出 3 个对话框
            PreCheckSummary preCheck = PreCheckService.ValidateFullPipelinePreConditions(
                db,
                showDialogOnBlocking: allowInteractiveDialog && !ConfigManager.IsHeadlessOrSuppressedUi);

            if (preCheck.HasBlockingIssues)
            {
                summary.PreCheckBlocked = true;
                sw.Stop();
                summary.ElapsedMilliseconds = sw.ElapsedMilliseconds;
                ed?.WriteMessage("\n[一键全量核查] 当前图纸未检测到合规的规划管线数据，已中止核查。\n");
                return summary;
            }

            // 1. 先执行跨图数据源三级智能嗅探，无法确定时按需弹出数据源设置窗口
            bool dataSourcesReady = EnsureDataSourcesReady(
                db,
                ctx,
                needPreviousData: true,
                needSurveyData: true,
                out bool dialogShown,
                allowInteractiveDialog: allowInteractiveDialog,
                customDialogLauncher: customDialogLauncher);

            summary.DataSourcesReady = dataSourcesReady;
            summary.DialogShown = dialogShown;
            summary.PreviousDataSource = ctx.PreviousDataSource;
            summary.SurveyDataSource = ctx.SurveyDataSource;

            // 2. 依次调度各核查规则并合并至当前文档会话（子规则启用 suppressBlockingDialog = true，避免批量中途重复弹窗）
            foreach (CheckRuleOptionItem opt in _ruleOptions)
            {
                if (opt.RequiresSurveyData && (ctx.SurveyDataSource == null || !ctx.SurveyDataSource.IsValid))
                {
                    ed?.WriteMessage($"\n[一键全量核查] 勘测数据源未就绪，已跳过 [{opt.DisplayName}]。");
                    continue;
                }

                if (opt.RequiresPreviousData && (ctx.PreviousDataSource == null || !ctx.PreviousDataSource.IsValid))
                {
                    ed?.WriteMessage($"\n[一键全量核查] 上版数据源未就绪，已跳过 [{opt.DisplayName}]。");
                    continue;
                }

                ICheckRule rule = CreateRuleInstance(opt.RuleId, db, ed, suppressBlockingDialog: true);
                if (rule == null)
                    continue;

                session?.RegisterActiveRule(rule);
                var ruleResult = rule.CheckAll(progress);

                if (session != null &&
                    string.Equals(rule.RuleId, FlowArrowRuleId, StringComparison.OrdinalIgnoreCase) &&
                    ruleResult != null)
                {
                    session.MergeRuleIssues(rule.RuleId, ruleResult, isIncremental: false);
                }

                summary.ExecutedRuleIds.Add(rule.RuleId);

                int ruleIssueCount = session != null
                    ? session.IssuesById.Values.Count(i =>
                        i != null && string.Equals(i.RuleId, rule.RuleId, StringComparison.OrdinalIgnoreCase))
                    : (ruleResult?.Count ?? 0);

                summary.IssueCountByRuleId[rule.RuleId] = ruleIssueCount;
            }

            summary.AllMergedIssues = session?.GetAllIssueItems() ?? Array.Empty<CheckIssueItem>();
            sw.Stop();
            summary.ElapsedMilliseconds = sw.ElapsedMilliseconds;

            ed?.WriteMessage(
                $"\n[一键全量核查] 执行完成（耗时 {summary.ElapsedMilliseconds} ms）：" +
                $"共运行 {summary.ExecutedRuleIds.Count} 项规则，累计检出核查条目 {summary.AllMergedIssues.Count} 项。\n");

            if (showPalette && !ConfigManager.IsHeadlessOrSuppressedUi)
            {
                palCheckPipe.Instance.ShowIssueItems(summary.AllMergedIssues);
            }

            return summary;
        }
    }
}
