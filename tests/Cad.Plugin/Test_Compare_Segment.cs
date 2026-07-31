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
using process_pipeline.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace process_pipeline.Tests.Cad.Plugin
{
    public class Test_Compare_Segment : CadCommandBase
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        [CommandMethod("test_compare_segment")]
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
            string layerA = "勘测管";

            // B.dwg 中的待比较图层。
            string layerB = "3-污水管-现状";

            // 判断容差，单位与 CAD 图纸单位一致。
            // 如果图纸单位是米，0.001 就是 1mm。
            // 如果图纸单位是毫米，0.001 就是 0.001mm。
            double tolerance = 0.1;

            // 弧线离散最大段长。
            // 如果数据里没有 bulge 弧段，这个参数影响不大。
            // 如果有弧线，值越小，离散越密，结果越接近原弧线，但线段数量越多。
            double arcMaxSegmentLength = 1.0;

            /*
             * ============================================================
             * 2. 正式执行
             * ============================================================
             */

            try
            {
                ed.WriteMessage("========== CADCOMPARE_2SIDEDB_TEST 开始 ==========\n");

                ed.WriteMessage($"A 文件：{dwgAPath}\n");
                ed.WriteMessage($"B 文件：{dwgBPath}\n");
                ed.WriteMessage($"A 图层：{layerA}\n");
                ed.WriteMessage($"B 图层：{layerB}\n");
                ed.WriteMessage($"容差：{tolerance}\n");
                ed.WriteMessage($"弧线离散最大段长：{arcMaxSegmentLength}\n");

                var geometryFactory = new GeometryFactory();

                /*
                 * 从 A.dwg 的 Side Database 提取 layerA。
                 * 
                 * 注意：
                 * 这里 transform 传 Matrix3d.Identity。
                 * 也就是说，假设 A.dwg 和 B.dwg 的坐标本身已经在同一坐标系下。
                 */
                ed.WriteMessage("正在从 A.dwg 读取基准图层...\n");

                List<CadSegment> segmentsA = ReadSegmentsFromSideDatabase(
                    dwgAPath,
                    layerA,
                    "A",
                    geometryFactory,
                    arcMaxSegmentLength,
                    Matrix3d.Identity,
                    ed);

                ed.WriteMessage($"A 提取线段数量：{segmentsA.Count}\n");

                /*
                 * 从 B.dwg 的 Side Database 提取 layerB。
                 */
                ed.WriteMessage("正在从 B.dwg 读取待比较图层...\n");

                List<CadSegment> segmentsB = ReadSegmentsFromSideDatabase(
                    dwgBPath,
                    layerB,
                    "B",
                    geometryFactory,
                    arcMaxSegmentLength,
                    Matrix3d.Identity,
                    ed);

                ed.WriteMessage($"B 提取线段数量：{segmentsB.Count}\n");

                if (segmentsA.Count == 0)
                {
                    ed.WriteMessage("A 没有提取到任何线段，请检查 A 文件路径和 layerA 图层名。\n");
                    return;
                }

                if (segmentsB.Count == 0)
                {
                    ed.WriteMessage("B 没有提取到任何线段，请检查 B 文件路径和 layerB 图层名。\n");
                    return;
                }

                /*
                 * 执行比较。
                 */
                var compareService = new CompareSegmentService(currentDb, ed, geometryFactory);

                /*
                 * Compare(A, B)
                 * 含义：
                 * 以 A 为基准，看 A 中哪些部分没有被 B 覆盖。
                 */
                ed.WriteMessage("正在执行 Compare(A, B)：检查 A 有、B 没有的部分...\n");

                var compareRes_A_to_B = compareService.CompareCoverage(
                    segmentsA,
                    segmentsB,
                    tolerance);

                /*
                 * Compare(B, A)
                 * 含义：
                 * 反向比较，看 B 中哪些部分没有被 A 覆盖。
                 * 这可以理解为 B 相对于 A 的多余部分。
                 */
                ed.WriteMessage("正在执行 Compare(B, A)：检查 B 有、A 没有的部分...\n");

                var compareRes_B_to_A = compareService.CompareCoverage(
                    segmentsB,
                    segmentsA,
                    tolerance);

                ed.WriteMessage($"A 有、B 没有的线段数量：{compareRes_A_to_B.MissingSegments.Count}\n");
                ed.WriteMessage($"B 有、A 没有的线段数量：{compareRes_B_to_A.MissingSegments.Count}\n");

                /*
                 * 把结果画到当前打开的 CAD 图中。
                 * 
                 * 红色：
                 *     A 有、B 没有。
                 * 
                 * 蓝色：
                 *     B 有、A 没有。
                 */
                using (Transaction tr = currentDb.TransactionManager.StartTransaction())
                {
                    ResultDrawer.DrawSegments(
                        currentDb,
                        tr,
                        compareRes_A_to_B.CoveredSegments,
                        "_OVERLAP_A_TO_B",
                        2); // 黄色
                    ResultDrawer.DrawSegments(
                        currentDb,
                        tr,
                        compareRes_A_to_B.MissingSegments,
                        "_CADCOMPARE_A_NOT_IN_B",
                        1); // 红色

                    ResultDrawer.DrawSegments(
                        currentDb,
                        tr,
                        compareRes_B_to_A.MissingSegments,
                        "_CADCOMPARE_B_NOT_IN_A",
                        5); // 蓝色

                    tr.Commit();
                }

                ed.WriteMessage("\n\n结果已经绘制到当前图：");
                ed.WriteMessage("\n红色图层 _CADCOMPARE_A_NOT_IN_B：A 有、B 没有");
                ed.WriteMessage("\n蓝色图层 _CADCOMPARE_B_NOT_IN_A：B 有、A 没有");
                ed.WriteMessage("\n黄色图层 _OVERLAP_A_TO_B：A和B重叠");


                ed.WriteMessage("========== CADCOMPARE_2SIDEDB_TEST 完成 ==========\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("CADCOMPARE_2SIDEDB_TEST 执行异常：\n");
                ed.WriteMessage(ex.Message + "\n");
                ed.WriteMessage(ex.StackTrace + "\n");
            }
        }

        /// <summary>
        /// 从 Side Database 读取某个 DWG 文件中的指定图层，并提取为 CadSegment 集合。
        /// 
        /// 参数说明：
        /// dwgPath:
        ///     要读取的 DWG 文件路径。
        /// 
        /// targetLayer:
        ///     要提取的图层名。
        /// 
        /// datasetName:
        ///     数据集名称，例如 A 或 B。
        /// 
        /// geometryFactory:
        ///     NetTopologySuite 的 GeometryFactory。
        /// 
        /// arcMaxSegmentLength:
        ///     弧线离散最大段长。
        /// 
        /// transform:
        ///     坐标变换矩阵。
        ///     当前测试脚本默认传 Matrix3d.Identity。
        /// 
        /// ed:
        ///     AutoCAD Editor，用于输出调试信息。
        /// </summary>
        private static List<CadSegment> ReadSegmentsFromSideDatabase(
            string dwgPath,
            string targetLayer,
            string datasetName,
            GeometryFactory geometryFactory,
            double arcMaxSegmentLength,
            Matrix3d transform,
            Editor ed)
        {
            var segments = new List<CadSegment>();

            /*
             * Side Database 的标准读取方式：
             * 
             * new Database(false, true)
             *     false 表示不创建默认图形；
             *     true 表示 noDocument，适合 side database。
             */
            using (Database sideDb = new Database(false, true))
            {
                /*
                 * ReadDwgFile 参数说明：
                 * 
                 * dwgPath:
                 *     DWG 文件路径。
                 * 
                 * FileOpenMode.OpenForReadAndAllShare:
                 *     只读打开，并允许其他进程共享访问。
                 * 
                 * true:
                 *     allowCPConversion，允许代码页转换。
                 * 
                 * "":
                 *     密码，普通 DWG 留空。
                 */
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

                /*
                 * 从 Side Database 的 ModelSpace 中提取指定图层。
                 * 
                 * 注意：
                 * isFromSideDatabase = true
                 */
                segments = CadSegmentExtractionService.ExtractSegmentsFromDatabaseModelSpace(
                    sideDb,
                    targetLayer,
                    datasetName,
                    geometryFactory,
                    arcMaxSegmentLength,
                    transform,
                    dwgPath,
                    true);
            }

            ed.WriteMessage($"文件 {dwgPath} 中图层 {targetLayer} 提取完成，线段数量：{segments.Count}\n");

            return segments;
        }
    }
}
