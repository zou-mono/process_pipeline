using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using process_pipeline.Geometry;
using process_pipeline.Models;

namespace process_pipeline.Core.DataSource
{
    /// <summary>
    /// 数据源承载类型枚举。
    /// </summary>
    public enum DataSourceKind
    {
        /// <summary>
        /// 未配置或未解析。
        /// </summary>
        Unresolved = 0,

        /// <summary>
        /// 数据位于当前激活的规划图纸（MdiActiveDocument）内部的指定图层。
        /// </summary>
        SameDrawing = 1,

        /// <summary>
        /// 数据位于当前 AutoCAD 进程中已打开的另一个 MDI 文档标签页。
        /// </summary>
        OpenMdiDocument = 2,

        /// <summary>
        /// 数据位于磁盘上的外部 DWG 文件（通过后台 Side Database 只读加载）。
        /// </summary>
        ExternalDwgFile = 3
    }

    /// <summary>
    /// 单个数据集（规划数据 / 上版数据 / 勘测数据）的数据源定位描述符。
    /// </summary>
    public class DataSourceDescriptor
    {
        /// <summary>
        /// 数据源承载方式。
        /// </summary>
        public DataSourceKind Kind { get; set; } = DataSourceKind.Unresolved;

        /// <summary>
        /// DWG 文件完整路径（对于外部文件或已保存的文档）。
        /// </summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>
        /// 显示名称（用于 UI 状态栏或数据源设置弹窗展示）。
        /// </summary>
        public string DisplayName { get; set; } = "未指定";

        /// <summary>
        /// 当 Kind 为 SameDrawing 或 OpenMdiDocument 时，关联的内存 Database 引用。
        /// </summary>
        public Database InMemoryDatabase { get; set; }

        /// <summary>
        /// 目标管线图层列表。
        /// </summary>
        public List<string> PipeLayers { get; set; } = new List<string>();

        /// <summary>
        /// 目标属性块或标注图层列表。
        /// </summary>
        public List<string> AttachmentLayers { get; set; } = new List<string>();

        /// <summary>
        /// 该数据源当前是否已有效就绪。
        /// </summary>
        public bool IsValid
        {
            get
            {
                switch (Kind)
                {
                    case DataSourceKind.SameDrawing:
                    case DataSourceKind.OpenMdiDocument:
                        return InMemoryDatabase != null;
                    case DataSourceKind.ExternalDwgFile:
                        return !string.IsNullOrWhiteSpace(FilePath) && File.Exists(FilePath);
                    default:
                        return false;
                }
            }
        }

        /// <summary>
        /// 生成用于缓存索引的唯一签名 Key。
        /// </summary>
        public string GetCacheKey()
        {
            string sourceId;
            if (!string.IsNullOrWhiteSpace(FilePath) && File.Exists(FilePath))
            {
                var fi = new FileInfo(FilePath);
                sourceId = $"{fi.FullName.ToLowerInvariant()}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}";
            }
            else if (InMemoryDatabase != null)
            {
                sourceId = $"MEMDB:{InMemoryDatabase.GetHashCode()}:{InMemoryDatabase.Filename ?? ""}";
            }
            else
            {
                sourceId = "INVALID";
            }

            string layersKey = PipeLayers != null
                ? string.Join(",", PipeLayers.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                : string.Empty;

            return $"{Kind}|{sourceId}|{layersKey}";
        }
    }

    /// <summary>
    /// 规划数据单根管线的多阶段富化属性快照（汇总颜色/图层分类、图面文字标注与流向箭头信息）。
    /// </summary>
    public class EnrichedPlanPipeInfo
    {
        /// <summary>
        /// 规划管线在当前文档中的 ObjectId。
        /// </summary>
        public ObjectId PipeId { get; set; } = ObjectId.Null;

        /// <summary>
        /// 规划管线句柄字符串。
        /// </summary>
        public string Handle { get; set; } = string.Empty;

        /// <summary>
        /// 所在原始图层名。
        /// </summary>
        public string LayerName { get; set; } = string.Empty;

        /// <summary>
        /// 实体 ACI 颜色索引值。
        /// </summary>
        public short ColorAci { get; set; }

