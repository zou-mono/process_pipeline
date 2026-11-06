using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using process_pipeline.Core.Config;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace process_pipeline.Core.DataSource
{
    /// <summary>
    /// 多数据源三级智能探测与数据库读取核心服务（Commit 2.3，MVVM 无 UI 依赖服务层）。
    /// 
    /// 职责说明：
    /// 1. 在多数据源命令（CHECKPIPE / MATCHPIPE / COMPARESEGMENT）启动时，以当前激活文档作为【规划数据】，
    ///    按以下三级优先级自动探测【上版数据】与【勘测数据】：
    ///    - 优先级 1（同图图层探测）：检查当前规划图纸内是否已合并包含足够数量（>= 10 个实体，排除残留脏图层）的上版/勘测管线图层；
    ///    - 优先级 2（已打开 MDI 文档探测）：检查当前 AutoCAD 进程中已打开的其他 DWG 标签页；
    ///    - 优先级 3（Config.json 记忆路径与同目录同名探测）：检查上次记忆路径或图纸所在目录/项目 Data 目录下的候选 DWG。
    /// 2. 提供统一的 <see cref="OpenOrReadDatabase"/> 辅助方法，屏蔽“同图 Database”、“MDI 内存 Database”与“外部 Side Database”的打开与释放差异。
    /// </summary>
    public class DataSourceService
    {
        /// <summary>
        /// 判定同一图纸内存在完整数据集的最小管线实体数阈值。
        /// 说明：新版数据.dwg 中可能残留 1~2 条未清理干净的旧图层图元（如 0污水-已核实需新增 上的 2 个块参照），
        /// 设置阈值为 10 可防止将残留脏数据误判为已合图的完整上版数据（正常上版数据约 317 条，勘测数据约 5800+ 条）。
        /// </summary>
        public const int MinSameDrawingEntityCount = 10;

        /// <summary>
        /// 为指定的规划图纸会话自动探测并绑定【上版数据】与【勘测数据】描述符。
        /// </summary>
        /// <param name="activePlanDb">当前激活的规划数据 Database</param>
        /// <param name="context">当前文档会话的数据集上下文（若传 null 则仅返回探测结果）</param>
        /// <param name="needPreviousData">是否需要探测上版数据</param>
        /// <param name="needSurveyData">是否需要探测勘测数据</param>
        /// <returns>是否所有请求的数据源均已成功就绪（若返回 false，则上层命令可按需弹出数据源设置窗口）</returns>
        public bool TryAutoResolveDataSources(
            Database activePlanDb,
            PipeDatasetContext context,
            bool needPreviousData = true,
            bool needSurveyData = true)
        {
            if (activePlanDb == null)
                return false;

            bool allResolved = true;

            if (needPreviousData)
            {
                DataSourceDescriptor prevDesc = ResolvePreviousDataSource(activePlanDb, context?.PreviousDataSource);
                if (context != null)
                {
                    context.PreviousDataSource = prevDesc;
                }
                if (!prevDesc.IsValid)
                {
                    allResolved = false;
                }
            }

            if (needSurveyData)
            {
                DataSourceDescriptor surveyDesc = ResolveSurveyDataSource(activePlanDb, context?.SurveyDataSource);
                if (context != null)
                {
                    context.SurveyDataSource = surveyDesc;
                }
                if (!surveyDesc.IsValid)
                {
                    allResolved = false;
                }
            }

            return allResolved;
        }

        /// <summary>
        /// 按三级优先级自动探测【上版数据】数据源。
        /// </summary>
        public DataSourceDescriptor ResolvePreviousDataSource(
            Database activePlanDb,
            DataSourceDescriptor existingDescriptor = null)
        {
            var pipeLayers = new List<string>(CadConfig.PreviousData.PipeLayers ?? new List<string>());
            var attrLayers = new List<string>(CadConfig.PreviousData.AttributeLayers ?? new List<string>());

            if (existingDescriptor != null && existingDescriptor.IsValid)
            {
                if (existingDescriptor.PipeLayers == null || existingDescriptor.PipeLayers.Count == 0)
                    existingDescriptor.PipeLayers = pipeLayers;
                if (existingDescriptor.AttachmentLayers == null || existingDescriptor.AttachmentLayers.Count == 0)
                    existingDescriptor.AttachmentLayers = attrLayers;
                return existingDescriptor;
            }

            // 优先级 1：检查当前规划图纸内部是否已合并包含完整的上版数据图层
            if (CadConfig.DataSources.PreferSameDrawingLayers && activePlanDb != null)
            {
                int count = CountCurveEntitiesOnLayers(activePlanDb, pipeLayers, MinSameDrawingEntityCount);
                if (count >= MinSameDrawingEntityCount)
                {
                    return new DataSourceDescriptor
                    {
                        Kind = DataSourceKind.SameDrawing,
                        InMemoryDatabase = activePlanDb,
                        FilePath = SafeGetDatabasePath(activePlanDb),
                        DisplayName = "当前图纸内图层（合图模式）",
                        PipeLayers = pipeLayers,
                        AttachmentLayers = attrLayers
                    };
                }
            }

            // 优先级 2：检查当前 AutoCAD 进程中已打开的其他 MDI 文档标签页
            if (CadConfig.DataSources.PreferOpenMdiDocuments)
            {
                DataSourceDescriptor mdiMatch = TryFindInOpenMdiDocuments(
                    activePlanDb,
                    pipeLayers,
                    attrLayers,
                    keywordHint: "上版");
                if (mdiMatch != null && mdiMatch.IsValid)
                {
                    return mdiMatch;
                }
            }

            // 优先级 3：检查 Config.json 中记忆的上次上版数据路径，或同目录/Data 目录下的候选文件
            string rememberedPath = CadConfig.DataSources.LastPreviousDataDwgPath;
            if (!string.IsNullOrWhiteSpace(rememberedPath) && File.Exists(rememberedPath))
            {
                return CreateExternalFileDescriptor(rememberedPath, pipeLayers, attrLayers);
            }

            string discoveredPath = TryDiscoverSiblingDwgFile(activePlanDb, "上版数据.dwg", "上版");
            if (!string.IsNullOrWhiteSpace(discoveredPath) && File.Exists(discoveredPath))
            {
                return CreateExternalFileDescriptor(discoveredPath, pipeLayers, attrLayers);
            }

            return new DataSourceDescriptor
            {
                Kind = DataSourceKind.Unresolved,
                DisplayName = "未找到上版数据（请指定 DWG）",
                PipeLayers = pipeLayers,
                AttachmentLayers = attrLayers
            };
        }

        /// <summary>
        /// 按三级优先级自动探测【勘测数据】数据源。
        /// </summary>
        public DataSourceDescriptor ResolveSurveyDataSource(
            Database activePlanDb,
            DataSourceDescriptor existingDescriptor = null)
        {
            var pipeLayers = new List<string>(CadConfig.SurveyData.PipeLayers ?? new List<string>());
            var attrLayers = new List<string>(CadConfig.SurveyData.AttributeLayers ?? new List<string>());

            if (existingDescriptor != null && existingDescriptor.IsValid)
            {
                if (existingDescriptor.PipeLayers == null || existingDescriptor.PipeLayers.Count == 0)
                    existingDescriptor.PipeLayers = pipeLayers;
                if (existingDescriptor.AttachmentLayers == null || existingDescriptor.AttachmentLayers.Count == 0)
                    existingDescriptor.AttachmentLayers = attrLayers;
                return existingDescriptor;
            }

            // 优先级 1：检查当前规划图纸内部是否已合并包含完整的勘测管线图层
            if (CadConfig.DataSources.PreferSameDrawingLayers && activePlanDb != null)
            {
                int count = CountCurveEntitiesOnLayers(activePlanDb, pipeLayers, MinSameDrawingEntityCount);
                if (count >= MinSameDrawingEntityCount)
                {
                    return new DataSourceDescriptor
                    {
                        Kind = DataSourceKind.SameDrawing,
                        InMemoryDatabase = activePlanDb,
                        FilePath = SafeGetDatabasePath(activePlanDb),
                        DisplayName = "当前图纸内图层（合图模式）",
                        PipeLayers = pipeLayers,
                        AttachmentLayers = attrLayers
                    };
                }
            }

            // 优先级 2：检查当前 AutoCAD 进程中已打开的其他 MDI 文档标签页
            if (CadConfig.DataSources.PreferOpenMdiDocuments)
            {
                DataSourceDescriptor mdiMatch = TryFindInOpenMdiDocuments(
                    activePlanDb,
                    pipeLayers,
                    attrLayers,
                    keywordHint: "勘测");
                if (mdiMatch != null && mdiMatch.IsValid)
                {
                    return mdiMatch;
                }
            }

            // 优先级 3：检查 Config.json 中记忆的上次勘测数据路径，或同目录/Data 目录下的候选文件
            string rememberedPath = CadConfig.DataSources.LastSurveyDataDwgPath;
            if (!string.IsNullOrWhiteSpace(rememberedPath) && File.Exists(rememberedPath))
            {
                return CreateExternalFileDescriptor(rememberedPath, pipeLayers, attrLayers);
            }

            string discoveredPath = TryDiscoverSiblingDwgFile(activePlanDb, "勘测管.dwg", "勘测");
            if (!string.IsNullOrWhiteSpace(discoveredPath) && File.Exists(discoveredPath))
            {
                return CreateExternalFileDescriptor(discoveredPath, pipeLayers, attrLayers);
            }

            return new DataSourceDescriptor
            {
                Kind = DataSourceKind.Unresolved,
                DisplayName = "未找到勘测数据（请指定 DWG）",
                PipeLayers = pipeLayers,
                AttachmentLayers = attrLayers
            };
        }

        /// <summary>
        /// 根据数据源描述符获取可读的 <see cref="Database"/> 实例。
        /// 若数据源为外部未打开的 DWG 文件，则创建后台 Side Database 只读加载，并通过 <paramref name="shouldDispose"/> 告知调用方在使用完毕后释放。
        /// </summary>
        public Database OpenOrReadDatabase(DataSourceDescriptor descriptor, out bool shouldDispose)
        {
            shouldDispose = false;
            if (descriptor == null || !descriptor.IsValid)
                return null;

            if ((descriptor.Kind == DataSourceKind.SameDrawing || descriptor.Kind == DataSourceKind.OpenMdiDocument) &&
                descriptor.InMemoryDatabase != null)
            {
                return descriptor.InMemoryDatabase;
            }

            if (!string.IsNullOrWhiteSpace(descriptor.FilePath) && File.Exists(descriptor.FilePath))
            {
                // 若该外部文件此时恰好已在 MDI 中打开，优先直接复用内存 Database，避免重复读盘
                Database openDb = TryFindOpenDatabaseByPath(descriptor.FilePath);
                if (openDb != null)
                {
                    return openDb;
                }

                var sideDb = new Database(false, true);
                sideDb.ReadDwgFile(descriptor.FilePath, FileOpenMode.OpenForReadAndAllShare, true, "");
                sideDb.CloseInput(true);
                shouldDispose = true;
                return sideDb;
            }

            return null;
        }

        /// <summary>
        /// 将用户确认或自动解析出的外部 DWG 路径记忆到 Config.json 中。
        /// </summary>
        public void RememberDataSourcePaths(
            DataSourceDescriptor previousSource,
            DataSourceDescriptor surveySource,
            bool saveImmediately = true)
        {
            bool changed = false;

            if (previousSource != null &&
                !string.IsNullOrWhiteSpace(previousSource.FilePath) &&
                File.Exists(previousSource.FilePath))
            {
                if (!string.Equals(CadConfig.DataSources.LastPreviousDataDwgPath, previousSource.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    CadConfig.DataSources.LastPreviousDataDwgPath = previousSource.FilePath;
                    changed = true;
                }
            }

            if (surveySource != null &&
                !string.IsNullOrWhiteSpace(surveySource.FilePath) &&
                File.Exists(surveySource.FilePath))
            {
                if (!string.Equals(CadConfig.DataSources.LastSurveyDataDwgPath, surveySource.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    CadConfig.DataSources.LastSurveyDataDwgPath = surveySource.FilePath;
                    changed = true;
                }
            }

            if (changed && saveImmediately)
            {
                CadConfig.Save();
            }
        }

        /// <summary>
        /// 统计指定数据库 ModelSpace 中位于目标图层列表上的曲线（Curve）实体数量，达到 <paramref name="stopAtCount"/> 即提前终止扫描。
        /// </summary>
        public static int CountCurveEntitiesOnLayers(
            Database db,
            IEnumerable<string> targetLayers,
            int stopAtCount = int.MaxValue)
        {
            if (db == null || targetLayers == null)
                return 0;

            var layerSet = new HashSet<string>(
                targetLayers.Where(l => !string.IsNullOrWhiteSpace(l)),
                StringComparer.OrdinalIgnoreCase);

            if (layerSet.Count == 0)
                return 0;

            int count = 0;
            try
            {
                using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    bool anyLayerExists = false;
                    foreach (string layerName in layerSet)
                    {
                        if (lt.Has(layerName))
                        {
                            anyLayerExists = true;
                            break;
                        }
                    }

                    if (!anyLayerExists)
                        return 0;

                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                    foreach (ObjectId objId in ms)
                    {
                        if (objId.IsErased) continue;

                        Entity ent = tr.GetObject(objId, OpenMode.ForRead, false) as Entity;
                        if (ent is Curve && !string.IsNullOrEmpty(ent.Layer) && layerSet.Contains(ent.Layer))
                        {
                            count++;
                            if (count >= stopAtCount)
                                break;
                        }
                    }

                    tr.Commit();
                }
            }
            catch
            {
                // 忽略异常，返回已统计数量
            }

            return count;
        }

        private static DataSourceDescriptor CreateExternalFileDescriptor(
            string filePath,
            List<string> pipeLayers,
            List<string> attrLayers)
        {
            string fullPath = Path.GetFullPath(filePath);
            return new DataSourceDescriptor
            {
                Kind = DataSourceKind.ExternalDwgFile,
                FilePath = fullPath,
                DisplayName = $"外部文件: {Path.GetFileName(fullPath)}",
                PipeLayers = pipeLayers,
                AttachmentLayers = attrLayers
            };
        }

        private static DataSourceDescriptor TryFindInOpenMdiDocuments(
            Database activePlanDb,
            List<string> pipeLayers,
            List<string> attrLayers,
            string keywordHint)
        {
            try
            {
                var docManager = AcadApp.DocumentManager;
                if (docManager == null)
                    return null;

                foreach (Document doc in docManager)
                {
                    if (doc?.Database == null || ReferenceEquals(doc.Database, activePlanDb))
                        continue;

                    int count = CountCurveEntitiesOnLayers(doc.Database, pipeLayers, MinSameDrawingEntityCount);
                    string docName = doc.Name ?? string.Empty;
                    bool nameMatches = !string.IsNullOrWhiteSpace(keywordHint) &&
                                       docName.IndexOf(keywordHint, StringComparison.OrdinalIgnoreCase) >= 0;

                    if (count >= MinSameDrawingEntityCount || (nameMatches && count > 0))
                    {
                        string filePath = SafeGetDatabasePath(doc.Database);
                        string shortName = !string.IsNullOrWhiteSpace(filePath)
                            ? Path.GetFileName(filePath)
                            : Path.GetFileName(docName);

                        return new DataSourceDescriptor
                        {
                            Kind = DataSourceKind.OpenMdiDocument,
                            InMemoryDatabase = doc.Database,
                            FilePath = filePath,
                            DisplayName = $"已打开文档: {shortName}",
                            PipeLayers = pipeLayers,
                            AttachmentLayers = attrLayers
                        };
                    }
                }
            }
            catch
            {
                // 在无 UI 或独立测试环境下安全忽略
            }

            return null;
        }

        private static Database TryFindOpenDatabaseByPath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return null;

            try
            {
                string normalizedTarget = Path.GetFullPath(filePath);
                var docManager = AcadApp.DocumentManager;
                if (docManager == null)
                    return null;

                foreach (Document doc in docManager)
                {
                    if (doc?.Database == null)
                        continue;

                    string docPath = SafeGetDatabasePath(doc.Database);
                    if (!string.IsNullOrWhiteSpace(docPath) &&
                        string.Equals(Path.GetFullPath(docPath), normalizedTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        return doc.Database;
                    }
                }
            }
            catch
            {
                // 忽略
            }

            return null;
        }

        private static string TryDiscoverSiblingDwgFile(
            Database activePlanDb,
            string preferredExactFileName,
            string keyword)
        {
            var candidateDirs = new List<string>();

            string activeDwgPath = SafeGetDatabasePath(activePlanDb);
            if (!string.IsNullOrWhiteSpace(activeDwgPath))
            {
                try
                {
                    string dir = Path.GetDirectoryName(activeDwgPath);
                    if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    {
                        candidateDirs.Add(dir);
                    }
                }
                catch
                {
                    // 忽略非法路径
                }
            }

            // 补充工作区 Data 目录作为开发/测试默认回退目录
            try
            {
                string assemblyDir = Path.GetDirectoryName(typeof(DataSourceService).Assembly.Location);
                if (!string.IsNullOrWhiteSpace(assemblyDir))
                {
                    string dir = assemblyDir;
                    for (int i = 0; i < 4 && !string.IsNullOrWhiteSpace(dir); i++)
                    {
                        string dataDir = Path.Combine(dir, "Data");
                        if (Directory.Exists(dataDir) && !candidateDirs.Contains(dataDir, StringComparer.OrdinalIgnoreCase))
                        {
                            candidateDirs.Add(dataDir);
                        }
                        dir = Directory.GetParent(dir)?.FullName;
                    }
                }
            }
            catch
            {
                // 忽略
            }

            foreach (string dir in candidateDirs)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(preferredExactFileName))
                    {
                        string exactPath = Path.Combine(dir, preferredExactFileName);
                        if (File.Exists(exactPath))
                            return exactPath;
                    }

                    if (!string.IsNullOrWhiteSpace(keyword))
                    {
                        var matchedFile = Directory.GetFiles(dir, "*.dwg", SearchOption.TopDirectoryOnly)
                            .FirstOrDefault(f => Path.GetFileName(f).IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (!string.IsNullOrWhiteSpace(matchedFile))
                            return matchedFile;
                    }
                }
                catch
                {
                    // 忽略目录访问异常
                }
            }

            return null;
        }

        private static string SafeGetDatabasePath(Database db)
        {
            if (db == null)
                return string.Empty;

            try
            {
                string fn = db.Filename;
                if (!string.IsNullOrWhiteSpace(fn) && File.Exists(fn))
                    return Path.GetFullPath(fn);
            }
            catch
            {
                // 忽略
            }

            return string.Empty;
        }
    }
}
