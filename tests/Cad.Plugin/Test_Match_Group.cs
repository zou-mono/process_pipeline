using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using NLog;
using process_pipeline.Core;
using process_pipeline.Geometry;

namespace Cad.Plugin
{
    /// <summary>
    /// 管线与属性块空间就近配对及 CAD Group 合法性核查无 UI 自动化测试（Stage 3 Commit 3.1）。
    /// 
    /// 覆盖场景：
    /// 1. 读取 Data\上版数据.dwg（317 条多段线 + 317 个 ws 属性块，GroupDictionary.Count == 0）：
    ///    - 验证双距离判据（外包框中心距离 + 曲线距离 + LEN 管长校验）对 317 条管线（含 95 条块距曲线 5m~159.7m 的大弯折管线）的 100% 配对能力；
    ///    - 验证配对提取出的 16+ 项业务属性字段完整性；
    ///    - 验证近距离并行管线歧义候选（PossibleMatches）检出能力。
    /// 2. 读取 Data\勘测管.dwg（5,823 条勘测管多段线 + 5,823 个 1 属性块，GroupDictionary.Count == 5823）：
    ///    - 模式 A（启用原生 Group 读取）：验证 5,823 个既有 CAD Group 全部 1:1 合法通过；
    ///    - 模式 B（忽略原生 Group 模拟未成组状态）：验证纯空间邻近算法对 5,823 条勘测管的配对准确率与 WIDTH/HEITH 属性提取。
    /// </summary>
    public class Test_Match_Group : CadCommandBase
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        [CommandMethod("test_match_group")]
        public override void Execute()
        {
            Logger.Info("========== 开始执行 [test_match_group] 管线-属性块空间配对与 Group 核查测试 ==========");

            string prevDwgPath = @"D:\Codes\Tools\process_pipeline\Data\上版数据.dwg";
            string surveyDwgPath = @"D:\Codes\Tools\process_pipeline\Data\勘测管.dwg";

            int passedChecks = 0;
            int failedChecks = 0;

            void AssertTrue(bool condition, string checkName)
            {
                if (condition)
                {
                    passedChecks++;
                    Logger.Info($"  [PASS] {checkName}");
                }
                else
                {
                    failedChecks++;
                    Logger.Error($"  [FAIL] {checkName}");
                }
            }

            var matcher = new PipeSpatialMatcher();

            // ====================================================================
            // 场景 1：测试 Data\上版数据.dwg（317 条管线 + 317 个 ws 块，无原生 Group）
            // ====================================================================
            Logger.Info($"--- 场景 1：测试上版数据空间双判据配对 ({prevDwgPath}) ---");
            AssertTrue(File.Exists(prevDwgPath), $"上版数据文件存在: {prevDwgPath}");

            if (File.Exists(prevDwgPath))
            {
                var prevPipeLayers = new[]
                {
                    "0污水-待补测现状管",
                    "0污水-规划新建",
                    "0污水-规划改建",
                    "0污水-已核实需新增"
                };
                var prevAttrLayers = new[] { "0污水-属性" };

                using (var prevDb = new Database(false, true))
                {
                    prevDb.ReadDwgFile(prevDwgPath, FileOpenMode.OpenForReadAndAllShare, true, "");
                    prevDb.CloseInput(true);

                    PipeSpatialMatchSummary prevSummary = matcher.MatchDatabaseBlockAttributes(
                        prevDb,
                        prevPipeLayers,
                        prevAttrLayers,
                        PipeSpatialMatchOptions.FromConfig());

                    Logger.Info(
                        $"  [统计] 上版管线总数={prevSummary.TotalPipeCount}, ws属性块总数={prevSummary.TotalAttachmentCount}, " +
                        $"原生Group数={prevSummary.TotalExistingGroupCount}, 唯一配对={prevSummary.UniqueMatches.Count}, " +
                        $"近距歧义候选={prevSummary.ConflictMatches.Count}, 未配对管线={prevSummary.UnmatchedPipes.Count}, " +
                        $"孤立属性块={prevSummary.OrphanAttachments.Count}");

                    AssertTrue(prevSummary.TotalPipeCount == 317,
                        $"上版数据四图层共读取到 317 条管线 (实际={prevSummary.TotalPipeCount})");
                    AssertTrue(prevSummary.TotalAttachmentCount == 317,
                        $"上版数据 '0污水-属性' 图层共读取到 317 个 ws 属性块 (实际={prevSummary.TotalAttachmentCount})");
                    AssertTrue(prevSummary.TotalExistingGroupCount == 0,
                        $"上版数据原始 GroupDictionary 数量为 0 (实际={prevSummary.TotalExistingGroupCount})");

                    int totalMatchedPrev = prevSummary.UniqueMatches.Count + prevSummary.ConflictMatches.Count;
                    AssertTrue(totalMatchedPrev == 317 && prevSummary.UnmatchedPipes.Count == 0 && prevSummary.OrphanAttachments.Count == 0,
                        $"317 条上版管线与 317 个 ws 块完成 100% 空间配对，0 遗漏、0 孤立块 (配对总数={totalMatchedPrev})");

                    // 统计大弯折管线（GIS 导出块距曲线 > 5.0m，最大达 159.7m）是否全部通过外包框中心判据成功配对，
                    // 以及 3 条人工新增管线（0污水-已核实需新增，距曲线 4.2m~8.9m）是否通过曲线邻近判据成功配对
                    var allMatchedItems = prevSummary.UniqueMatches.Concat(prevSummary.ConflictMatches).ToList();
                    var gisLargeBendItems = allMatchedItems
                        .Where(m => m.Channel == SpatialMatchChannel.ExtentsCenterMatch && m.DistanceToCurve > 5.0)
                        .ToList();
                    double maxDistToCurve = allMatchedItems.Max(m => m.DistanceToCurve);
                    Logger.Info(
                        $"  [大弯折与分渠道统计] GIS大弯折管线(距曲线>5m)={gisLargeBendItems.Count} 条 " +
                        $"(最大距曲线={maxDistToCurve:F2}m, 外包框中心命中={prevSummary.ExtentsCenterMatchedCount}, 曲线邻近命中={prevSummary.CurveProximityMatchedCount})");

                    AssertTrue(gisLargeBendItems.Count >= 90 && gisLargeBendItems.All(m => m.DistanceToExtentsCenter <= 1.5),
                        $"GIS 大弯折管线 ({gisLargeBendItems.Count} 条, 最大距曲线 {maxDistToCurve:F1}m) 全部通过外包框中心判据精准捕获");

                    // 验证属性字典字段完整性（WIDTH, HEITH, QSBG, MDBG, PD, LEN, GHZT, LX 等）
                    int validAttrCount = prevSummary.MatchedAttributesByPipeHandle.Values
                        .Count(attrs => attrs.ContainsKey("WIDTH") && attrs.ContainsKey("LEN") && attrs.ContainsKey("GHZT") && attrs.ContainsKey("LX"));
                    AssertTrue(validAttrCount == 317,
                        $"全部 317 条上版管线均已成功挂载 WIDTH/LEN/GHZT/LX 等核心业务属性 (实际={validAttrCount})");

                    if (prevSummary.ConflictMatches.Count > 0)
                    {
                        foreach (var conflict in prevSummary.ConflictMatches.Take(5))
                        {
                            Logger.Info(
                                $"  [歧义明细] 管线[{conflict.Pipe.Handle}] <-> 首选块[{conflict.MatchedAttachment.Handle}], " +
                                $"候选数={conflict.PossibleMatches.Count}, 说明: {conflict.Description}");
                        }
                    }

                    // 生成可视化核验图纸：Data\上版数据_配对结果预览.dwg
                    string visualOutputDwgPath = @"D:\Codes\Tools\process_pipeline\Data\上版数据_配对结果预览.dwg";
                    int drawnLinkCount = GenerateVisualCheckDwg(prevDwgPath, visualOutputDwgPath, prevSummary);
                    AssertTrue(File.Exists(visualOutputDwgPath) && drawnLinkCount == 317,
                        $"已生成可视化配对连线图纸 [{visualOutputDwgPath}]，共绘制 {drawnLinkCount} 组醒目配对连线与标记");
                }
            }

            // ====================================================================
            // 场景 2：测试 Data\勘测管.dwg（5,823 条管线 + 5,823 个 1 块，含 5,823 个原生 Group）
            // ====================================================================
            Logger.Info($"--- 场景 2：测试勘测管数据 Group 合法性校验与无 Group 空间配对 ({surveyDwgPath}) ---");
            AssertTrue(File.Exists(surveyDwgPath), $"勘测管数据文件存在: {surveyDwgPath}");

            if (File.Exists(surveyDwgPath))
            {
                var surveyLayers = new[] { "勘测管" };

                using (var surveyDb = new Database(false, true))
                {
                    surveyDb.ReadDwgFile(surveyDwgPath, FileOpenMode.OpenForReadAndAllShare, true, "");
                    surveyDb.CloseInput(true);

                    // 2A：验证既有 5,823 个 CAD Group 的 1:1 合法性
                    var groupCheckOpts = PipeSpatialMatchOptions.FromConfig();
                    groupCheckOpts.UseExistingCadGroups = true;

                    PipeSpatialMatchSummary groupSummary = matcher.MatchDatabaseBlockAttributes(
                        surveyDb,
                        surveyLayers,
                        surveyLayers,
                        groupCheckOpts);

                    Logger.Info(
                        $"  [模式 A - 原生 Group 校验] 管线总数={groupSummary.TotalPipeCount}, 属性块总数={groupSummary.TotalAttachmentCount}, " +
                        $"合法 1:1 Group 数={groupSummary.ValidGroupPairCount}, 异常 Group 数={groupSummary.InvalidGroupCount}");

                    AssertTrue(groupSummary.TotalPipeCount == 5823 && groupSummary.TotalAttachmentCount == 5823,
                        $"勘测管图纸共读取到 5823 条管线与 5823 个属性块 (管线={groupSummary.TotalPipeCount}, 块={groupSummary.TotalAttachmentCount})");
                    AssertTrue(groupSummary.ValidGroupPairCount == 5823 && groupSummary.InvalidGroupCount == 0,
                        $"勘测管全部 5823 个原生 CAD Group 均通过 1:1 合法性核查 (合法={groupSummary.ValidGroupPairCount}, 异常={groupSummary.InvalidGroupCount})");

                    int boxCulvertCount = groupSummary.MatchedAttributesByPipeHandle.Values
                        .Count(attrs => attrs.TryGetValue("HEITH", out object h) && h != null && h.ToString().Trim() != "0" && h.ToString().Trim() != "");
                    AssertTrue(boxCulvertCount == 128,
                        $"准确识别勘测管中 HEITH != '0' 的渠箱记录共 128 条 (实际={boxCulvertCount})");

                    // 2B：忽略原生 Group，模拟未成组状态下对 5,823 条勘测管的纯空间配对
                    var spatialOnlyOpts = PipeSpatialMatchOptions.FromConfig();
                    spatialOnlyOpts.UseExistingCadGroups = false;
                    spatialOnlyOpts.UseXDataLinks = false;

                    PipeSpatialMatchSummary spatialSummary = matcher.MatchDatabaseBlockAttributes(
                        surveyDb,
                        surveyLayers,
                        surveyLayers,
                        spatialOnlyOpts);

                    int totalSpatialMatched = spatialSummary.UniqueMatches.Count + spatialSummary.ConflictMatches.Count;
                    Logger.Info(
                        $"  [模式 B - 模拟未成组纯空间配对] 配对成功={totalSpatialMatched}/5823 " +
                        $"(唯一={spatialSummary.UniqueMatches.Count}, 邻近歧义={spatialSummary.ConflictMatches.Count}, 未匹配={spatialSummary.UnmatchedPipes.Count})");

                    AssertTrue(totalSpatialMatched == 5823 && spatialSummary.UnmatchedPipes.Count == 0,
                        $"模拟无 Group 状态下，5823 条勘测管通过纯空间距离实现 100% 配对覆盖 (实际={totalSpatialMatched})");
                }
            }

            string finalSummary = $"[test_match_group] 测试完成：通过 {passedChecks} 项，失败 {failedChecks} 项。";
            if (failedChecks == 0)
            {
                Logger.Info($"========== {finalSummary} ==========");
            }
            else
            {
                Logger.Error($"========== {finalSummary} ==========");
            }
        }

