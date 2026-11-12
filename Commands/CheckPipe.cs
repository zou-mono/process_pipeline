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
        }

        protected override Dictionary<ObjectId, CheckIssueItem> Execute(ProgressContext context)
        {
            var result = _rule.CheckAll(context);
            var session = DocumentSessionManager.GetOrCreateSession(_db);
            if (session != null && result != null)
            {
                session.RegisterActiveRule(_rule);
                if (CheckPipelineCoordinator.IsDictionaryBasedRule(_rule.RuleId))
                {
                    return session.MergeRuleIssues(_rule.RuleId, result, isIncremental: false);
                }
            }
            return result;
        }

        protected override Dictionary<ObjectId, CheckIssueItem> Execute(ProgressContext context, List<ObjectId> pipeIds)
        {
            if (_rule == null)
                return new Dictionary<ObjectId, CheckIssueItem>();

            var session = DocumentSessionManager.GetOrCreateSession(_db);
            Dictionary<ObjectId, CheckIssueItem> sameRuleIssues;
            if (session != null)
            {
                sameRuleIssues = session.GetRuleIssueDictionary(_rule.RuleId);
            }
            else if (palCheckPipe.Instance.CurrentIssues != null)
            {
                sameRuleIssues = palCheckPipe.Instance.CurrentIssues
                    .Where(kvp => kvp.Value != null &&
                                  (string.IsNullOrWhiteSpace(kvp.Value.RuleId) ||
                                   string.Equals(kvp.Value.RuleId, _rule.RuleId, StringComparison.OrdinalIgnoreCase)))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            }
            else
            {
                sameRuleIssues = new Dictionary<ObjectId, CheckIssueItem>();
            }

            var result = _rule.CheckIncremental(pipeIds, sameRuleIssues, context);
            if (session != null && result != null)
            {
                if (CheckPipelineCoordinator.IsDictionaryBasedRule(_rule.RuleId))
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
                    palCheckPipe.Instance.ShowIssueItems(session.GetAllIssueItems(), _rule?.RuleId);
                }
                else
                {
                    palCheckPipe.Instance.Show(result);
                }
            }
        }
    }
}
