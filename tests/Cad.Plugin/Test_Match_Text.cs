using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using NLog;
using process_pipeline.Commands;
using process_pipeline.Core;
using process_pipeline.Core.DataSource;
using process_pipeline.Core.PreCheck;
using process_pipeline.Geometry;
using process_pipeline.Models;

namespace Cad.Plugin
{
    /// <summary>
    /// 阶段五 Commit 5.1 无 UI 命令行自动化测试：
    /// 验证规划管线文字标注解析器（PipeLabelParser）及规划管线与文字标注的空间+平行角度配对、PipeDatasetContext 富化组装。
    /// 
    /// 覆盖场景：
    /// 1. PipeLabelParser 正则解析器单元测试：
    ///    - 2-管线规格-现状 格式（"d400"、"d800-i1.0"）；
    ///    - 2-管线名称-2025 格式（"d600-i5.6-L1080"、"DN600-L2216"）；
    ///    - 3-标注-管线标注-规划扩建 格式（"d700-i2.0-L900"）；
    ///    - MText 控制符剥离、%%c 直径转义符、渠箱断面（1200x1000）及非管线文字过滤；
    /// 2. Data\新版数据.dwg 三大规划文字标注图层全量扫描与解析率验证；
    /// 3. 复用 PipeSpatialMatcher 完成规划管线与文字标注的空间距离 + 走向平行角配对，
    ///    验证结果组装写入 PipeDatasetContext（WIDTH / PD / LEN / RAW_LABEL）并输出未匹配管线明细。
    /// </summary>
    public class Test_Match_Text : CadCommandBase
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        [CommandMethod("test_match_text")]
        public override void Execute()
        {
            Logger.Info("========== 开始执行 [test_match_text] 规划管线文字标注解析与空间配对测试 ==========");

            int passedChecks = 0;
            int failedChecks = 0;

            void AssertTrue(bool condition, string description)
            {
                if (condition)
                {
                    passedChecks++;
                    Logger.Info($"  [PASS] {description}");
                }
                else
                {
                    failedChecks++;
                    Logger.Error($"  [FAIL] {description}");
                }
            }

            // =========================================================================
            // 场景 1：验证 PipeLabelParser 正则解析器对各类典型标注格式与 MText 控制符的解析
            // =========================================================================
            Logger.Info("--- 场景 1：验证 PipeLabelParser 正则解析器对典型规划文字标注的解析 ---");

            var p1 = PipeLabelParser.Parse("d400");
            AssertTrue(p1.IsSuccess && p1.Diameter == 400.0 && p1.Height == 0.0 && !p1.Slope.HasValue && !p1.Length.HasValue,
                "解析现状规格 'd400' -> WIDTH=400, HEITH=0, PD=null, LEN=null");

            var p2 = PipeLabelParser.Parse("d800-i1.0");
            AssertTrue(p2.IsSuccess && p2.Diameter == 800.0 && p2.Slope == 1.0 && !p2.Length.HasValue,
                "解析现状带坡度规格 'd800-i1.0' -> WIDTH=800, PD=1.0, LEN=null");

            var p3 = PipeLabelParser.Parse("d600-i5.6-L1080");
            AssertTrue(p3.IsSuccess && p3.Diameter == 600.0 && p3.Slope == 5.6 && p3.Length == 1080.0,
                "解析 2025 新建规格 'd600-i5.6-L1080' -> WIDTH=600, PD=5.6, LEN=1080");

            var p4 = PipeLabelParser.Parse("DN600-L2216");
            AssertTrue(p4.IsSuccess && p4.Prefix == "DN" && p4.Diameter == 600.0 && !p4.Slope.HasValue && p4.Length == 2216.0,
                "解析 2025 新建压力管规格 'DN600-L2216' -> Prefix=DN, WIDTH=600, PD=null, LEN=2216");

            var p5 = PipeLabelParser.Parse("d700-i2.0-L900");
            AssertTrue(p5.IsSuccess && p5.Diameter == 700.0 && p5.Slope == 2.0 && p5.Length == 900.0,
                "解析规划扩建规格 'd700-i2.0-L900' -> WIDTH=700, PD=2.0, LEN=900");

            var p6 = PipeLabelParser.Parse("\\A1;{\\fSimSun|b0|i0;%%c500－i1.5—L320}");
            AssertTrue(p6.IsSuccess && p6.Diameter == 500.0 && p6.Slope == 1.5 && p6.Length == 320.0,
                "解析含 MText 富文本控制码、%%c 直径符与全角连字符标注 -> WIDTH=500, PD=1.5, LEN=320");

            var p7 = PipeLabelParser.Parse("1200x1000-i1.2-L450");
            AssertTrue(p7.IsSuccess && p7.Diameter == 1200.0 && p7.Height == 1000.0 && p7.Slope == 1.2 && p7.Length == 450.0,
                "解析矩形渠箱标注 '1200x1000-i1.2-L450' -> WIDTH=1200, HEITH=1000, PD=1.2, LEN=450");

            var p8 = PipeLabelParser.Parse("2DN1000");
            AssertTrue(p8.IsSuccess && p8.Prefix == "DN" && p8.Diameter == 1000.0 && p8.Height == 0.0,
                "解析并排双管压力管标注 '2DN1000' -> Prefix=DN, WIDTH=1000, HEITH=0");

            var pInvalid = PipeLabelParser.Parse("污水规划总平面图-2025");
            AssertTrue(!pInvalid.IsSuccess && !pInvalid.Diameter.HasValue,
                "非管线规格普通文本 '污水规划总平面图-2025' 正确过滤（IsSuccess=false）");

            // =========================================================================
            // 场景 2：扫描 Data\新版数据.dwg 三大规划文字标注图层并验证实测标注解析率
            // =========================================================================
            string planDwgPath = @"D:\Codes\Tools\process_pipeline\Data\新版数据.dwg";
            AssertTrue(File.Exists(planDwgPath), $"规划数据测试文件存在: {planDwgPath}");
            if (!File.Exists(planDwgPath))
                return;

            Logger.Info("--- 场景 2：扫描 Data\\新版数据.dwg 规划文字标注图层实体与解析统计 ---");

            using (Database planDb = new Database(false, true))
            {
                planDb.ReadDwgFile(planDwgPath, FileOpenMode.OpenForReadAndAllShare, false, null);
                planDb.CloseInput(true);

                var planCfg = CadConfig.PlanningData;
                var targetTextLayers = planCfg.GetAllTextLayers();

                var totalTextByLayer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var parsedTextByLayer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var sampleParsedByLayer = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var unparsedTexts = new List<string>();

                foreach (string tl in targetTextLayers)
                {
                    totalTextByLayer[tl] = 0;
                    parsedTextByLayer[tl] = 0;
                    sampleParsedByLayer[tl] = new List<string>();
                }

                using (Transaction tr = planDb.TransactionManager.StartOpenCloseTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(planDb.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                    foreach (ObjectId objId in ms)
                    {
                        if (objId.IsNull || objId.IsErased) continue;
                        Entity ent = tr.GetObject(objId, OpenMode.ForRead, false) as Entity;
                        if (ent == null || string.IsNullOrWhiteSpace(ent.Layer)) continue;

                        string layer = ent.Layer.Trim();
                        if (!targetTextLayers.Contains(layer)) continue;

                        if (ent is DBText || ent is MText)
                        {
                            totalTextByLayer[layer] = totalTextByLayer.TryGetValue(layer, out int c) ? c + 1 : 1;
                            SpatialAttachmentItem att = PipeLabelParser.CreateAttachmentFromTextEntity(ent, objId, out PipeLabelParseResult parsed);
                            if (att != null && parsed != null && parsed.IsSuccess)
                            {
                                parsedTextByLayer[layer] = parsedTextByLayer.TryGetValue(layer, out int pc) ? pc + 1 : 1;
                                if (sampleParsedByLayer[layer].Count < 5)
                                {
                                    sampleParsedByLayer[layer].Add(
                                        $"{att.Handle}: '{parsed.CleanedText}' -> WIDTH={parsed.Diameter}, PD={parsed.Slope?.ToString() ?? "-"}, LEN={parsed.Length?.ToString() ?? "-"}");
                                }
                            }
                            else
                            {
                                string raw = (ent as DBText)?.TextString ?? (ent as MText)?.Text ?? string.Empty;
                                unparsedTexts.Add($"[{layer}] Handle={ent.Handle}: '{raw}'");
                            }
                        }
                    }

                    tr.Commit();
                }

                int totalTextEntities = totalTextByLayer.Values.Sum();
                int totalParsedEntities = parsedTextByLayer.Values.Sum();

                foreach (string tl in targetTextLayers)
                {
                    Logger.Info($"  [图层文字统计] {tl}: 总文字实体={totalTextByLayer[tl]}, 成功解析={parsedTextByLayer[tl]}");
                    foreach (string sample in sampleParsedByLayer[tl])
                    {
                        Logger.Info($"    样例 -> {sample}");
                    }
                }

                if (unparsedTexts.Count > 0)
                {
                    Logger.Info($"  [未解析为管径规格的文字] 共 {unparsedTexts.Count} 个: {string.Join("; ", unparsedTexts.Take(10))}");
                }

                AssertTrue(totalTextByLayer.TryGetValue("2-管线规格-现状", out int cntEx) && cntEx > 0 && parsedTextByLayer["2-管线规格-现状"] == cntEx,
                    $"图层 [2-管线规格-现状] 全部 {cntEx} 个文字标注 100% 解析成功");
                AssertTrue(totalTextByLayer.TryGetValue("2-管线名称-2025", out int cntNew) && cntNew > 0 && parsedTextByLayer["2-管线名称-2025"] == cntNew,
                    $"图层 [2-管线名称-2025] 全部 {cntNew} 个文字标注 100% 解析成功");
                AssertTrue(totalTextByLayer.TryGetValue("3-标注-管线标注-规划扩建", out int cntExp) && cntExp > 0 && parsedTextByLayer["3-标注-管线标注-规划扩建"] == cntExp,
                    $"图层 [3-标注-管线标注-规划扩建] 全部 {cntExp} 个文字标注 100% 解析成功");

                // =========================================================================
                // 场景 3：验证 PipeSpatialMatcher 规划管线与文字标注空间+平行角度配对及 PipeDatasetContext 组装
                // =========================================================================
                Logger.Info("--- 场景 3：验证规划管线与文字标注空间+平行角度配对及 PipeDatasetContext 组装 ---");

                var session = DocumentSessionManager.GetOrCreateSession(planDb);
                session.DatasetContext.ClearSessionData();

                // 3.1 先执行规划数据分类自检（初始化 256 条有效规划管线的类别与几何）
                var planCheckSvc = new PlanDataCheckService(planDb, Ed);
                planCheckSvc.InspectPlanningData(planCfg, session.DatasetContext);

                // 3.2 再执行流向箭头核查（同步 256 条有效规划管线的流向属性 LX）
                var flowSvc = new FlowArrowService(planDb, Ed, false);
                flowSvc.CheckAll();

                // 3.3 执行规划管线文字标注空间+平行角度配对并组装至 PipeDatasetContext
                var matcher = new PipeSpatialMatcher();
                PipeSpatialMatchSummary textMatchSummary = matcher.MatchAndEnrichPlanTextLabels(
                    planDb,
                    session.DatasetContext,
                    planCfg);

                int matchedPipeCount = textMatchSummary.UniqueMatches.Count + textMatchSummary.ConflictMatches.Count;
                Logger.Info(
                    $"  [文字空间配对汇总] 有效规划管线总数={textMatchSummary.TotalPipeCount}, 有效文字标注总数={textMatchSummary.TotalAttachmentCount}, " +
                    $"唯一匹配(Unique)={textMatchSummary.UniqueMatches.Count}, 歧义匹配(Conflict)={textMatchSummary.ConflictMatches.Count}, " +
                    $"合计配对成功={matchedPipeCount}, 未配对管线(Unmatched)={textMatchSummary.UnmatchedPipes.Count}, 孤立文字(Orphan)={textMatchSummary.OrphanAttachments.Count}");

                // 按管线图层统计配对率与未匹配管线明细
                var layerGroups = session.DatasetContext.PlanPipesById.Values
                    .GroupBy(p => p.LayerName)
                    .OrderBy(g => g.Key);

                foreach (var grp in layerGroups)
                {
                    int totalInLayer = grp.Count();
                    int withWidth = grp.Count(p => p.TextDiameter.HasValue && p.Polyline != null && p.Polyline.Attributes.ContainsKey("WIDTH"));
                    int withSlope = grp.Count(p => p.TextSlope.HasValue && p.Polyline != null && p.Polyline.Attributes.ContainsKey("PD"));
                    int withLen = grp.Count(p => p.TextLength.HasValue && p.Polyline != null && p.Polyline.Attributes.ContainsKey("LEN"));
                    Logger.Info($"  [按图层富化统计] {grp.Key}: 总管线={totalInLayer}, 已提取管径(WIDTH)={withWidth}, 已提取坡度(PD)={withSlope}, 已提取管长(LEN)={withLen}");
                }

                if (textMatchSummary.UnmatchedPipes.Count > 0)
                {
                    Logger.Info($"  [未匹配文字标注的管线明细 (共 {textMatchSummary.UnmatchedPipes.Count} 条)]:");
                    foreach (PipeAttachmentMatchItem unm in textMatchSummary.UnmatchedPipes.Take(20))
                    {
                        Logger.Info($"    -> Handle={unm.Pipe.Handle}, Layer={unm.Pipe.LayerName}, Length={unm.Pipe.Length:F2}m, Mid=({unm.Pipe.MidPoint.X:F1},{unm.Pipe.MidPoint.Y:F1})");
                    }
                }

                if (textMatchSummary.OrphanAttachments.Count > 0)
                {
                    Logger.Info($"  [未配对到管线的孤立文字明细 (共 {textMatchSummary.OrphanAttachments.Count} 个)]:");
                    foreach (PipeAttachmentMatchItem orp in textMatchSummary.OrphanAttachments.Take(25))
                    {
                        var att = orp.MatchedAttachment;
                        var candidates = new List<string>();
                        foreach (var pInfo in session.DatasetContext.PlanPipesById.Values)
                        {
                            if (pInfo?.Polyline?.Geometry == null) continue;
                            double d = PipeSpatialMatcher.ComputePointToLineStringDistance(
                                att.Position, pInfo.Polyline.Geometry, out _, out double segAng);
                            if (d <= 150.0)
                            {
                                double angDiff = PipeSpatialMatcher.ComputeParallelAngleDifference(att.RotationDegrees ?? 0, segAng);
                                string matchedTag = pInfo.TextDiameter.HasValue ? $"[已配:{pInfo.MatchedTextHandle}='{pInfo.RawLabelText}']" : "[未配]";
                                candidates.Add($"{pInfo.Handle}({pInfo.LayerName},L={pInfo.Polyline.Geometry.Length:F0}m,d={d:F1}m,angDiff={angDiff:F1}°){matchedTag}");
                            }
                        }
                        candidates.Sort((a, b) =>
                        {
                            double da = double.Parse(a.Split(new[] { ",d=" }, StringSplitOptions.None)[1].Split('m')[0]);
                            double db = double.Parse(b.Split(new[] { ",d=" }, StringSplitOptions.None)[1].Split('m')[0]);
                            return da.CompareTo(db);
                        });
                        Logger.Info($"    -> Handle={att.Handle}, Layer={att.LayerName}, Text='{att.BlockNameOrText}', Pos=({att.Position.X:F1},{att.Position.Y:F1}), Rot={att.RotationDegrees:F1}° | 邻近管线: {string.Join(" ; ", candidates.Take(3))}");
                    }
                }

                int contextEnrichedWithWidth = session.DatasetContext.PlanPipesById.Values.Count(p =>
                    p.TextDiameter.HasValue &&
                    p.Polyline != null &&
                    p.Polyline.Attributes.ContainsKey("WIDTH") &&
                    p.Polyline.Attributes.ContainsKey("GHZT") &&
                    p.Polyline.Attributes.ContainsKey("LX"));

                Logger.Info($"  [PipeDatasetContext 三阶段汇总] 同时具备 GHZT(类别) + LX(流向) + WIDTH(文字管径) 的规划管线数={contextEnrichedWithWidth}/{session.DatasetContext.PlanPipesById.Count}");

                AssertTrue(textMatchSummary.TotalPipeCount == 256,
                    $"参与文字配对的有效非退化规划管线总数为 256 条 (实际={textMatchSummary.TotalPipeCount})");
                AssertTrue(textMatchSummary.TotalAttachmentCount == 216 && totalParsedEntities == 216,
                    $"参与空间配对的有效文字标注总数为 216 个 (14+194+8，实际={textMatchSummary.TotalAttachmentCount})");
                AssertTrue(matchedPipeCount == 171 && textMatchSummary.UnmatchedPipes.Count == 85 && textMatchSummary.OrphanAttachments.Count == 45,
                    $"按类别隔离配对后精准匹配 171 条规划管线（唯一=167, 歧义=4），检出 85 条无标注管段与 45 个长干管重复标注 (实际配对={matchedPipeCount})");
                AssertTrue(contextEnrichedWithWidth == matchedPipeCount,
                    $"全部配对成功的 {matchedPipeCount} 条规划管线均已将 WIDTH/PD/LEN 组装写入 PipeDatasetContext 并与 GHZT/LX 汇总 (实际={contextEnrichedWithWidth})");
            }

            string summaryText = $"[test_match_text] 测试完成：通过 {passedChecks} 项，失败 {failedChecks} 项。";
            if (failedChecks == 0)
            {
                Logger.Info($"========== {summaryText} ==========");
            }
            else
            {
                Logger.Error($"========== {summaryText} ==========");
            }
        }
    }
}
