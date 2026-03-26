using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using process_pipeline.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autodesk.AutoCAD.Geometry;
using System.Security.Cryptography;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using process_pipeline.Utils;
using process_pipeline.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using AcadDb = Autodesk.AutoCAD.DatabaseServices;

namespace process_pipeline.Commands
{
    public class ReversePolylineCommands : CadCommandBase
    {
        [CommandMethod("RevPL", CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public override void Execute()
        {
            var service = new ReversePolylineService(Doc.Database, Doc.Editor);
            service.Run("反转多段线");
        }
    }

    public class ReversePolylineService : CadBase
    {
        private List<ObjectId> _fixedIds = new List<ObjectId>();
        public ReversePolylineService(AcadDb.Database db, Editor ed) : base(db, ed)
        {

        }

        protected override void ExecuteVoid(ProgressContext context) { 
                    SelectionSet ss;

            using (new SysVarScope(new Dictionary<string, object>
            {
                { "SELECTIONEFFECT", 0 },
                { "PRESELECTIONEFFECT", 0 },
                { "SELECTIONEFFCOLOR", 5 },
            }))
            {
                // 1. 优先获取预选集（用户提前选中的对象）
                PromptSelectionResult impliedRes = Ed.SelectImplied();

                object selEff = AcadApp.GetSystemVariable("SELECTIONEFFECT");
                object preSelEff = AcadApp.GetSystemVariable("PRESELECTIONEFFECT");

                DbgLog.Write(Ed, $"[Inside using] SELECTIONEFFECT={selEff}, PRESELECTIONEFFECT={preSelEff}");

                //PromptSelectionResult impliedRes = Ed.GetSelection();
                if (impliedRes.Status == PromptStatus.OK && impliedRes.Value.Count > 0)
                {
                    // 有预选，直接使用
                    ss = impliedRes.Value;
                    Ed.WriteMessage($"\n检测到 {ss.Count} 个已选中对象，将直接反转...");
                }
                else
                {
                    //Ed.SelectionAdded += onAdded;
                    // 没有预选，让用户选择
                    PromptSelectionOptions opts = new PromptSelectionOptions
                    {
                        MessageForAdding = "\n请选择要反转的线(直线/多段线/圆弧/样条曲线等, 可多选）",
                        MessageForRemoval = "\n移除对象（Shift+点选）",
                        RejectObjectsOnLockedLayers = true,
                        AllowDuplicates = false
                    };

                    PromptSelectionResult res = Ed.GetSelection(opts, GetReverseFilter());
                    if (res.Status != PromptStatus.OK)
                    {
                        Ed.WriteMessage("\n*取消* 或未选中对象");
                        return;
                    }

                    ss = res.Value;
                }

                if (ss.Count == 0)
                {
                    Ed.WriteMessage("\n没有选到任何 Polyline 或 Line。");
                    return;
                }

                Ed.SetImpliedSelection(ss.GetObjectIds());
                //Ed.Regen();  // 强制刷新显示高亮

                int reversedCount = 0;

                using (Transaction tr = Db.TransactionManager.StartTransaction())
                //using (OpenCloseTransaction tr = Db.TransactionManager.StartOpenCloseTransaction())
                {
                    _fixedIds = new List<ObjectId>();

                    try
                    {
                        foreach (SelectedObject selObj in ss)
                        {
                            //Entity ent = tr.GetObject(selObj.ObjectId, OpenMode.ForWrite) as Entity;
                            //if (ent == null) continue;

                            Curve cv = (Curve)tr.GetObject(selObj.ObjectId, OpenMode.ForWrite);
                            if (cv == null) continue;

                            cv.ReverseCurve();   // 一行搞定
                            _fixedIds.Add(selObj.ObjectId);
                            reversedCount++;
                        }

                        tr.Commit();
                    }
                    catch { 
                        tr.Abort();
                    }
                }

                if (reversedCount > 0)
                {
                    // Commit 成功后再更新列表（Undo 时列表不会提前删）
                    if(palCheckArrow.Instance.IsVisible)
                        palCheckArrow.Instance?.MarkProblemFixed(_fixedIds, true);

                    Ed.WriteMessage($"\n已成功反转 {reversedCount} 个对象。");
                    Doc.Editor.Regen();  // 刷新显示，确保反转立即可见
                }
                else
                {
                    Ed.WriteMessage("\n选中的对象中没有可反转的 Polyline 或 Line。");
                }
            }
        }

        // 筛选器：只允许 Polyline 和 Line
        private SelectionFilter GetReverseFilter()
        {
            TypedValue[] tvs = new TypedValue[]
            {
                new TypedValue((int)DxfCode.Operator, "<OR"),
                    new TypedValue((int)DxfCode.Start, "LWPOLYLINE"),
                    new TypedValue((int)DxfCode.Start, "*POLYLINE"),
                    new TypedValue((int)DxfCode.Start, "LINE"),
                    new TypedValue((int)DxfCode.Start, "ARC"),
                    new TypedValue((int)DxfCode.Start, "ELLIPSE"),
                    new TypedValue((int)DxfCode.Start, "CIRCLE"),
                    new TypedValue((int)DxfCode.Start, "SPLINE"),
                    //new TypedValue((int)DxfCode.Start, "POLYLINE2D"),  // 旧式 2D Polyline
                new TypedValue((int)DxfCode.Operator, "OR>")
            };

            return new SelectionFilter(tvs);
        }
    }
}
