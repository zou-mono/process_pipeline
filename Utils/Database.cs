using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using process_pipeline.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace process_pipeline.Utils
{
    public class Database : CadBase { 
        public ObjectId EnsureAuxLayer(string layerName)
        {
            using (Transaction tr = Db.TransactionManager.StartTransaction())
            {
                LayerTable lt = (LayerTable)tr.GetObject(Db.LayerTableId, OpenMode.ForWrite);

                //string layerName = "核查辅助线";

                if (lt.Has(layerName))
                {
                    return lt[layerName];
                    //return (LayerTableRecord)tr.GetObject(lt[layerName], OpenMode.ForRead);
                }

                // 创建新图层
                LayerTableRecord ltr = new LayerTableRecord();
                ltr.Name = layerName;
                ltr.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(ColorMethod.ByAci, 1); // 红色（可改）
                ltr.IsLocked = false;
                ltr.IsOff = false;
                ltr.IsFrozen = false;

                ObjectId layerId = lt.Add(ltr);
                tr.AddNewlyCreatedDBObject(ltr, true);

                tr.Commit();

                //Doc.Editor.WriteMessage($"\n已创建图层“{layerName}”（用于辅助检查线）。\n");

                return lt[layerName];
            }
        }    
    }
}
