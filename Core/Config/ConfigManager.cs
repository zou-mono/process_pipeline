using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using process_pipeline.Utils;

namespace process_pipeline.Core.Config
{
    /// <summary>
    /// 全局配置文件（Config.json）读写、校验与自愈管理器。
    /// 支持带 // 中文注释的 JSON 解析、缺失自动生成默认模板、损坏自动备份为 Config_error_bak.json 并恢复默认配置。
    /// </summary>
    public static class ConfigManager
    {
        public const string DefaultConfigFileName = "Config.json";
        public const string BackupConfigFileName = "Config_error_bak.json";

        private static readonly object _syncRoot = new object();
        private static AppConfig _current;
        private static string _activeConfigFilePath;

        /// <summary>
        /// 是否强制静默 UI 弹窗警告（在无界面 accoreconsole.exe 测试环境或显式开启时不弹模态框）。
        /// </summary>
        public static bool SuppressUiAlerts { get; set; } = false;

        /// <summary>
        /// 当前进程是否处于无头测试模式（accoreconsole.exe）或已显式静默 UI 弹窗。
        /// </summary>
        public static bool IsHeadlessOrSuppressedUi => ShouldSuppressUiAlerts(null);

        /// <summary>
        /// 最近一次加载配置时是否触发了错误自愈。
        /// </summary>
        public static bool LastLoadHadError { get; private set; }

        /// <summary>
        /// 最近一次加载配置的错误原因说明（无错误时为空字符串）。
        /// </summary>
        public static string LastErrorMessage { get; private set; } = string.Empty;