        /// <summary>
        /// 按“颜色优先级高于图层名”规则判定出的标准类别 ID（PlanNew / Existing / PlanExpand）。
        /// </summary>
        public string ResolvedCategoryId { get; set; } = string.Empty;

        /// <summary>
        /// 按“颜色优先级高于图层名”规则判定出的标准类别中文名称（规划新建管 / 现状管 / 规划改扩建管）。
        /// </summary>
        public string ResolvedCategoryName { get; set; } = string.Empty;

        /// <summary>
        /// 是否存在“颜色与图层名规则不一致”的待核查异常。
        /// </summary>
        public bool HasColorLayerMismatch { get; set; }

        /// <summary>
        /// 从关联管线文字标注中解析出的管径 WIDTH（毫米，如 400、600；未匹配时为 null）。
        /// </summary>
        public double? TextDiameter { get; set; }

        /// <summary>
        /// 从关联管线文字标注中解析出的坡度 PD（如 1.0、5.6；未包含时为 null）。
        /// </summary>
        public double? TextSlope { get; set; }

        /// <summary>
        /// 从关联管线文字标注中解析出的标注管长 LEN（米；未包含时为 null）。
        /// </summary>
        public double? TextLength { get; set; }

        /// <summary>
        /// 匹配到的管线文字原始文本（如 "d600-i5.6-L1080"）。
        /// </summary>
        public string RawLabelText { get; set; } = string.Empty;

        /// <summary>
        /// 匹配到的文字实体句柄。
        /// </summary>
        public string MatchedTextHandle { get; set; } = string.Empty;

        /// <summary>
        /// 从流向箭头核查中提取出的流向角度（度，0~360）或流向描述。
        /// </summary>
        public double? FlowDirectionAngle { get; set; }

        /// <summary>
        /// 流向是否与管线绘制方向一致（无反向冲突）。
        /// </summary>
        public bool? IsFlowDirectionConsistent { get; set; }

        /// <summary>
        /// 匹配到的最近流向箭头块句柄（Stage 4 Commit 4.2）。
        /// </summary>
        public string MatchedArrowHandle { get; set; } = string.Empty;

        /// <summary>
        /// 流向核查状态简述（如 "顺向" / "反向" / "多箭头冲突" / "无箭头"，Stage 4 Commit 4.2）。
        /// </summary>
        public string FlowDirectionText { get; set; } = string.Empty;

        /// <summary>
        /// 对应的几何与属性包装对象。
        /// </summary>
        public CadPolyline Polyline { get; set; }

        /// <summary>
        /// 将富化提取到的属性同步写入 Polyline.Attributes 字典（供 MatchPipe / CompareSegment 统一读取）。
        /// </summary>
        public void SyncToPolylineAttributes()
        {
            if (Polyline == null)
                return;

            if (Polyline.Attributes == null)
                Polyline.Attributes = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(ResolvedCategoryId))
                Polyline.Attributes["CATEGORY_ID"] = ResolvedCategoryId;

            if (!string.IsNullOrWhiteSpace(ResolvedCategoryName))
                Polyline.Attributes["GHZT"] = ResolvedCategoryName;

            if (TextDiameter.HasValue)
                Polyline.Attributes["WIDTH"] = TextDiameter.Value;

            if (TextSlope.HasValue)
                Polyline.Attributes["PD"] = TextSlope.Value;

            if (TextLength.HasValue)
                Polyline.Attributes["LEN"] = TextLength.Value;

            if (!string.IsNullOrWhiteSpace(RawLabelText))
                Polyline.Attributes["RAW_LABEL"] = RawLabelText;

            if (FlowDirectionAngle.HasValue)
                Polyline.Attributes["LX_ANGLE"] = FlowDirectionAngle.Value;

            if (IsFlowDirectionConsistent.HasValue)
            {
                Polyline.Attributes["LX_VALID"] = IsFlowDirectionConsistent.Value;
                Polyline.Attributes["LX"] = IsFlowDirectionConsistent.Value ? "0" : "1";
            }

