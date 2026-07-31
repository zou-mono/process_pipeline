using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using process_pipeline.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Cad.Plugin
{
    /// <summary>
    /// 将比较结果画回 CAD。
    /// 
    /// 为了便于调试：
    /// 1. A 中 B 未覆盖的部分画成红色；
    /// 2. B 中 A 没有的多余部分画成蓝色。
    /// 
    /// 这些结果会被画到指定图层上。
    /// </summary>
    public static class ResultDrawer
    {
        /// <summary>
        /// 绘制一组 CadSegment 到 ModelSpace。
        /// </summary>
        public static void DrawSegments(
            Database db,
            Transaction tr,
            IEnumerable<CadSegment> segments,
            string layerName,
            short colorIndex)
        {
            EnsureLayer(db, tr, layerName, colorIndex);

            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord modelSpace =
                (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            foreach (var seg in segments)
            {
                var pl = new Polyline();
                pl.AddVertexAt(0, new Point2d(seg.Start.X, seg.Start.Y), 0, 0, 0);
                pl.AddVertexAt(1, new Point2d(seg.End.X, seg.End.Y), 0, 0, 0);
                //var line = new Line(
                //    new Point3d(seg.Start.X, seg.Start.Y, 0),
                //    new Point3d(seg.End.X, seg.End.Y, 0));
                pl.Closed = false;
                pl.Layer = layerName;
                pl.Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex);

                modelSpace.AppendEntity(pl);
                tr.AddNewlyCreatedDBObject(pl, true);
            }
        }

        /// <summary>
        /// 将 CadPolyline 集合绘制回 CAD。
        /// 
        /// 每个 CadPolyline 会被绘制成一个 AutoCAD Polyline。
        /// </summary>
        /// <param name="db">AutoCAD Database。</param>
        /// <param name="tr">当前 Transaction。</param>
        /// <param name="polylines">要绘制的 CadPolyline 集合。</param>
        /// <param name="layerName">输出图层名。</param>
        /// <param name="colorIndex">ACI 颜色索引。例如 1 红色，2 黄色，3 绿色。</param>
        public static void DrawCadPolylines(
            Database db,
            IEnumerable<CadPolyline> polylines,
            string layerName,
            short colorIndex)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {

                EnsureLayer(db, tr, layerName, colorIndex);

                BlockTable bt = (BlockTable)tr.GetObject(
                    db.BlockTableId,
                    OpenMode.ForRead);

                BlockTableRecord modelSpace =
                    (BlockTableRecord)tr.GetObject(
                        bt[BlockTableRecord.ModelSpace],
                        OpenMode.ForWrite);

                foreach (var cadPolyline in polylines)
                {
                    if (cadPolyline == null ||
                        cadPolyline.Geometry == null ||
                        cadPolyline.Geometry.NumPoints < 2)
                    {
                        continue;
                    }

                    var coords = cadPolyline.Geometry.Coordinates;

                    var pl = new Polyline();

                    for (int i = 0; i < coords.Length; i++)
                    {
                        var c = coords[i];

                        pl.AddVertexAt(
                            i,
                            new Point2d(c.X, c.Y),
                            0,
                            0,
                            0);
                    }

                    pl.Layer = layerName;
                    pl.Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex);

                    // 如果首尾点相同，设置 Closed。
                    if (coords.Length >= 3 && coords[0].Equals2D(coords[coords.Length - 1]))
                    {
                        pl.Closed = true;
                    }

                    modelSpace.AppendEntity(pl);
                    tr.AddNewlyCreatedDBObject(pl, true);
                }

                tr.Commit();
            }
        }

        /// <summary>
        /// 确保指定图层存在。
        /// 如果不存在，则创建。
        /// </summary>
        private static void EnsureLayer(
            Database db,
            Transaction tr,
            string layerName,
            short colorIndex)
        {
            LayerTable layerTable =
                (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);

            if (layerTable.Has(layerName))
                return;

            layerTable.UpgradeOpen();

            var layerRecord = new LayerTableRecord
            {
                Name = layerName,
                Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex)
            };

            layerTable.Add(layerRecord);
            tr.AddNewlyCreatedDBObject(layerRecord, true);
        }
    }
}
