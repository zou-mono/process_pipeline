using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using process_pipeline.Core;
using process_pipeline.Forms;
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

    public class CheckPipeService : CadBase<Dictionary<ObjectId, ProblemItem>>
    {
        private readonly AcadDb.Database _db;
        private readonly Editor _ed;

        public CheckPipeService(AcadDb.Database db, Editor ed) : base(db, ed)
        {
            _ed = ed;
            _db = db;
        }

        protected override Dictionary<ObjectId, ProblemItem> Execute(ProgressContext context) 
        {
            return new Dictionary<ObjectId, ProblemItem>();
        }

        protected override Dictionary<ObjectId, ProblemItem> Execute(ProgressContext context, List<ObjectId> pipeIds)
        {
            return new Dictionary<ObjectId, ProblemItem>();
        }

        protected override void OnSuccess(Dictionary<ObjectId, ProblemItem> result, bool bOnlyUpdate = false)
        {
            //if (result == null || result.Count == 0)
            //{
            //    Ed.WriteMessage("\n检查通过，无问题。\n");
            //    return;
            //}

            if (bOnlyUpdate)
            {
                palCheckPipe.Instance.UpdateData(result);
            }
            else { 
                // 弹窗展示
                palCheckPipe.Instance.Show(result);    
            }
        }
    }
}
