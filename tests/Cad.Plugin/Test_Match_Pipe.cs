using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Cad.Plugin;
using NetTopologySuite.Geometries;
using NLog;
using process_pipeline.Commands;
using process_pipeline.Core;
using process_pipeline.Geometry;
using process_pipeline.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace process_pipeline.Tests.Cad.Plugin
{
    public class Test_Match_pipe : CadCommandBase
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        [CommandMethod("test_match_pipe")]
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
            string dwgAPath = @"D:\资料\部门内部文件\小工具\管线CAD\提资\勘测管.dwg";

            // B.dwg 文件路径：待比较数据所在 DWG。
            string dwgBPath = @"D:\资料\部门内部文件\小工具\管线CAD\提资\盐田初始.dwg";

            // A.dwg 中的基准图层。
            string baseLayer = "勘测管";

            // B.dwg 中的待比较图层。
            string targetLayer = "3-污水管-现状";

            /*
             * ============================================================
             * 2. 正式执行
             * ============================================================
            */
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


            try
            {
                ed.WriteMessage("========== CADCOMPARE_2SIDEDB_TEST 开始 ==========\n");

                ed.WriteMessage($"base 文件：{dwgAPath}\n");
                ed.WriteMessage($"target 文件：{dwgBPath}\n");
                ed.WriteMessage($"base 图层：{baseLayer}\n");
                ed.WriteMessage($"target 图层：{targetLayer}\n");

                /*
                 * 从 A.dwg 的 Side Database 提取 layerA。
                 * 
                 * 注意：
                 * 这里 transform 传 Matrix3d.Identity。
                 * 也就是说，假设 A.dwg 和 B.dwg 的坐标本身已经在同一坐标系下。
                 */
                ed.WriteMessage("正在从 A.dwg 读取基准图层...\n");

                var geometryFactory = NetTopologySuite.NtsGeometryServices.Instance
                    .CreateGeometryFactory();

                /*
                 * 从 B.dwg 的 Side Database 提取 layerB。
                 */
                ed.WriteMessage("正在从 B.dwg 读取待比较图层...\n");

                List<CadPolyline> basePolylines = null;
                List<CadPolyline> targetPolylines = null;

                using (Database sideDb = new Database(false, true))
                {
                    sideDb.ReadDwgFile(
                        dwgAPath,
                        FileOpenMode.OpenForReadAndAllShare,
                        true,
                        "");

                    basePolylines =
                        GeometryHelper.CollectPolylinesFromLayer(
                            sideDb,
                            baseLayer,
                            geometryFactory,
                            "base_raw");

                    sideDb.CloseInput(true);
                }

                using (Database sideDb = new Database(false, true))
                {
                    sideDb.ReadDwgFile(
                        dwgBPath,
                        FileOpenMode.OpenForReadAndAllShare,
                        true,
                        "");

                    targetPolylines =
                        GeometryHelper.CollectPolylinesFromLayer(
                            sideDb,
                            targetLayer,
                            geometryFactory,
                            "target");

                     sideDb.CloseInput(true);
                }

                if (basePolylines == null)
                    basePolylines = new List<CadPolyline>();

                if (targetPolylines == null)
                    targetPolylines = new List<CadPolyline>();

                ed.WriteMessage($"base 原始 polyline 数量：{basePolylines.Count}\n");
                ed.WriteMessage($"target 原始 polyline 数量：{targetPolylines.Count}\n");

                // 核心过程：管道匹配
                List<CompareResult> results = MatchPipeService.PipeMatcher(
                    basePolylines,
                    targetPolylines,
                    geometryFactory,
                    null,
                    mergeOptions);

                int matchedCount = results.Count(x => x.IsMatched);
                int unmatchedCount = results.Count - matchedCount;

                foreach (var result in results)
                {
                    string targetHandles = result.TargetPolyline?.SourceHandles != null
                        ? string.Join(", ", result.TargetPolyline.SourceHandles)
                        : "";

                    string baseHandles = result.BestBasePolyline?.SourceHandles != null
                        ? string.Join(", ", result.BestBasePolyline.SourceHandles)
                        : "";

                    if (!result.IsMatched)
                    {
                        Logger.Debug(
                            $"B Handles=[{targetHandles}], 未找到匹配 A");
                        continue;
                    }

                    Logger.Debug(
                        $"B Handles=[{targetHandles}] -> " +
                        $"A Handles=[{baseHandles}], " +
                        $"Score={result.Score:F4}, " +
                        $"AvgDist={result.AverageDistance:F4}, " +
                        $"MaxDist={result.MaxDistance:F4}, " +
                        $"BadRatio={result.BadPointRatio:F2}, " +
                        $"target区间=[{result.CoveredIntervalOnTarget?.Start:F3}, {result.CoveredIntervalOnTarget?.End:F3}], " +
                        $"base区间=[{result.CoveredIntervalOnBase?.Start:F3}, {result.CoveredIntervalOnBase?.End:F3}]");
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("CADCOMPARE_2SIDEDB_TEST 执行异常：\n");
                ed.WriteMessage(ex.Message + "\n");
                ed.WriteMessage(ex.StackTrace + "\n");
            }
        }
    }
}

