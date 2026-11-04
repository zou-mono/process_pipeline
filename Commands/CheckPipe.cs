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

            // 1. 直接 new 服务，填参数
            var service = new CheckPipeService(Doc.Database, Doc.Editor)
            {

            };

            // 2. 启动引擎！
            service.Run(Properties.Settings.Default.taskFlowArrow, false);
        }
    }

    /// <summary>
    /// 管线综合核查服务（Commit 2.3）。
    /// 
    /// 架构设计说明：
    /// 1. 升级泛型返回值为 Dictionary<ObjectId, CheckIssueItem>，直接对接统一领域模型。
    /// 2. 内部通过统一的 ICheckRule 接口调度核查规则（默认挂载 FlowArrowService，后续可扩展多规则聚合），
    ///    实现命令执行引擎与具体算法实现的解耦。
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
                session.ActiveRule = _rule;
            }
        }

        protected override Dictionary<ObjectId, CheckIssueItem> Execute(ProgressContext context)
        {
            var result = _rule.CheckAll(context);
            var session = DocumentSessionManager.GetOrCreateSession(_db);
            if (session != null && result != null)
            {
                session.CurrentIssues = new Dictionary<ObjectId, CheckIssueItem>(result);
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
                session.CurrentIssues = new Dictionary<ObjectId, CheckIssueItem>(result);
            }
            return result;
        }

        protected override void OnSuccess(Dictionary<ObjectId, CheckIssueItem> result, bool bOnlyUpdate = false)
        {
            if (bOnlyUpdate)
            {
                palCheckPipe.Instance.UpdateIncrementalData(result);
            }
            else
            {
                // 弹窗展示全量核查结果
                palCheckPipe.Instance.Show(result);
            }
        }
    }
}
