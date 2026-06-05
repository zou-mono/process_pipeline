using System;
using Autodesk.AutoCAD.DatabaseServices;
using Cad.Tests.Shared;

namespace Cad.Plugin
{
    public static class DwgStatisticsService
    {
        public static LayerStatistics ReadAndCountByLayer(string dwgPath)
        {
            if (string.IsNullOrWhiteSpace(dwgPath))
                throw new ArgumentException("dwgPath 不能为空。", nameof(dwgPath));

            var db = new Database(false, true);
            db.ReadDwgFile(dwgPath, FileOpenMode.OpenForReadAndAllShare, true, "");

            var result = new LayerStatistics();

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null)
                        continue;

                    result.TotalCount++;

                    string layerName = string.IsNullOrWhiteSpace(ent.Layer) ? "0" : ent.Layer.Trim();

                    if (!result.LayerCountMap.ContainsKey(layerName))
                        result.LayerCountMap[layerName] = 0;

                    result.LayerCountMap[layerName]++;
                }

                tr.Commit();
            }

            return result;
        }
    }
}
