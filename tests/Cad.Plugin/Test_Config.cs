using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using NLog;
using process_pipeline.Core;
using process_pipeline.Core.Config;
using process_pipeline.Core.DataSource;
using process_pipeline.Geometry;
using process_pipeline.Models;

namespace Cad.Plugin
{
    /// <summary>
    /// Config.json 统一配置管理框架无 UI 自动化测试（Commit 2.2）。
    /// 覆盖场景：
    /// 1. 缺失时自动生成带 // 中文注释的标准 Config.json 并验证所有分层默认值；
    /// 2. 含 // 中文注释的自定义参数与数据源记忆路径读取及 Reload() 热重载验证；
    /// 3. JSON 语法损坏与关键图层缺失时的 Config_error_bak.json 自动备份与自愈恢复验证。
    /// </summary>
    public class Test_Config : CadCommandBase
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        [CommandMethod("test_config")]
        public override void Execute()
        {
            Logger.Info("========== 开始执行 [test_config] 配置管理框架自动化测试 ==========");

            string originalConfigPath = ConfigManager.ActiveConfigFilePath;
            string testDir = Path.Combine(Path.GetTempPath(), "process_pipeline_test_config_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(testDir);

            string testConfigPath = Path.Combine(testDir, ConfigManager.DefaultConfigFileName);
            string testBackupPath = Path.Combine(testDir, ConfigManager.BackupConfigFileName);

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

            try
            {
                // ====================================================================
                // 场景 1：缺失 Config.json 时自动生成带中文注释的标准配置文件
                // ====================================================================
                Logger.Info("--- 场景 1：验证缺失时自动生成带中文注释的默认 Config.json ---");
                if (File.Exists(testConfigPath))
                    File.Delete(testConfigPath);

                AppConfig cfg1 = CadConfig.LoadFromFile(testConfigPath, suppressUiAlerts: true);

                AssertTrue(File.Exists(testConfigPath), "默认 Config.json 文件已自动生成");
                AssertTrue(!ConfigManager.LastLoadHadError, "首次生成默认配置不应标记为错误自愈");

                string generatedText = File.ReadAllText(testConfigPath, Encoding.UTF8);
                AssertTrue(generatedText.Contains("// 管线综合核查插件全局配置文件"), "生成的 Config.json 包含顶部中文 // 注释说明");
                AssertTrue(generatedText.Contains("\"PlanningData\"") && generatedText.Contains("\"PreviousData\"") && generatedText.Contains("\"SurveyData\""),
                    "生成的 Config.json 包含规划/上版/勘测三大数据源配置节点");

                AssertTrue(cfg1.PlanningData.Categories.Count == 3, $"规划数据默认包含 3 类管线规则 (实际={cfg1.PlanningData.Categories.Count})");
                AssertTrue(CadConfig.PipeLayers.Count == 5 && CadConfig.PipeLayers.Contains("3-污水管-2025新建"),
                    $"CadConfig.PipeLayers 兼容门面正确汇总 5 个规划管线图层 (实际={CadConfig.PipeLayers.Count})");
                AssertTrue(CadConfig.ArrowLayers.Count == 2 && CadConfig.ArrowLayers.Contains("3-标注-流向"),
                    $"CadConfig.ArrowLayers 兼容门面正确汇总 2 个流向箭头图层 (实际={CadConfig.ArrowLayers.Count})");
                AssertTrue(cfg1.PreviousData.PipeLayers.Count == 4 && cfg1.PreviousData.CoreAttributeTags.Count == 16,
                    $"上版数据默认包含 4 个管线图层与 16 项标准属性字段");
                AssertTrue(cfg1.SurveyData.PipeLayers.Contains("勘测管"), "勘测数据默认包含 '勘测管' 图层");
                AssertTrue(Math.Abs(CadConfig.MaxBufferDistance - 50.0) < 1e-6 && Math.Abs(CadConfig.AngleTolerance - 30.0) < 1e-6,
                    $"默认流向匹配阈值正确 (MaxBufferDistance={CadConfig.MaxBufferDistance}, AngleTolerance={CadConfig.AngleTolerance})");

                // ====================================================================
                // 场景 2：修改带 // 注释的自定义配置并验证 Reload() 与路径记忆持久化
                // ====================================================================
                Logger.Info("--- 场景 2：验证带 // 注释的自定义配置读取、Reload() 热重载与路径记忆 ---");
                string customJsonWithComments = generatedText
                    .Replace("\"MaxBufferDistance\": 50.0", "// 自定义测试缓冲距离\r\n      \"MaxBufferDistance\": 88.5")
                    .Replace("\"AngleTolerance\": 30.0", "\"AngleTolerance\": 42.0")
                    .Replace("\"GripLimit\": 500", "\"GripLimit\": 320");

                File.WriteAllText(testConfigPath, customJsonWithComments, Encoding.UTF8);
                CadConfig.Reload();

                AssertTrue(!ConfigManager.LastLoadHadError, "加载含 // 注释的自定义 Config.json 无报错");
                AssertTrue(Math.Abs(CadConfig.MaxBufferDistance - 88.5) < 1e-6, $"Reload 后 MaxBufferDistance 更新为 88.5 (实际={CadConfig.MaxBufferDistance})");
                AssertTrue(Math.Abs(CadConfig.AngleTolerance - 42.0) < 1e-6, $"Reload 后 AngleTolerance 更新为 42.0 (实际={CadConfig.AngleTolerance})");
                AssertTrue(CadConfig.GripLimit == 320, $"Reload 后 GripLimit 更新为 320 (实际={CadConfig.GripLimit})");

                // 测试数据源路径记忆保存
                string dummyPrevPath = @"D:\Codes\Tools\process_pipeline\Data\上版数据.dwg";
                string dummySurveyPath = @"D:\Codes\Tools\process_pipeline\Data\勘测数据.dwg";
                ConfigManager.SaveDataSourcePaths(dummyPrevPath, dummySurveyPath);
                CadConfig.Reload();

                AssertTrue(CadConfig.DataSources.LastPreviousDataDwgPath == dummyPrevPath &&
                           CadConfig.DataSources.LastSurveyDataDwgPath == dummySurveyPath,
                    "SaveDataSourcePaths 持久化上版与勘测 DWG 路径并在 Reload 后保持一致");

                // ====================================================================
                // 场景 3：写入损坏的 JSON 语法，验证自动备份为 Config_error_bak.json 并自愈恢复
                // ====================================================================
                Logger.Info("--- 场景 3：验证损坏 JSON 自动备份为 Config_error_bak.json 并恢复默认配置 ---");
                string corruptedContent = "// 故意损坏的配置文件\r\n{ \"General\": { \"GripLimit\": 500, BROKEN_JSON ";
                File.WriteAllText(testConfigPath, corruptedContent, Encoding.UTF8);

                CadConfig.Reload();

                AssertTrue(ConfigManager.LastLoadHadError, $"检测到损坏 JSON 并触发自愈 (错误信息: {ConfigManager.LastErrorMessage})");
                AssertTrue(File.Exists(testBackupPath), "已自动生成损坏备份文件 Config_error_bak.json");
                string backupContent = File.ReadAllText(testBackupPath, Encoding.UTF8);
                AssertTrue(backupContent.Contains("BROKEN_JSON"), "Config_error_bak.json 完整保留了损坏前的原始文本");
                AssertTrue(Math.Abs(CadConfig.MaxBufferDistance - 50.0) < 1e-6 && CadConfig.GripLimit == 500,
                    "自愈后内存配置与磁盘 Config.json 已恢复为标准默认值");

                // ====================================================================
                // 场景 4：关键图层配置被清空时的语义校验自愈
                // ====================================================================
                Logger.Info("--- 场景 4：验证关键图层列表为空时的语义校验与自愈 ---");
                string emptyArrowLayersJson = File.ReadAllText(testConfigPath, Encoding.UTF8)
                    .Replace("\"3-标注-流向\",", "")
                    .Replace("\"污水-流向\"", "");
                File.WriteAllText(testConfigPath, emptyArrowLayersJson, Encoding.UTF8);

                CadConfig.Reload();

                AssertTrue(ConfigManager.LastLoadHadError, $"检测到 ArrowLayers 为空并触发自愈 (错误信息: {ConfigManager.LastErrorMessage})");
                AssertTrue(CadConfig.ArrowLayers.Count == 2, $"自愈后 ArrowLayers 恢复为 2 个默认图层 (实际={CadConfig.ArrowLayers.Count})");

                // ====================================================================
                // 场景 5：验证 DocumentCheckSession 多规则/多子问题并存与 DataSourceService 智能数据源探测（Commit 2.3）
                // ====================================================================
                Logger.Info("--- 场景 5：验证 DocumentCheckSession IssueId 索引与 DataSourceService 三级探测 ---");
                if (Db != null)
                {
                    var session = DocumentSessionManager.GetOrCreateSession(Db);
                    session.ClearAll();

                    // 模拟同一根管线（或无 ObjectId 的缺失管段）在多个规则下产生多条问题
                    var arrowIssue = new CheckIssueItem
                    {
                        IssueId = "FlowArrow:18F:0",
                        RuleId = "FlowArrow",
                        RuleName = "流向箭头核查",
                        TargetHandle = "18F",
                        TargetObjectId = Db.BlockTableId, // 借用非空有效 ObjectId 模拟同一管线实体
                        Description = "流向冲突"
                    };
                    var segIssues = new List<CheckIssueItem>
                    {
                        new CheckIssueItem
                        {
                            IssueId = "CompareSegment:18F:0",
                            RuleId = "CompareSegment",
                            RuleName = "折线段对比",
                            TargetHandle = "18F",
                            TargetObjectId = Db.BlockTableId,
                            Description = "折线段 0 偏移"
                        },
                        new CheckIssueItem
                        {
                            IssueId = "CompareSegment:18F:1",
                            RuleId = "CompareSegment",
                            RuleName = "折线段对比",
                            TargetHandle = "18F",
                            TargetObjectId = Db.BlockTableId,
                            Description = "折线段 1 未覆盖"
                        },
                        new CheckIssueItem
                        {
                            IssueId = "CompareSegment:PREV_2A1:0",
                            RuleId = "CompareSegment",
                            RuleName = "折线段对比",
                            TargetHandle = "PREV_2A1",
                            TargetObjectId = ObjectId.Null,
                            Description = "上版管段缺失"
                        }
                    };

                    session.MergeRuleIssueItems("FlowArrow", new[] { arrowIssue }, isIncremental: false);
                    session.MergeRuleIssueItems("CompareSegment", segIssues, isIncremental: false);

                    AssertTrue(session.IssuesById.Count == 4,
                        $"DocumentCheckSession.IssuesById 支持同管线多规则/多折线段及 ObjectId.Null 问题并存 (预期=4, 实际={session.IssuesById.Count})");
                    var byOid = session.GetIssuesByObjectId(Db.BlockTableId);
                    AssertTrue(byOid.Count == 3,
                        $"ObjectIdToIssueIds 二级索引可准确反查同一 ObjectId 下的 3 条跨规则/多折线段问题 (实际={byOid.Count})");

                    // 测试 DataSourceService 对新版数据.dwg 的探测（新版数据含 2 个残留块参照，不应误判为已合图的上版数据）
                    string planDwgPath = @"D:\Codes\Tools\process_pipeline\Data\新版数据.dwg";
                    if (File.Exists(planDwgPath))
                    {
                        using (var planSideDb = new Database(false, true))
                        {
                            planSideDb.ReadDwgFile(planDwgPath, FileOpenMode.OpenForReadAndAllShare, true, "");
                            planSideDb.CloseInput(true);

                            var dsService = new DataSourceService();
                            var prevDesc = dsService.ResolvePreviousDataSource(planSideDb);
                            var surveyDesc = dsService.ResolveSurveyDataSource(planSideDb);

                            AssertTrue(prevDesc.IsValid && prevDesc.Kind == DataSourceKind.ExternalDwgFile,
                                $"DataSourceService 未将新版数据中的残留图层误判为同图数据，正确解析外部上版数据 ({prevDesc.DisplayName})");
                            AssertTrue(surveyDesc.IsValid && surveyDesc.Kind == DataSourceKind.ExternalDwgFile,
                                $"DataSourceService 正确解析外部勘测数据 ({surveyDesc.DisplayName})");
                        }
                    }

                    session.ClearAll();
                }

                // 验证 Config.json 中的属性字段别名列表（LengthAttributeAliases / WidthAttributeAliases / HeightAttributeAliases）与动态解析
                var spatialMatchCfg = CadConfig.Algorithms.SpatialMatch;
                AssertTrue(spatialMatchCfg.LengthAttributeAliases != null &&
                           spatialMatchCfg.LengthAttributeAliases.Contains("LEN") &&
                           spatialMatchCfg.LengthAttributeAliases.Contains("LENGTH") &&
                           spatialMatchCfg.LengthAttributeAliases.Contains("管长"),
                    $"Config.json 默认包含管长字段多别名配置 ({string.Join(", ", spatialMatchCfg.LengthAttributeAliases ?? new List<string>())})");

                var customAliasAttrs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["LENGTH"] = "128.65",
                    ["DN"] = "600",
                    ["HEIGHT"] = "2200"
                };
                double? resolvedLen = PipeSpatialMatcher.ResolveDeclaredLengthFromAttributes(customAliasAttrs);
                PipeSpatialMatcher.NormalizeCanonicalAttributeAliases(customAliasAttrs);
                AssertTrue(resolvedLen.HasValue && Math.Abs(resolvedLen.Value - 128.65) < 1e-6 &&
                           Equals(customAliasAttrs["LEN"], "128.65") &&
                           Equals(customAliasAttrs["WIDTH"], "600") &&
                           Equals(customAliasAttrs["HEITH"], "2200"),
                    "PipeSpatialMatcher 支持通过配置别名 (LENGTH/DN/HEIGHT) 动态解析管长并归一化补齐标准键名 (LEN/WIDTH/HEITH)");
            }
            finally
            {
                // 恢复默认配置文件路径并清理临时目录
                CadConfig.LoadFromFile(originalConfigPath, suppressUiAlerts: true);
                try
                {
                    if (Directory.Exists(testDir))
                    {
                        Directory.Delete(testDir, recursive: true);
                    }
                }
                catch
                {
                    // Ignore cleanup error
                }
            }

            string summary = $"[test_config] 测试完成：通过 {passedChecks} 项，失败 {failedChecks} 项。";
            if (failedChecks == 0)
            {
                Logger.Info($"========== {summary} ==========");
            }
            else
            {
                Logger.Error($"========== {summary} ==========");
            }
        }
    }
}