            if (!string.IsNullOrWhiteSpace(MatchedArrowHandle))
                Polyline.Attributes["ARROW_HANDLE"] = MatchedArrowHandle;

            if (!string.IsNullOrWhiteSpace(FlowDirectionText))
                Polyline.Attributes["LX_DESC"] = FlowDirectionText;
        }
    }

    /// <summary>
    /// 勘测数据只读静态缓存条目（包含原始多段线、按管径拓扑合并后的多段线及 STRtree 空间索引）。
    /// </summary>
    public class SurveyDatasetCacheEntry
    {
        public string CacheKey { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public List<CadPolyline> RawPolylines { get; set; } = new List<CadPolyline>();
        public List<CadPolyline> MergedPolylines { get; set; } = new List<CadPolyline>();
        public STRtree<CadPolyline> MergedPolylineIndex { get; set; } = new STRtree<CadPolyline>();
        public STRtree<CadSegment> MergedSegmentIndex { get; set; } = new STRtree<CadSegment>();
    }

    /// <summary>
    /// 管线核查三方数据集上下文（Commit 2.3）。
    /// 
    /// 核心职责：
    /// 1. 统一收拢【规划数据】的多阶段提取属性（颜色分类、文字管径/坡度/管长、流向箭头）；
    /// 2. 针对【勘测数据（只读基准）】提供跨会话静态拓扑合并与 STRtree 空间索引缓存，避免重复耗时计算；
    /// 3. 针对【上版数据（核查期可编辑）】提供动态缓存与局部失效/增量更新机制，支撑双向编辑增量感知。
    /// </summary>
    public class PipeDatasetContext
    {
        private static readonly object _surveyCacheLock = new object();
        private static readonly Dictionary<string, SurveyDatasetCacheEntry> _readonlySurveyCache =
            new Dictionary<string, SurveyDatasetCacheEntry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 当前关联的【上版数据】数据源描述符。
        /// </summary>
        public DataSourceDescriptor PreviousDataSource { get; set; } = new DataSourceDescriptor();

        /// <summary>
        /// 当前关联的【勘测数据】数据源描述符。
        /// </summary>
        public DataSourceDescriptor SurveyDataSource { get; set; } = new DataSourceDescriptor();

        // ============================================================
        // 1. 规划数据（当前文档，支持随时编辑与富化属性汇聚）
        // ============================================================

        /// <summary>
        /// 规划管线富化信息表（按 ObjectId 索引）。
        /// </summary>
        public Dictionary<ObjectId, EnrichedPlanPipeInfo> PlanPipesById { get; } =
            new Dictionary<ObjectId, EnrichedPlanPipeInfo>();

        /// <summary>
        /// 规划管线富化信息表（按十六进制 Handle 字符串索引，忽略大小写）。
        /// </summary>
        public Dictionary<string, EnrichedPlanPipeInfo> PlanPipesByHandle { get; } =
            new Dictionary<string, EnrichedPlanPipeInfo>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 注册或更新一条规划管线的富化属性记录。
        /// </summary>
        public EnrichedPlanPipeInfo GetOrCreatePlanPipeInfo(ObjectId pipeId, string handle = null)
        {
            if (!pipeId.IsNull && PlanPipesById.TryGetValue(pipeId, out EnrichedPlanPipeInfo existing))
            {
                return existing;
            }

            string resolvedHandle = !string.IsNullOrWhiteSpace(handle)
                ? handle
                : (!pipeId.IsNull && pipeId.IsValid ? pipeId.Handle.ToString() : string.Empty);

            if (!string.IsNullOrWhiteSpace(resolvedHandle) &&
                PlanPipesByHandle.TryGetValue(resolvedHandle, out EnrichedPlanPipeInfo byHandle))
            {
                if (!pipeId.IsNull)
                    PlanPipesById[pipeId] = byHandle;
                return byHandle;
            }

            var info = new EnrichedPlanPipeInfo
            {
                PipeId = pipeId,
                Handle = resolvedHandle
            };

            if (!pipeId.IsNull)
                PlanPipesById[pipeId] = info;

            if (!string.IsNullOrWhiteSpace(resolvedHandle))
                PlanPipesByHandle[resolvedHandle] = info;

            return info;
        }

