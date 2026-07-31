using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using process_pipeline.Core;
using process_pipeline.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Cad.Plugin
{
    public class Test_Merge_Polylines : CadCommandBase
    {
        [CommandMethod("Test_Merge_Polylines")]
        public override void Execute()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            Database currentDb = doc.Database;

            /*
             * ============================================================
             * 1. 测试参数区
             * ============================================================
             * 
             * 你先直接在这里写死路径和图层名。
             * 等算法验证通过之后，再考虑做 UI、配置文件或命令行输入。
             */

            // A.dwg 文件路径：基准数据所在 DWG。
            string dwgPath = @"D:\资料\部门内部文件\小工具\管线CAD\提资\勘测管.dwg";

            // A.dwg 中的基准图层。
            string targetLayer = "勘测管";
            string outputLayer = "_勘测管_MERGED_CHECK";

            var geometryFactory = NetTopologySuite.NtsGeometryServices.Instance
                .CreateGeometryFactory();

            var mergeOptions = new PolylineMergeOptions
            {
                // 端点拓扑归并容差。
                // 这个值越大，越容易把端点相近但不完全重合的线合并。
                // 建议先从 0.01 或 0.001 开始测试。
                NodeTolerance = 0.01,

                // 按 WIDTH 分组合并。
                // WIDTH 不一致的 polyline 不会进入同一组，也就不会被合并。
                MergeAttributeNames = new List<string>
                {
                    "WIDTH"
                },

                // 是否要求同图层。
                // 当前只读取 B 图层，其实 true/false 影响不大。
                RequireSameLayer = true,

                // 是否合并闭环。
                MergeClosedLoops = true,

                // 移除拼接处重复点。
                RemoveDuplicateConsecutivePoints = true,

                // 过滤极短 polyline。
                MinPolylineLength = 0.001
            };

            using (Database sideDb = new Database(false, true))
            {
                sideDb.ReadDwgFile(
                    dwgPath,
                    FileOpenMode.OpenForReadAndAllShare,
                    true,
                    "");

                /*
                 * 释放文件输入流。
                 * 
                 * 对批量读取 DWG 很重要，否则可能导致文件一直被占用。
                 */
                sideDb.CloseInput(true);

                var rawPolylines = GeometryHelper.CollectPolylinesFromLayer(
                    sideDb,
                    targetLayer,
                    geometryFactory,
                    "target_raw");

                ed.WriteMessage($"B 原始 polyline 数量：{rawPolylines.Count}\n");

                var merger = new PolylineGraphMerger(geometryFactory);

                var mergedPolylines = merger.Merge(
                    rawPolylines,
                    mergeOptions);

                ed.WriteMessage($"B 合并后 polyline 数量：{mergedPolylines.Count}\n");

                int mergedFromMultipleCount = 0;

                foreach (var pl in mergedPolylines)
                {
                    if (pl.SourcePolylines != null && pl.SourcePolylines.Count > 1)
                        mergedFromMultipleCount++;
                }

                ed.WriteMessage($"其中由多条原始 polyline 合并而来的数量：{mergedFromMultipleCount}\n");

                ResultDrawer.DrawCadPolylines(
                    currentDb,
                    mergedPolylines,
                    outputLayer,
                    3);

                ed.WriteMessage($"\n拓扑合并检查完成，结果已绘制到图层：{outputLayer}");
            }
        }
    }
}
