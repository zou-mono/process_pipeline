using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using process_pipeline.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AcadDb = Autodesk.AutoCAD.DatabaseServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace process_pipeline.Utils
{
    public class Database {
        Document Doc = AcadApp.DocumentManager.MdiActiveDocument;
        AcadDb.Database Db = AcadApp.DocumentManager.MdiActiveDocument.Database;

        public ObjectId EnsureAuxLayer(string layerName)
        {
            using (Transaction tr = Db.TransactionManager.StartTransaction())
            {
                LayerTable lt = (LayerTable)tr.GetObject(Db.LayerTableId, OpenMode.ForWrite);

                //string layerName = "核查辅助线";

                if (lt.Has(layerName))
                {
                    return lt[layerName];
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

                return lt[layerName];
            }
        }    
    }
}