        /// <summary>
        /// 当前生效的配置文件完整路径。
        /// </summary>
        public static string ActiveConfigFilePath
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_activeConfigFilePath))
                {
                    string dllFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppDomain.CurrentDomain.BaseDirectory;
                    _activeConfigFilePath = Path.Combine(dllFolder, DefaultConfigFileName);
                }
                return _activeConfigFilePath;
            }
        }

        /// <summary>
        /// 当前生效的备份配置文件完整路径（与 ActiveConfigFilePath 同目录）。
        /// </summary>
        public static string ActiveBackupFilePath
        {
            get
            {
                string dir = Path.GetDirectoryName(ActiveConfigFilePath) ?? AppDomain.CurrentDomain.BaseDirectory;
                return Path.Combine(dir, BackupConfigFileName);
            }
        }

        /// <summary>
        /// 获取当前全局配置实例（首次访问时自动加载）。
        /// </summary>
        public static AppConfig Current
        {
            get
            {
                if (_current == null)
                {
                    lock (_syncRoot)
                    {
                        if (_current == null)
                        {
                            Load();
                        }
                    }
                }
                return _current;
            }
        }

        /// <summary>
        /// 从指定或默认的 Config.json 路径加载配置。
        /// </summary>
        /// <param name="customFilePath">自定义配置文件路径；传 null 则使用插件 DLL 同级目录下的 Config.json</param>
        /// <param name="suppressUiAlerts">是否静默 UI 弹窗；传 null 则自动根据 SuppressUiAlerts 及进程环境判断</param>
        public static AppConfig Load(string customFilePath = null, bool? suppressUiAlerts = null)
        {
            lock (_syncRoot)
            {
                if (!string.IsNullOrWhiteSpace(customFilePath))
                {
                    _activeConfigFilePath = Path.GetFullPath(customFilePath);
                }
                else if (string.IsNullOrWhiteSpace(_activeConfigFilePath))
                {
                    string dllFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppDomain.CurrentDomain.BaseDirectory;
                    _activeConfigFilePath = Path.Combine(dllFolder, DefaultConfigFileName);
                }

                string configPath = _activeConfigFilePath;
                string backupPath = ActiveBackupFilePath;

                LastLoadHadError = false;
                LastErrorMessage = string.Empty;

                bool fileExisted = File.Exists(configPath);

                if (!fileExisted)
                {
                    // 首次运行或配置文件不存在：直接生成带中文注释的标准默认 Config.json
                    var defaultConfig = AppConfig.CreateDefault();
                    TryWriteCommentedConfigFile(configPath, defaultConfig);
                    _current = defaultConfig;
                    return _current;
                }

                try
                {
                    string rawText = File.ReadAllText(configPath, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(rawText))
                    {
                        throw new InvalidDataException("配置文件内容为空。");
                    }

                    JToken rootToken;
                    using (var sr = new StringReader(rawText))
                    using (var reader = new JsonTextReader(sr))
                    {
                        var loadSettings = new JsonLoadSettings
                        {
                            CommentHandling = CommentHandling.Ignore,
                            LineInfoHandling = LineInfoHandling.Load
                        };
                        rootToken = JToken.ReadFrom(reader, loadSettings);
                    }

                    if (!(rootToken is JObject rootObj))
                    {
                        throw new InvalidDataException("配置文件根节点必须是 JSON 对象。");
                    }

                    // 校验关键业务节点是否存在且非空
                    ValidateRootJsonObject(rootObj);

                    // 反序列化为强类型配置对象（由于 List 属性在构造函数中已有默认值，使用 ObjectCreationHandling.Replace 避免列表重复追加）
                    var serializer = JsonSerializer.Create(new JsonSerializerSettings
                    {
                        ObjectCreationHandling = ObjectCreationHandling.Replace,
                        MissingMemberHandling = MissingMemberHandling.Ignore,
                        NullValueHandling = NullValueHandling.Ignore
                    });

                    var parsedConfig = rootObj.ToObject<AppConfig>(serializer);
                    NormalizeAndValidateConfig(parsedConfig);

                    _current = parsedConfig;
                    return _current;
                }
                catch (Exception ex)
                {
                    LastLoadHadError = true;
                    LastErrorMessage = ex.Message;

                    // 自愈机制：1. 备份损坏的原文件 -> 2. 重新生成带注释的标准默认文件 -> 3. 回退到默认内存配置 -> 4. 按需弹窗提示
                    var fallbackConfig = AppConfig.CreateDefault();
                    _current = fallbackConfig;

                    try
                    {
                        if (File.Exists(configPath))
                        {
                            File.Copy(configPath, backupPath, true);
                        }
                        TryWriteCommentedConfigFile(configPath, fallbackConfig);
                    }
                    catch
                    {
                        // 忽略文件被外部锁定时可能产生的 IO 异常，确保内存默认配置可用
                    }

                    if (!ShouldSuppressUiAlerts(suppressUiAlerts))
                    {
                        try
                        {
                            Application.ShowAlertDialog(
                                $"【全局配置错误自动修复】\n\n" +
                                $"检测到 {Path.GetFileName(configPath)} 配置文件格式错误或缺少必要图层配置：\n" +
                                $"{ex.Message}\n\n" +
                                $"为了保证插件正常运行，系统已自动为您重置为标准默认配置。\n" +
                                $"{IconUnicode.Info}原来的配置文件已安全备份为：\n" +
                                $"{Path.GetFileName(backupPath)}\n\n" +
                                $"请打开新的 {Path.GetFileName(configPath)} 参考标准格式，并重新填入自定义参数。"
                            );
                        }
                        catch
                        {
                            // 忽略 CAD 尚未完全初始化时弹窗可能引发的异常
                        }
                    }

                    return _current;
                }
            }
        }

        /// <summary>
        /// 将当前配置（或修改后的配置）保存回当前激活的 Config.json（保留完整中文注释）。
        /// </summary>
        public static bool Save()
        {
            lock (_syncRoot)
            {
                if (_current == null)
                {
                    _current = AppConfig.CreateDefault();
                }
                return TryWriteCommentedConfigFile(ActiveConfigFilePath, _current);
            }
        }

        /// <summary>
        /// 更新并持久化多数据源（上版数据 DWG、勘测数据 DWG）的最近使用路径。
        /// </summary>
        public static void SaveDataSourcePaths(string previousDwgPath, string surveyDwgPath)
        {
            lock (_syncRoot)
            {
                var cfg = Current;
                if (cfg.DataSources == null)
                {
                    cfg.DataSources = new DataSourceMemorySettings();
                }

                if (previousDwgPath != null)
                {
                    cfg.DataSources.LastPreviousDataDwgPath = previousDwgPath.Trim();
                }

                if (surveyDwgPath != null)
                {
                    cfg.DataSources.LastSurveyDataDwgPath = surveyDwgPath.Trim();
                }

                TryWriteCommentedConfigFile(ActiveConfigFilePath, cfg);
            }
        }

        /// <summary>
        /// 校验 JSON 根节点中是否包含必要的图层配置节，防止用户将整个节点删空或拼写错误而静默失效。
        /// </summary>
        private static void ValidateRootJsonObject(JObject rootObj)
        {
            JToken planningToken = rootObj["PlanningData"];
            if (planningToken == null || planningToken.Type != JTokenType.Object)
            {
                throw new InvalidDataException("缺少 PlanningData（规划数据配置）节点。");
            }

            JToken categoriesToken = planningToken["Categories"];
            if (categoriesToken == null || categoriesToken.Type != JTokenType.Array || !categoriesToken.HasValues)
            {
                throw new InvalidDataException("PlanningData.Categories（规划管线类别与图层规则）不能为空。");
            }

            JToken arrowLayersToken = planningToken["ArrowLayers"];
            if (arrowLayersToken == null || arrowLayersToken.Type != JTokenType.Array || !arrowLayersToken.HasValues)
            {
                throw new InvalidDataException("PlanningData.ArrowLayers（流向箭头图层列表）不能为空。");
            }
        }

        /// <summary>
        /// 对反序列化后的配置对象做二次完整性校验与默认值兜底。
        /// </summary>
        private static void NormalizeAndValidateConfig(AppConfig config)
        {
            if (config == null)
                throw new InvalidDataException("配置反序列化结果为空。");

            if (config.General == null) config.General = new GeneralSettings();
            if (config.TaskNames == null) config.TaskNames = new TaskNameSettings();
            if (config.PlanningData == null) throw new InvalidDataException("PlanningData 节点为空。");
            if (config.PreviousData == null) config.PreviousData = new PreviousDataSettings();
            if (config.SurveyData == null) config.SurveyData = new SurveyDataSettings();
            if (config.Algorithms == null) config.Algorithms = new AlgorithmSettings();
            if (config.Algorithms.ArrowMatch == null) config.Algorithms.ArrowMatch = new ArrowMatchSettings();
            if (config.Algorithms.SpatialMatch == null) config.Algorithms.SpatialMatch = new SpatialMatchSettings();
            if (config.Algorithms.PipeMerge == null) config.Algorithms.PipeMerge = new PipeMergeSettings();
            if (config.Algorithms.PipeCompare == null) config.Algorithms.PipeCompare = new PipeCompareSettings();
            if (config.DataSources == null) config.DataSources = new DataSourceMemorySettings();

            var pipeLayers = config.PlanningData.GetAllPipeLayers();
            if (pipeLayers.Count == 0)
            {
                throw new InvalidDataException("PlanningData.Categories 中未配置任何有效的管线图层（PipeLayers）。");
            }

            var arrowLayers = config.PlanningData.GetArrowLayersSet();
            if (arrowLayers.Count == 0)
            {
                throw new InvalidDataException("PlanningData.ArrowLayers 中未配置任何有效的流向箭头图层。");
            }

            if (config.PreviousData.PipeLayers == null || config.PreviousData.PipeLayers.Count == 0)
            {
                throw new InvalidDataException("PreviousData.PipeLayers（上版数据管线图层列表）不能为空。");
            }

            if (config.SurveyData.PipeLayers == null || config.SurveyData.PipeLayers.Count == 0)
            {
                throw new InvalidDataException("SurveyData.PipeLayers（勘测数据管线图层列表）不能为空。");
            }

            if (config.Algorithms.ArrowMatch.MaxBufferDistance <= 0)
                config.Algorithms.ArrowMatch.MaxBufferDistance = 50.0;

            if (config.Algorithms.ArrowMatch.AngleTolerance <= 0)
                config.Algorithms.ArrowMatch.AngleTolerance = 30.0;

            if (config.Algorithms.SpatialMatch.LengthAttributeAliases == null ||
                config.Algorithms.SpatialMatch.LengthAttributeAliases.Count == 0)
            {
                config.Algorithms.SpatialMatch.LengthAttributeAliases = new List<string>
                {
                    "LEN", "LENGTH", "PIPE_LEN", "管长", "长度", "CD"
                };
            }

            if (config.Algorithms.SpatialMatch.WidthAttributeAliases == null ||
                config.Algorithms.SpatialMatch.WidthAttributeAliases.Count == 0)
            {
                config.Algorithms.SpatialMatch.WidthAttributeAliases = new List<string>
                {
                    "WIDTH", "DN", "DIAMETER", "管径", "宽度", "GJ"
                };
            }

            if (config.Algorithms.SpatialMatch.HeightAttributeAliases == null ||
                config.Algorithms.SpatialMatch.HeightAttributeAliases.Count == 0)
            {
                config.Algorithms.SpatialMatch.HeightAttributeAliases = new List<string>
                {
                    "HEITH", "HEIGHT", "高度", "GD"
                };
            }

            if (config.General.GripLimit <= 0)
                config.General.GripLimit = 500;
        }

        private static bool ShouldSuppressUiAlerts(bool? explicitOverride)
        {
            if (explicitOverride.HasValue)
                return explicitOverride.Value;

            if (SuppressUiAlerts)
                return true;

            try
            {
                string procName = Process.GetCurrentProcess().ProcessName;
                if (!string.IsNullOrEmpty(procName) &&
                    procName.IndexOf("accoreconsole", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            catch
            {
                // Ignore
            }

            return false;
        }

        /// <summary>
        /// 生成包含详细中文 // 注释的 Config.json 文件，方便工程人员直接阅读与编辑。
        /// </summary>
        public static bool TryWriteCommentedConfigFile(string filePath, AppConfig config)
        {
            try
            {
                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string content = BuildCommentedJsonContent(config ?? AppConfig.CreateDefault());
                File.WriteAllText(filePath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string BuildCommentedJsonContent(AppConfig cfg)
        {
            var sb = new StringBuilder(4096);
            sb.AppendLine("// ============================================================================");
            sb.AppendLine("// 管线综合核查插件全局配置文件 (Config.json)");
            sb.AppendLine("// 说明：");
            sb.AppendLine("// 1. 本文件采用标准 JSON 格式，支持使用 // 编写单行注释。");
            sb.AppendLine("// 2. 修改本文件后，可在 CAD 中重新运行核查命令或调用 Reload 使配置生效。");
            sb.AppendLine("// 3. 若配置文件语法损坏或关键图层被清空，系统会自动将其备份为 Config_error_bak.json 并恢复默认配置。");
            sb.AppendLine("// ============================================================================");
            sb.AppendLine("{");

            // 1. General
            sb.AppendLine("  // 通用系统参数配置");
            sb.AppendLine("  \"General\": " + IndentJson(cfg.General, 2) + ",");
            sb.AppendLine();

            // 2. TaskNames
            sb.AppendLine("  // 各核查与辅助命令的任务显示名称配置");
            sb.AppendLine("  \"TaskNames\": " + IndentJson(cfg.TaskNames, 2) + ",");
            sb.AppendLine();

            // 3. PlanningData
            sb.AppendLine("  // 【规划数据】图层、颜色优先级（1=红/规划新建，5=蓝/现状，6=洋红/规划改扩建）与标注图层配置");
            sb.AppendLine("  \"PlanningData\": " + IndentJson(cfg.PlanningData, 2) + ",");
            sb.AppendLine();

            // 4. PreviousData
            sb.AppendLine("  // 【上版数据】管线图层、属性块（ws 块）图层与 16 项标准属性字段配置");
            sb.AppendLine("  \"PreviousData\": " + IndentJson(cfg.PreviousData, 2) + ",");
            sb.AppendLine();

            // 5. SurveyData
            sb.AppendLine("  // 【勘测数据】实测现状管线图层与属性块字段配置");
            sb.AppendLine("  \"SurveyData\": " + IndentJson(cfg.SurveyData, 2) + ",");
            sb.AppendLine();

            // 6. Algorithms
            sb.AppendLine("  // 核心几何与核查算法阈值参数配置（单位：米 / 度）");
            sb.AppendLine("  \"Algorithms\": " + IndentJson(cfg.Algorithms, 2) + ",");
            sb.AppendLine();

            // 7. DataSources
            sb.AppendLine("  // 跨文档核查数据源智能嗅探与历史路径记忆配置");
            sb.AppendLine("  \"DataSources\": " + IndentJson(cfg.DataSources, 2));

            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string IndentJson(object obj, int baseIndentSpaces)
        {
            string raw = JsonConvert.SerializeObject(obj, Formatting.Indented);
            string[] lines = raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            if (lines.Length <= 1)
                return raw;

            string indent = new string(' ', baseIndentSpaces);
            var sb = new StringBuilder(raw.Length + lines.Length * baseIndentSpaces);
            sb.AppendLine(lines[0]);
            for (int i = 1; i < lines.Length; i++)
            {
                if (i == lines.Length - 1)
                    sb.Append(indent).Append(lines[i]);
                else
                    sb.Append(indent).AppendLine(lines[i]);
            }
            return sb.ToString();
        }
    }
}