        /// <summary>
        /// 移除单条规划管线缓存（当规划管线被删除或需重算时调用）。
        /// </summary>
        public void RemovePlanPipeInfo(ObjectId pipeId)
        {
            if (PlanPipesById.TryGetValue(pipeId, out EnrichedPlanPipeInfo info))
            {
                PlanPipesById.Remove(pipeId);
                if (!string.IsNullOrWhiteSpace(info.Handle))
                {
                    PlanPipesByHandle.Remove(info.Handle);
                }
            }
        }

        // ============================================================
        // 2. 上版数据（核查过程中允许人工修正，支持动态失效与增量更新）
        // ============================================================

        /// <summary>
        /// 上版数据缓存是否有效（当上版数据图层发生编辑时自动置为 false）。
        /// </summary>
        public bool IsPreviousDataCacheValid { get; private set; }

        /// <summary>
        /// 上版数据多段线列表（含 Group 属性）。
        /// </summary>
        public List<CadPolyline> PreviousPolylines { get; private set; } = new List<CadPolyline>();

        /// <summary>
        /// 上版数据按 Handle 索引字典。
        /// </summary>
        public Dictionary<string, CadPolyline> PreviousPolylinesByHandle { get; } =
            new Dictionary<string, CadPolyline>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 上版数据多段线级 STRtree 空间索引。
        /// </summary>
        public STRtree<CadPolyline> PreviousPolylineIndex { get; private set; } = new STRtree<CadPolyline>();

        /// <summary>
        /// 上版数据折线段级 STRtree 空间索引（供 CompareSegment 极速查询候选同源线段）。
        /// </summary>
        public STRtree<CadSegment> PreviousSegmentIndex { get; private set; } = new STRtree<CadSegment>();

        /// <summary>
        /// 使用最新读取的上版多段线集合刷新动态缓存与 STRtree 空间索引。
        /// </summary>
        public void UpdatePreviousDataset(List<CadPolyline> polylines)
        {
            PreviousPolylines = polylines ?? new List<CadPolyline>();
            PreviousPolylinesByHandle.Clear();

            var polyTree = new STRtree<CadPolyline>();
            var segTree = new STRtree<CadSegment>();

            foreach (CadPolyline pl in PreviousPolylines)
            {
                if (pl == null) continue;

                if (pl.SourceHandles != null)
                {
                    foreach (string h in pl.SourceHandles)
                    {
                        if (!string.IsNullOrWhiteSpace(h))
                            PreviousPolylinesByHandle[h] = pl;
                    }
                }

                if (pl.Geometry != null && !pl.Geometry.IsEmpty)
                {
                    polyTree.Insert(pl.Geometry.EnvelopeInternal, pl);
                }

                if (pl.Segments != null)
                {
                    foreach (CadSegment seg in pl.Segments)
                    {
                        if (seg?.Envelope != null)
                        {
                            segTree.Insert(seg.Envelope, seg);
                        }
                    }
                }
            }

            polyTree.Build();
            segTree.Build();

            PreviousPolylineIndex = polyTree;
            PreviousSegmentIndex = segTree;
            IsPreviousDataCacheValid = true;
        }

        /// <summary>
        /// 标记上版数据缓存失效（当监听到上版数据管线、ws 属性块或 Group 发生增删改时调用）。
        /// </summary>
        public void InvalidatePreviousData()
        {
            IsPreviousDataCacheValid = false;
        }

        /// <summary>
        /// 根据给定包围盒查询附近的上版数据多段线候选集（供上版数据被编辑时反向查找受影响的规划管线）。
        /// </summary>
        public IList<CadPolyline> QueryPreviousPolylines(Envelope searchEnvelope)
        {
            if (searchEnvelope == null || PreviousPolylineIndex == null)
                return Array.Empty<CadPolyline>();

            return PreviousPolylineIndex.Query(searchEnvelope);
        }

        // ============================================================
        // 3. 勘测数据（只读基准数据，全局静态缓存合并结果与空间索引）
        // ============================================================

