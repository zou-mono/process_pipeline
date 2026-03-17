using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using process_pipeline.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using process_pipeline.Util;
using Autodesk.AutoCAD.Geometry;
using System.Security.Cryptography;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using process_pipeline.Utils;

namespace process_pipeline.Commands
{
    public class ReversePolylineCommands : CadBase
    {
        [CommandMethod("RevPL", CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void Execute()
        {   
            // SELECTIONEFFECT=0, PRESELECTIONEFFECT=0，这样更醒目

            SelectionSet ss = null;

            using (new SysVarScope(new Dictionary<string, object>
            {
                { "SELECTIONEFFECT", 0 },
                { "PRESELECTIONEFFECT", 0 }
            }))
            {
                // 1. 优先获取预选集（用户提前选中的对象）
                PromptSelectionResult impliedRes = Ed.SelectImplied();

                object selEff = AcadApp.GetSystemVariable("SELECTIONEFFECT");
                object preSelEff = AcadApp.GetSystemVariable("PRESELECTIONEFFECT");

                DbgLog.Write(Ed, $"[Inside using] SELECTIONEFFECT={selEff}, PRESELECTIONEFFECT={preSelEff}");
                //var highlighted = new HashSet<ObjectId>();
                //SelectionAddedEventHandler onAdded = (s, e) =>
                //{
                //    using (var tr = Db.TransactionManager.StartOpenCloseTransaction())
                //    {
                //        for (int i = 0; i < e.AddedObjects.Count; i++)
                //        {
                //            var id = e.AddedObjects[i].ObjectId;
                //            if (highlighted.Contains(id)) continue;

                //            var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                //            ent?.Highlight();
                //            highlighted.Add(id);
                //        }
                //        tr.Commit();
                //    }
                //    Ed.UpdateScreen();
                //};

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
                        MessageForAdding = "\n选择要反转的 Polyline 或 Line（可多选）",
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

                    //finally { 
                    //    Ed.SelectionAdded -= onAdded;
                    //}
                }

                if (ss.Count == 0)
                {
                    Ed.WriteMessage("\n没有选到任何 Polyline 或 Line。");
                    return;
                }

                Ed.SetImpliedSelection(ss.GetObjectIds());
                Ed.Regen();  // 强制刷新显示高亮

                int reversedCount = 0;

                using (Transaction tr = Db.TransactionManager.StartTransaction())
                {
                    foreach (SelectedObject selObj in ss)
                    {
                        Entity ent = tr.GetObject(selObj.ObjectId, OpenMode.ForWrite) as Entity;
                        if (ent == null) continue;

                        if (Geometry.ReverseLineEntity(ent)) reversedCount++;
                    }

                    tr.Commit();
                }

                if (reversedCount > 0)
                {
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
                    new TypedValue((int)DxfCode.Start, "POLYLINE"),
                    new TypedValue((int)DxfCode.Start, "LINE"),
                    //new TypedValue((int)DxfCode.Start, "POLYLINE2D"),  // 旧式 2D Polyline
                new TypedValue((int)DxfCode.Operator, "OR>")
            };

            return new SelectionFilter(tvs);
        }
    }
}
