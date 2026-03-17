using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using process_pipeline.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace process_pipeline.Commands
{
    public class ClearLayerCommands : CadBase 
    {   
        [CommandMethod("ClearLayer", CommandFlags.Session | CommandFlags.Redraw)]
        public void Execute() {
            // 提示用户输入图层名称（支持通配符）
            PromptStringOptions pso = new PromptStringOptions("\n输入要清空的图层名称：")
            {
                AllowSpaces = true,
                DefaultValue = "TEMP"  // 默认值，可改
            };

            PromptResult pr = Ed.GetString(pso);
            if (pr.Status != PromptStatus.OK)
            {
                Ed.WriteMessage("\n*取消*");
                return;
            }

            string layerName = pr.StringResult.Trim();
            if (string.IsNullOrEmpty(layerName))
            {
                Ed.WriteMessage("\n图层名称不能为空。");
                return;
            }

            ClearLayer(layerName);

            Ed.WriteMessage($"\n已清空图层“{layerName}”上的所有实体\n");
            Doc.Editor.Regen();
        }

        public void ClearLayer(string layerName)
        {
            using (Transaction tr = Db.TransactionManager.StartTransaction())
            {
                LayerTable lt = (LayerTable)tr.GetObject(Db.LayerTableId, OpenMode.ForRead);
                if (!lt.Has(layerName))
                {
                    // 图层不存在，不需要清空
                    return;
                }

                LayerTableRecord ltr = (LayerTableRecord)tr.GetObject(lt[layerName], OpenMode.ForRead);

                // 查找该图层上的所有实体
                TypedValue[] filter = new TypedValue[]
                {
                    new TypedValue((int)DxfCode.LayerName, layerName)
                };

                PromptSelectionResult res = Ed.SelectAll(new SelectionFilter(filter));
                if (res.Status != PromptStatus.OK || res.Value.Count == 0)
                    return;

                using (Transaction trWrite = Db.TransactionManager.StartTransaction())
                {
                    foreach (SelectedObject obj in res.Value)
                    {
                        Entity ent = (Entity)trWrite.GetObject(obj.ObjectId, OpenMode.ForWrite);
                        ent.Erase();
                    }
                    trWrite.Commit();
                }

                tr.Commit();
            }
        }
    }
}