        /// <summary>
        /// 获取或构建只读勘测数据的拓扑合并缓存与 STRtree 空间索引。
        /// </summary>
        public SurveyDatasetCacheEntry GetOrBuildSurveyCache(
            Database surveyDb,
            IEnumerable<string> surveyLayers,
            GeometryFactory geometryFactory = null,
            PolylineMergeOptions mergeOptions = null)
        {
            if (surveyDb == null)
                throw new ArgumentNullException(nameof(surveyDb));

            var layerList = (surveyLayers ?? CadConfig.SurveyData.PipeLayers ?? new List<string> { "勘测管" })
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var gf = geometryFactory ?? NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
            var opts = mergeOptions ?? new PolylineMergeOptions
            {
                NodeTolerance = CadConfig.Algorithms.PipeMerge.NodeTolerance,
                MinPolylineLength = CadConfig.Algorithms.PipeMerge.MinPolylineLength,
                MergeAttributeNames = new List<string>(CadConfig.Algorithms.PipeMerge.MergeAttributeNames ?? new List<string> { "WIDTH" }),
                RequireSameLayer = true
            };

            string sourceKey = SurveyDataSource != null && SurveyDataSource.IsValid
                ? SurveyDataSource.GetCacheKey()
                : $"DB:{surveyDb.GetHashCode()}:{surveyDb.Filename ?? ""}|{string.Join(",", layerList)}";
            string fullKey = $"{sourceKey}|TOL:{opts.NodeTolerance:F4}|ATTR:{string.Join(",", opts.MergeAttributeNames ?? new List<string>())}";

            lock (_surveyCacheLock)
            {
                if (_readonlySurveyCache.TryGetValue(fullKey, out SurveyDatasetCacheEntry cached) && cached != null)
                {
                    return cached;
                }

                var rawPolylines = new List<CadPolyline>();
                foreach (string layer in layerList)
                {
                    var extracted = GeometryHelper.CollectPolylinesFromLayer(surveyDb, layer, gf, "survey");
                    if (extracted != null && extracted.Count > 0)
                    {
                        rawPolylines.AddRange(extracted);
                    }
                }

                var merger = new PolylineGraphMerger(gf);
                List<CadPolyline> mergedPolylines = merger.Merge(rawPolylines, opts) ?? new List<CadPolyline>();

                var polyTree = new STRtree<CadPolyline>();
                var segTree = new STRtree<CadSegment>();

                foreach (CadPolyline pl in mergedPolylines)
                {
                    if (pl == null) continue;
                    if (pl.Geometry != null && !pl.Geometry.IsEmpty)
                    {
                        polyTree.Insert(pl.Geometry.EnvelopeInternal, pl);
                    }
                    if (pl.Segments != null)
                    {
                        foreach (CadSegment seg in pl.Segments)
                        {
                            if (seg?.Envelope != null)
                            {
                                segTree.Insert(seg.Envelope, seg);
                            }
                        }
                    }
                }

                polyTree.Build();
                segTree.Build();

                var entry = new SurveyDatasetCacheEntry
                {
                    CacheKey = fullKey,
                    RawPolylines = rawPolylines,
                    MergedPolylines = mergedPolylines,
                    MergedPolylineIndex = polyTree,
                    MergedSegmentIndex = segTree
                };

                _readonlySurveyCache[fullKey] = entry;
                return entry;
            }
        }

        /// <summary>
        /// 清空全局只读勘测数据静态缓存（仅在手动切换勘测文件或强制重置时调用）。
        /// </summary>
        public static void ClearGlobalSurveyCache()
        {
            lock (_surveyCacheLock)
            {
                _readonlySurveyCache.Clear();
            }
        }

        /// <summary>
        /// 清空当前会话的动态数据集上下文（保留全局静态只读勘测缓存）。
        /// </summary>
        public void ClearSessionData()
        {
            PlanPipesById.Clear();
            PlanPipesByHandle.Clear();
            PreviousPolylines.Clear();
            PreviousPolylinesByHandle.Clear();
            PreviousPolylineIndex = new STRtree<CadPolyline>();
            PreviousSegmentIndex = new STRtree<CadSegment>();
            IsPreviousDataCacheValid = false;
        }
    }
}
