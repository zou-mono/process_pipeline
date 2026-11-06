using System;
using System.Collections.Generic;
using process_pipeline.Core.Config;

namespace process_pipeline.Core
{
    /// <summary>
    /// 全局配置静态门面类（兼容原有 CadConfig 调用方式，底层完全由 Core/Config/ConfigManager 驱动）。
    /// </summary>
    public static class CadConfig
    {
        private static HashSet<string> _pipeLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static HashSet<string> _arrowLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static CadConfig()
        {
            RefreshCachedSets(ConfigManager.Current);
        }

        /// <summary>
        /// 获取当前完整的分层强类型配置对象。
        /// </summary>
        public static AppConfig Current => ConfigManager.Current;

        /// <summary>
        /// 各命令与面板的任务中文名称配置（替代原 Properties.Settings.Default）。
        /// </summary>
        public static TaskNameSettings TaskNames => ConfigManager.Current.TaskNames;

        /// <summary>
        /// 规划数据图层与分类规则配置。
        /// </summary>
        public static PlanningDataSettings PlanningData => ConfigManager.Current.PlanningData;

        /// <summary>
        /// 上版数据图层与属性字段配置。
        /// </summary>
        public static PreviousDataSettings PreviousData => ConfigManager.Current.PreviousData;

        /// <summary>
        /// 勘测数据图层与属性字段配置。
        /// </summary>
        public static SurveyDataSettings SurveyData => ConfigManager.Current.SurveyData;

        /// <summary>
        /// 核心几何与核查算法阈值参数配置。
        /// </summary>
        public static AlgorithmSettings Algorithms => ConfigManager.Current.Algorithms;

        /// <summary>
        /// 跨文档核查数据源历史路径记忆配置。
        /// </summary>
        public static DataSourceMemorySettings DataSources => ConfigManager.Current.DataSources;

        /// <summary>
        /// 规划数据所有管线图层集合（向下兼容属性）。
        /// </summary>
        public static HashSet<string> PipeLayers => _pipeLayers;

        /// <summary>
        /// 规划数据所有流向箭头图层集合（向下兼容属性）。
        /// </summary>
        public static HashSet<string> ArrowLayers => _arrowLayers;

        /// <summary>
        /// 流向箭头匹配最大缓冲距离（向下兼容属性，对应 Algorithms.ArrowMatch.MaxBufferDistance）。
        /// </summary>
        public static double MaxBufferDistance => ConfigManager.Current.Algorithms.ArrowMatch.MaxBufferDistance;

        /// <summary>
        /// 流向箭头匹配角度偏差容差（向下兼容属性，对应 Algorithms.ArrowMatch.AngleTolerance）。
        /// </summary>
        public static double AngleTolerance => ConfigManager.Current.Algorithms.ArrowMatch.AngleTolerance;

        /// <summary>
        /// 选中实体时 CAD 夹点显示数量上限（向下兼容属性，对应 General.GripLimit）。
        /// </summary>
        public static short GripLimit => ConfigManager.Current.General.GripLimit;

        /// <summary>
        /// 重新从当前激活的 Config.json 磁盘文件读取配置（支持热重载）。
        /// </summary>
        public static void Reload()
        {
            var cfg = ConfigManager.Load();
            RefreshCachedSets(cfg);
        }

        /// <summary>
        /// 从指定路径加载 Config.json（专供自定义测试或切换配置文件使用）。
        /// </summary>
        public static AppConfig LoadFromFile(string customFilePath, bool? suppressUiAlerts = null)
        {
            var cfg = ConfigManager.Load(customFilePath, suppressUiAlerts);
            RefreshCachedSets(cfg);
            return cfg;
        }

        /// <summary>
        /// 将当前配置保存回 Config.json。
        /// </summary>
        public static bool Save()
        {
            return ConfigManager.Save();
        }

        /// <summary>
        /// 触发静态初始化并确保 Config.json 已被加载与自愈校验。
        /// </summary>
        public static void EnsureLoaded()
        {
            if (_pipeLayers == null || _pipeLayers.Count == 0)
            {
                RefreshCachedSets(ConfigManager.Current);
            }
        }

        private static void RefreshCachedSets(AppConfig cfg)
        {
            if (cfg?.PlanningData != null)
            {
                _pipeLayers = cfg.PlanningData.GetAllPipeLayers();
                _arrowLayers = cfg.PlanningData.GetArrowLayersSet();
            }
            else
            {
                _pipeLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _arrowLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
