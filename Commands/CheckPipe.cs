using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using process_pipeline.Core;
using process_pipeline.Forms;
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
    public class CheckPipeCommands : CadCommandBase
    {
        [CommandMethod("CHECKPIPE", CommandFlags.Modal | CommandFlags.NoUndoMarker)]
        public override void Execute()
        {
            if (Doc == null) return;

            // Stage 6 Commit 6.3：通过 CheckPipelineCoordinator 统一执行三级数据源智能嗅探、
            // 按需弹出数据源设置窗口，并一键调度全量五项核查规则汇聚展示至 ucMainCheckWindow。
            CheckPipelineCoordinator.ExecuteFullPipelineCheck(
                Doc.Database,
                Doc.Editor,
                allowInteractiveDialog: true,
                showPalette: true);
        }
    }

    /// <summary>
    /// 管线综合核查服务（Commit 2.3 & Stage 6 Commit 6.3）。
    /// 
    /// 架构设计说明：
    /// 1. 升级泛型返回值为 Dictionary<ObjectId, CheckIssueItem>，直接对接统一领域模型。
    /// 2. 内部通过统一的 ICheckRule 接口调度核查规则，并完美保留 DocumentCheckSession.IssuesById
    ///    中的多折线段子问题（SubIndex）与 ObjectId.Null 跨图缺失管段问题。
    /// </summary>
    public class CheckPipeService : CadBase<Dictionary<ObjectId, CheckIssueItem>>
    {
        private readonly AcadDb.Database _db;
        private readonly Editor _ed;
        private readonly ICheckRule _rule;

        public CheckPipeService(AcadDb.Database db, Editor ed, ICheckRule rule = null) : base(db, ed)
        {
            _ed = ed;
            _db = db;
            _rule = rule ?? new FlowArrowService(db, ed, false);

            var session = DocumentSessionManager.GetOrCreateSession(db);
            if (session != null)
            {
                session.RegisterActiveRule(_rule);
            }
        }

        protected override Dictionary<ObjectId, CheckIssueItem> Execute(ProgressContext context)
        {
            var result = _rule.CheckAll(context);
            var session = DocumentSessionManager.GetOrCreateSession(_db);
            if (session != null && result != null)
            {
                // FlowArrowService 仅返回按 ObjectId 组织的字典，需同步合并至 session.IssuesById；
                // 其他规则（PlanDataCheck / MatchGroup / MatchPipe / CompareSegment）已在 CheckAll 内部直接调用 MergeRuleIssueItems。
                if (string.Equals(_rule.RuleId, CheckPipelineCoordinator.FlowArrowRuleId, StringComparison.OrdinalIgnoreCase))
                {
                    return session.MergeRuleIssues(_rule.RuleId, result, isIncremental: false);
                }
            }
            return result;
        }

        protected override Dictionary<ObjectId, CheckIssueItem> Execute(ProgressContext context, List<ObjectId> pipeIds)
        {
            var session = DocumentSessionManager.GetOrCreateSession(_db);
            var currentIssues = session?.CurrentIssues != null && session.CurrentIssues.Count > 0
                ? new Dictionary<ObjectId, CheckIssueItem>(session.CurrentIssues)
                : (palCheckPipe.Instance.CurrentIssues != null
                    ? palCheckPipe.Instance.CurrentIssues.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                    : new Dictionary<ObjectId, CheckIssueItem>());

            var result = _rule.CheckIncremental(pipeIds, currentIssues, context);
            if (session != null && result != null)
            {
                if (string.Equals(_rule.RuleId, CheckPipelineCoordinator.FlowArrowRuleId, StringComparison.OrdinalIgnoreCase))
                {
                    return session.MergeRuleIssues(_rule.RuleId, result, isIncremental: true);
                }
            }
            return result;
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
                    palCheckPipe.Instance.UpdateIncrementalData(result);
                }
            }
            else
            {
                if (session != null && session.IssuesById.Count > 0)
                {
                    palCheckPipe.Instance.ShowIssueItems(session.GetAllIssueItems());
                }
                else
                {
                    palCheckPipe.Instance.Show(result);
                }
            }
        }
    }
}