        /// <summary>
        /// 复制上版数据图纸并在其中绘制每组（管线 <-> ws属性块）的醒目配对连线与定位标记，同时绑定 CAD Group，
        /// 保存为独立预览 DWG 供人工在 AutoCAD 中直观检查配对准确性。
        /// </summary>
        private static int GenerateVisualCheckDwg(
            string sourceDwgPath,
            string targetDwgPath,
            PipeSpatialMatchSummary summary)
        {
            if (File.Exists(targetDwgPath))
            {
                File.Delete(targetDwgPath);
            }
            File.Copy(sourceDwgPath, targetDwgPath, overwrite: true);

            int drawnCount = 0;

            using (var db = new Database(false, true))
            {
                db.ReadDwgFile(targetDwgPath, FileOpenMode.OpenForReadAndWriteNoShare, true, "");
                db.CloseInput(true);

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForWrite);

                    // 图层 1：黄色（ACI=2）—— 外包框中心命中（含大弯折 L/U 型管线，从 ws 块连向管线最近投影点）
                    ObjectId layerCenterHitId = EnsureCheckLayer(lt, tr, "00_核查配对线_外包框中心命中_黄", colorIndex: 2);

                    // 图层 2：亮绿色（ACI=3）—— 曲线邻近命中（从 ws 块连向管线最近投影点）
                    ObjectId layerCurveHitId = EnsureCheckLayer(lt, tr, "00_核查配对线_曲线邻近命中_绿", colorIndex: 3);

                    // 图层 3：青色（ACI=4）—— 近距离歧义候选辅助线
                    ObjectId layerConflictId = EnsureCheckLayer(lt, tr, "00_核查配对线_近距歧义候选_青", colorIndex: 4);

                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    DBDictionary groupDict = (DBDictionary)tr.GetObject(db.GroupDictionaryId, OpenMode.ForWrite);

                    var allMatches = summary.UniqueMatches.Concat(summary.ConflictMatches).ToList();
                    foreach (PipeAttachmentMatchItem item in allMatches)
                    {
                        if (item?.Pipe == null || item.MatchedAttachment == null)
                            continue;

                        ObjectId targetLayerId = item.Status == SpatialMatchStatus.OneToManyConflict
                            ? layerConflictId
                            : (item.Channel == SpatialMatchChannel.ExtentsCenterMatch ? layerCenterHitId : layerCurveHitId);

                        short targetColor = item.Status == SpatialMatchStatus.OneToManyConflict
                            ? (short)4
                            : (item.Channel == SpatialMatchChannel.ExtentsCenterMatch ? (short)2 : (short)3);

                        Point3d blockPt = item.MatchedAttachment.Position;
                        Point3d pipePt = item.ClosestPointOnPipe;

                        // 若 ws 块本身已经紧贴在直管段上（DistanceToCurve < 0.3m），则除了在块位置画小圆圈外，
                        // 再从块位置向管线起点和终点方向各拉一条短指示线（或连向管线端点），确保直管段在图面上也能一眼看出配对关系
                        var createdIds = new ObjectIdCollection();

                        if (db.TryGetObjectId(new Handle(Convert.ToInt64(item.Pipe.Handle, 16)), out ObjectId pipeId) && !pipeId.IsNull)
                        {
                            createdIds.Add(pipeId);
                        }
                        if (db.TryGetObjectId(new Handle(Convert.ToInt64(item.MatchedAttachment.Handle, 16)), out ObjectId attId) && !attId.IsNull)
                        {
                            createdIds.Add(attId);
                        }

                        if (item.DistanceToCurve >= 0.05)
                        {
                            // 绘制从 ws 块插入点指向所配对管线最近点的醒目连线
                            var linkLine = new Line(blockPt, pipePt)
                            {
                                LayerId = targetLayerId,
                                ColorIndex = targetColor,
                                LineWeight = LineWeight.LineWeight030
                            };
                            ObjectId lineId = ms.AppendEntity(linkLine);
                            tr.AddNewlyCreatedDBObject(linkLine, true);
                            createdIds.Add(lineId);

                            // 在管线落点处绘制一个小圆圈（半径 0.8m），清晰标示连线锚定在哪根管线上
                            var anchorCircle = new Circle(pipePt, Vector3d.ZAxis, 0.8)
                            {
                                LayerId = targetLayerId,
                                ColorIndex = targetColor
                            };
                            ObjectId circleId = ms.AppendEntity(anchorCircle);
                            tr.AddNewlyCreatedDBObject(anchorCircle, true);
                            createdIds.Add(circleId);
                        }
                        else
                        {
                            // 当 ws 块恰好落在直管段本体上（距离 < 0.05m）时：
                            // 在块与管线重合点处绘制一个半径 1.2m 的醒目圆圈，并向该管线首尾端各拉一条指向线到 1/4 处，方便人工识别它绑定的是哪条直管
                            var onPipeCircle = new Circle(blockPt, Vector3d.ZAxis, 1.2)
                            {
                                LayerId = targetLayerId,
                                ColorIndex = targetColor,
                                LineWeight = LineWeight.LineWeight030
                            };
                            ObjectId circleId = ms.AppendEntity(onPipeCircle);
                            tr.AddNewlyCreatedDBObject(onPipeCircle, true);
                            createdIds.Add(circleId);

                            if (item.Pipe.Geometry != null && item.Pipe.Geometry.NumPoints >= 2)
                            {
                                var coords = item.Pipe.Geometry.Coordinates;
                                Point3d startPt = new Point3d(coords[0].X, coords[0].Y, 0);
                                Point3d quarterPt = new Point3d(
                                    blockPt.X + (startPt.X - blockPt.X) * 0.35,
                                    blockPt.Y + (startPt.Y - blockPt.Y) * 0.35,
                                    0);
                                if (blockPt.DistanceTo(quarterPt) > 0.05)
                                {
                                    var dirLine = new Line(blockPt, quarterPt)
                                    {
                                        LayerId = targetLayerId,
                                        ColorIndex = targetColor,
                                        LineWeight = LineWeight.LineWeight030
                                    };
                                    ObjectId dirLineId = ms.AppendEntity(dirLine);
                                    tr.AddNewlyCreatedDBObject(dirLine, true);
                                    createdIds.Add(dirLineId);
                                }
                            }
                        }

                        // 同时将（管线 + ws块 + 连线）绑定为 CAD Group，在 CAD 中开启编组选择(Ctrl+Shift+A)时点选任一对象即可整组高亮
                        string grpName = $"MATCH_{item.Pipe.Handle}_{item.MatchedAttachment.Handle}";
                        if (!groupDict.Contains(grpName) && createdIds.Count >= 2)
                        {
                            var grp = new Group($"管线[{item.Pipe.Handle}]-属性块[{item.MatchedAttachment.Handle}]", true);
                            groupDict.SetAt(grpName, grp);
                            tr.AddNewlyCreatedDBObject(grp, true);
                            grp.Append(createdIds);
                        }

                        drawnCount++;
                    }

                    tr.Commit();
                }

                db.SaveAs(targetDwgPath, DwgVersion.Current);
            }

            Logger.Info($"  [可视化输出] 已保存配对连线检查图纸: {targetDwgPath} (共 {drawnCount} 组连线与编组)");
            return drawnCount;
        }

        private static ObjectId EnsureCheckLayer(LayerTable lt, Transaction tr, string layerName, short colorIndex)
        {
            if (lt.Has(layerName))
                return lt[layerName];

            var ltr = new LayerTableRecord
            {
                Name = layerName,
                Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex),
                IsLocked = false,
                IsOff = false,
                IsFrozen = false
            };
            ObjectId id = lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
            return id;
        }
    }
}
