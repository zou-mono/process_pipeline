using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using process_pipeline.Core;
using process_pipeline.Core.Config;
using process_pipeline.Core.DataSource;
using process_pipeline.Models;
using process_pipeline.Utils;
using Database = Autodesk.AutoCAD.DatabaseServices.Database;

namespace process_pipeline.Geometry
{
    /// <summary>
    /// 管线与邻近附属要素（属性块 / 文字标注）空间配对状态枚举（Stage 3 Commit 3.1）。
    /// </summary>
    public enum SpatialMatchStatus
    {
        /// <summary>
        /// 1:1 唯一高置信度匹配（通过已绑定 Group、XData 或空间唯一匹配）。
        /// </summary>
        UniqueMatch = 0,

        /// <summary>
        /// 存在一对多或多对一空间歧义（距离/管长相近，需人工复核，附带 PossibleMatches 候选连线）。
        /// </summary>
        OneToManyConflict = 1,

        /// <summary>
        /// 管线未找到任何可配对的属性块或文字标注。
        /// </summary>
        NoMatchPipe = 2,

        /// <summary>
        /// 孤立的属性块或文字标注（未配对到任何管线）。
        /// </summary>
        OrphanAttachment = 3,

        /// <summary>
        /// 原生 CAD Group 结构异常（例如一个 Group 内包含多条管线、多个属性块或缺少管线/属性块）。
        /// </summary>
        InvalidGroupStructure = 4
    }

    /// <summary>
    /// 配对命中来源渠道枚举。
    /// </summary>
    public enum SpatialMatchChannel
    {
        None = 0,
        CadGroup = 1,
        XDataLink = 2,
        ExtentsCenterMatch = 3,
        CurveProximityMatch = 4
    }

    /// <summary>
    /// 待配对的目标管线几何包装项。
    /// </summary>
    public class SpatialPipeTarget
    {
        public ObjectId PipeId { get; set; } = ObjectId.Null;
        public string Handle { get; set; } = string.Empty;
        public string LayerName { get; set; } = string.Empty;
        public LineString Geometry { get; set; }
        public double Length => Geometry != null ? Geometry.Length : 0.0;
        public Point3d ExtentsCenter { get; set; }
        public Point3d MidPoint { get; set; }
        public Envelope Envelope { get; set; }
        public string XDataLinkedAttachmentHandle { get; set; } = string.Empty;
    }

    /// <summary>
    /// 待配对的附属要素（属性块 BlockReference 或文字标注 DBText/MText）包装项。
    /// </summary>
    public class SpatialAttachmentItem
    {
        public ObjectId AttachmentId { get; set; } = ObjectId.Null;
        public string Handle { get; set; } = string.Empty;
        public string LayerName { get; set; } = string.Empty;
        public string BlockNameOrText { get; set; } = string.Empty;
        public Point3d Position { get; set; }
        public double? RotationDegrees { get; set; }
        public double? DeclaredLength { get; set; }
        public Dictionary<string, object> Attributes { get; set; } =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        public string XDataLinkedPipeHandle { get; set; } = string.Empty;
    }

    /// <summary>
    /// 单条管线与附属要素的配对明细结果。
    /// </summary>
    public class PipeAttachmentMatchItem
    {
        public SpatialMatchStatus Status { get; set; }
        public SpatialMatchChannel Channel { get; set; }
        public SpatialPipeTarget Pipe { get; set; }
        public SpatialAttachmentItem MatchedAttachment { get; set; }
        public ObjectId GroupId { get; set; } = ObjectId.Null;
        public string GroupName { get; set; } = string.Empty;
        public double DistanceToCurve { get; set; } = double.MaxValue;
        public double DistanceToExtentsCenter { get; set; } = double.MaxValue;
        public double? LengthDifference { get; set; }
        public double? AngleDifferenceDegrees { get; set; }
        public Point3d ClosestPointOnPipe { get; set; }
        public string Description { get; set; } = string.Empty;
        public List<MatchItem> PossibleMatches { get; set; } = new List<MatchItem>();
    }

    /// <summary>
    /// 管线与附属要素全图配对汇总报告（Commit 3.1）。
    /// </summary>
    public class PipeSpatialMatchSummary
    {
        public int TotalPipeCount { get; set; }
        public int TotalAttachmentCount { get; set; }
        public int TotalExistingGroupCount { get; set; }
        public int ValidGroupPairCount { get; set; }
        public int InvalidGroupCount { get; set; }
        public int XDataMatchedCount { get; set; }
        public int ExtentsCenterMatchedCount { get; set; }
        public int CurveProximityMatchedCount { get; set; }

        public List<PipeAttachmentMatchItem> UniqueMatches { get; } = new List<PipeAttachmentMatchItem>();
        public List<PipeAttachmentMatchItem> ConflictMatches { get; } = new List<PipeAttachmentMatchItem>();
        public List<PipeAttachmentMatchItem> UnmatchedPipes { get; } = new List<PipeAttachmentMatchItem>();
        public List<PipeAttachmentMatchItem> OrphanAttachments { get; } = new List<PipeAttachmentMatchItem>();
        public List<PipeAttachmentMatchItem> InvalidGroups { get; } = new List<PipeAttachmentMatchItem>();

        /// <summary>
        /// 按管线 Handle 快速检索配对成功（含唯一匹配与有首选候选的歧义项）的属性字典。
        /// </summary>
        public Dictionary<string, Dictionary<string, object>> MatchedAttributesByPipeHandle { get; } =
            new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 按管线 ObjectId 快速检索配对结果。
        /// </summary>
        public Dictionary<ObjectId, PipeAttachmentMatchItem> MatchByPipeId { get; } =
            new Dictionary<ObjectId, PipeAttachmentMatchItem>();
    }

    /// <summary>
    /// 空间邻近配对算法参数选项。
    /// </summary>
    public class PipeSpatialMatchOptions
    {
        /// <summary>
        /// 属性块插入点到管线曲线本体的最大搜索距离（米，默认读取 Config.json 的 MaxPointToCurveDistance = 5.0m）。
        /// </summary>
        public double MaxCurveDistance { get; set; } = 5.0;

        /// <summary>
        /// 属性块插入点到管线几何外包框中心（ExtentsCenter）的最大容差（米，默认 1.5m）。
        /// 用于精准捕获 GIS 导出的上版数据 ws 属性块（包括外包框中心远离曲线的大弯折 L/U 型管线）。
        /// </summary>
        public double ExtentsCenterTolerance { get; set; } = 1.5;

        /// <summary>
        /// 当多个候选距离相差在此阈值以内（米）且无法通过管长 LEN 区分时，判定为一对多空间歧义（默认 0.5m）。
        /// </summary>
        public double AmbiguityDistanceMargin { get; set; } = 0.5;

        /// <summary>
        /// 校验属性块 LEN 字段与管线几何长度时的容差（米，默认 1.5m）。
        /// </summary>
        public double LengthMatchTolerance { get; set; } = 1.5;

        /// <summary>
        /// 是否启用文字/块旋转角与管线段方向的平行性校验（用于阶段 5 管线文字标注匹配）。
        /// </summary>
        public bool CheckParallelAngle { get; set; } = false;

        /// <summary>
        /// 平行角度容差（度，0~90，默认 30 度）。
        /// </summary>
        public double ParallelAngleToleranceDegrees { get; set; } = 30.0;

        /// <summary>
        /// 是否优先读取 CAD 原生 GroupDictionary 中的既有配对关系（默认 true）。
        /// </summary>
        public bool UseExistingCadGroups { get; set; } = true;

        /// <summary>
        /// 是否读取 XData (PIPE_CHECK_LINK) 中的持久化配对句柄（默认 true）。
        /// </summary>
        public bool UseXDataLinks { get; set; } = true;

        /// <summary>
        /// 属性块中表示“管线长度”的候选字段名（Tag）别名列表（默认从 Config.json 的 SpatialMatch.LengthAttributeAliases 读取）。
        /// </summary>
        public List<string> LengthAttributeAliases { get; set; } = new List<string>
        {
            "LEN", "LENGTH", "PIPE_LEN", "管长", "长度", "CD"
        };

        /// <summary>
        /// 属性块中表示“管径/宽度”的候选字段名（Tag）别名列表。
        /// </summary>
        public List<string> WidthAttributeAliases { get; set; } = new List<string>
        {
            "WIDTH", "DN", "DIAMETER", "管径", "宽度", "GJ"
        };

        /// <summary>
        /// 属性块中表示“断面高度”的候选字段名（Tag）别名列表（兼容 HEITH 与 HEIGHT）。
        /// </summary>
        public List<string> HeightAttributeAliases { get; set; } = new List<string>
        {
            "HEITH", "HEIGHT", "高度", "GD"
        };

        public static PipeSpatialMatchOptions FromConfig()
        {
            var spatialCfg = CadConfig.Current?.Algorithms?.SpatialMatch;
            if (spatialCfg == null)
                return new PipeSpatialMatchOptions();

            return new PipeSpatialMatchOptions
            {
                MaxCurveDistance = spatialCfg.MaxSearchDistance > 0 ? spatialCfg.MaxSearchDistance : 25.0,
                ExtentsCenterTolerance = spatialCfg.ExtentsCenterTolerance > 0 ? spatialCfg.ExtentsCenterTolerance : 1.5,
                LengthMatchTolerance = spatialCfg.LengthTolerance > 0 ? spatialCfg.LengthTolerance : 1.5,
                ParallelAngleToleranceDegrees = spatialCfg.TextAngleTolerance > 0 ? spatialCfg.TextAngleTolerance : 35.0,
                AmbiguityDistanceMargin = 0.5,
                LengthAttributeAliases = spatialCfg.LengthAttributeAliases != null && spatialCfg.LengthAttributeAliases.Count > 0
                    ? new List<string>(spatialCfg.LengthAttributeAliases)
                    : new List<string> { "LEN", "LENGTH", "PIPE_LEN", "管长", "长度", "CD" },
                WidthAttributeAliases = spatialCfg.WidthAttributeAliases != null && spatialCfg.WidthAttributeAliases.Count > 0
                    ? new List<string>(spatialCfg.WidthAttributeAliases)
                    : new List<string> { "WIDTH", "DN", "DIAMETER", "管径", "宽度", "GJ" },
                HeightAttributeAliases = spatialCfg.HeightAttributeAliases != null && spatialCfg.HeightAttributeAliases.Count > 0
                    ? new List<string>(spatialCfg.HeightAttributeAliases)
                    : new List<string> { "HEITH", "HEIGHT", "高度", "GD" }
            };
        }
    }

    /// <summary>
    /// 通用管线邻近要素空间匹配器（Stage 3 Commit 3.1）。
    /// 
    /// 核心特性：
    /// 1. 支持三级配对优先级：CAD GroupDictionary -> XData (PIPE_CHECK_LINK) -> 空间双距离判据匹配；
    /// 2. 空间双距离判据融合：
    ///    - 判据 A（外包框中心对齐）：针对 GIS 导出的上版数据 ws 属性块，即使管线呈 L/U 型大弯折（块距曲线高达 159.7m），
    ///      只要块坐标与管线外包框中心重合（DistToExtentsCenter &lt;= 1.5m）且 LEN 管长吻合，即可 100% 精准配对；
    ///    - 判据 B（曲线本体邻近 + 可选平行角/管长校验）：针对勘测管属性块、人工摆放属性块及规划管线文字标注；
    /// 3. 自动检出 1:N / N:1 空间歧义并生成 PossibleMatches 瞬态辅助连线数据。
    /// </summary>
    public class PipeSpatialMatcher
    {
        /// <summary>
        /// 图属持久化 XData 注册应用名（Commit 3.1 / 3.2）。
        /// </summary>
        public const string XDataRegAppName = "PIPE_CHECK_LINK";

        private readonly GeometryFactory _geometryFactory;

        public PipeSpatialMatcher(GeometryFactory geometryFactory = null)
        {
            _geometryFactory = geometryFactory ?? NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
        }

        /// <summary>
        /// 对指定 CAD 数据库中的管线图层与属性块图层执行完整的三级（Group -> XData -> 空间双判据）配对分析。
        /// </summary>
        public PipeSpatialMatchSummary MatchDatabaseBlockAttributes(
            Database db,
            IEnumerable<string> pipeLayers,
            IEnumerable<string> blockLayers,
            PipeSpatialMatchOptions options = null,
            ProgressContext progress = null)
        {
            if (db == null)
                throw new ArgumentNullException(nameof(db));

            options = options ?? PipeSpatialMatchOptions.FromConfig();

            var pipeLayerSet = new HashSet<string>(
                (pipeLayers ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()),
                StringComparer.OrdinalIgnoreCase);

            var blockLayerSet = new HashSet<string>(
                (blockLayers ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()),
                StringComparer.OrdinalIgnoreCase);

            var pipesById = new Dictionary<ObjectId, SpatialPipeTarget>();
            var pipesByHandle = new Dictionary<string, SpatialPipeTarget>(StringComparer.OrdinalIgnoreCase);
            var attachmentsById = new Dictionary<ObjectId, SpatialAttachmentItem>();
            var attachmentsByHandle = new Dictionary<string, SpatialAttachmentItem>(StringComparer.OrdinalIgnoreCase);

            var summary = new PipeSpatialMatchSummary();
            var matchedPipeIds = new HashSet<ObjectId>();
            var matchedAttachmentIds = new HashSet<ObjectId>();

            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                // 1. 扫描 ModelSpace 提取目标图层上的全部管线与属性块
                foreach (ObjectId objId in ms)
                {
                    if (objId.IsErased) continue;

                    Entity ent = tr.GetObject(objId, OpenMode.ForRead, false) as Entity;
                    if (ent == null || string.IsNullOrWhiteSpace(ent.Layer))
                        continue;

                    if (ent is Polyline pl && pipeLayerSet.Contains(ent.Layer))
                    {
                        SpatialPipeTarget pipeTarget = CreatePipeTarget(pl, objId, _geometryFactory);
                        if (pipeTarget != null)
                        {
                            pipesById[objId] = pipeTarget;
                            pipesByHandle[pipeTarget.Handle] = pipeTarget;
                        }
                    }
                    else if (ent is Line line && pipeLayerSet.Contains(ent.Layer))
                    {
                        SpatialPipeTarget pipeTarget = CreatePipeTargetFromLine(line, objId, _geometryFactory);
                        if (pipeTarget != null)
                        {
                            pipesById[objId] = pipeTarget;
                            pipesByHandle[pipeTarget.Handle] = pipeTarget;
                        }
                    }

                    if (ent is BlockReference br && blockLayerSet.Contains(ent.Layer))
                    {
                        SpatialAttachmentItem attItem = CreateAttachmentFromBlock(br, objId, tr, options);
                        if (attItem != null)
                        {
                            attachmentsById[objId] = attItem;
                            attachmentsByHandle[attItem.Handle] = attItem;
                        }
                    }
                }

                summary.TotalPipeCount = pipesById.Count;
                summary.TotalAttachmentCount = attachmentsById.Count;

                // 2. 第一级：校验并提取 CAD 原生 GroupDictionary 中的绑定关系
                if (options.UseExistingCadGroups)
                {
                    DBDictionary groupDict = tr.GetObject(db.GroupDictionaryId, OpenMode.ForRead) as DBDictionary;
                    if (groupDict != null && groupDict.Count > 0)
                    {
                        summary.TotalExistingGroupCount = groupDict.Count;
                        foreach (DBDictionaryEntry entry in groupDict)
                        {
                            Group grp = tr.GetObject(entry.Value, OpenMode.ForRead, false) as Group;
                            if (grp == null || grp.IsErased)
                                continue;

                            ObjectId[] entIds = grp.GetAllEntityIds();
                            if (entIds == null || entIds.Length == 0)
                                continue;

                            var groupPipes = new List<SpatialPipeTarget>();
                            var groupAttachments = new List<SpatialAttachmentItem>();

                            foreach (ObjectId eid in entIds)
                            {
                                if (pipesById.TryGetValue(eid, out SpatialPipeTarget p))
                                {
                                    groupPipes.Add(p);
                                }
                                else if (attachmentsById.TryGetValue(eid, out SpatialAttachmentItem a))
                                {
                                    groupAttachments.Add(a);
                                }
                                else if (!eid.IsNull && !eid.IsErased)
                                {
                                    // 兼容处理：若 Group 内包含目标管线，但配对的 BlockReference 位于其他未顯式声明图层，也尝试按块属性提取
                                    if (tr.GetObject(eid, OpenMode.ForRead, false) is BlockReference fallbackBr)
                                    {
                                        SpatialAttachmentItem fallbackAtt = CreateAttachmentFromBlock(fallbackBr, eid, tr, options);
                                        if (fallbackAtt != null && fallbackAtt.Attributes.Count > 0)
                                        {
                                            attachmentsById[eid] = fallbackAtt;
                                            attachmentsByHandle[fallbackAtt.Handle] = fallbackAtt;
                                            groupAttachments.Add(fallbackAtt);
                                        }
                                    }
                                }
                            }

                            // 若该 Group 与目标管线/属性块图层均无关，则跳过
                            if (groupPipes.Count == 0 && groupAttachments.Count == 0)
                                continue;

                            if (groupPipes.Count == 1 && groupAttachments.Count == 1)
                            {
                                SpatialPipeTarget p = groupPipes[0];
                                SpatialAttachmentItem a = groupAttachments[0];

                                if (!matchedPipeIds.Contains(p.PipeId) && !matchedAttachmentIds.Contains(a.AttachmentId))
                                {
                                    matchedPipeIds.Add(p.PipeId);
                                    matchedAttachmentIds.Add(a.AttachmentId);
                                    summary.ValidGroupPairCount++;

                                    double distCurve = ComputePointToLineStringDistance(a.Position, p.Geometry, out Point3d closestPt, out _);
                                    double distCenter = Distance2D(a.Position, p.ExtentsCenter);

                                    var matchItem = new PipeAttachmentMatchItem
                                    {
                                        Status = SpatialMatchStatus.UniqueMatch,
                                        Channel = SpatialMatchChannel.CadGroup,
                                        Pipe = p,
                                        MatchedAttachment = a,
                                        GroupId = entry.Value,
                                        GroupName = entry.Key,
                                        DistanceToCurve = distCurve,
                                        DistanceToExtentsCenter = distCenter,
                                        ClosestPointOnPipe = closestPt,
                                        PossibleMatches = new List<MatchItem>
                                        {
                                            new MatchItem(a.AttachmentId, a.Position, distCurve, closestPt, a.RotationDegrees ?? 0.0, 0.0, true)
                                        },
                                        Description = $"已通过 CAD Group [{entry.Key}] 1:1 绑定"
                                    };

                                    RegisterUniqueMatch(summary, matchItem);
                                }
                                else
                                {
                                    summary.InvalidGroupCount++;
                                    summary.InvalidGroups.Add(new PipeAttachmentMatchItem
                                    {
                                        Status = SpatialMatchStatus.InvalidGroupStructure,
                                        Channel = SpatialMatchChannel.CadGroup,
                                        Pipe = p,
                                        MatchedAttachment = a,
                                        GroupId = entry.Value,
                                        GroupName = entry.Key,
                                        ClosestPointOnPipe = p != null ? p.MidPoint : a.Position,
                                        Description = $"Group [{entry.Key}] 中的管线 [{p.Handle}] 或属性块 [{a.Handle}] 已被其他 Group 重复引用"
                                    });
                                }
                            }
                            else
                            {
                                SpatialPipeTarget firstPipe = groupPipes.FirstOrDefault();
                                SpatialAttachmentItem firstAtt = groupAttachments.FirstOrDefault();
                                summary.InvalidGroupCount++;
                                summary.InvalidGroups.Add(new PipeAttachmentMatchItem
                                {
                                    Status = SpatialMatchStatus.InvalidGroupStructure,
                                    Channel = SpatialMatchChannel.CadGroup,
                                    Pipe = firstPipe,
                                    MatchedAttachment = firstAtt,
                                    GroupId = entry.Value,
                                    GroupName = entry.Key,
                                    ClosestPointOnPipe = firstPipe != null ? firstPipe.MidPoint : (firstAtt != null ? firstAtt.Position : Point3d.Origin),
                                    PossibleMatches = groupAttachments
                                        .Select(att => new MatchItem(
                                            att.AttachmentId,
                                            att.Position,
                                            firstPipe != null ? Distance2D(att.Position, firstPipe.MidPoint) : 0.0,
                                            firstPipe != null ? firstPipe.MidPoint : att.Position,
                                            att.RotationDegrees ?? 0.0,
                                            0.0,
                                            true))
                                        .ToList(),
                                    Description = $"Group [{entry.Key}] 成员结构异常：包含 {groupPipes.Count} 条管线、{groupAttachments.Count} 个属性块（应为 1:1）"
                                });
                            }
                        }
                    }
                }

                // 3. 第二级：对尚未通过 Group 匹配的管线，尝试通过 XData (PIPE_CHECK_LINK) 双向恢复配对
                if (options.UseXDataLinks)
                {
                    // 3.1 正向：通过管线上的 XDataLinkedAttachmentHandle 查找属性块
                    foreach (SpatialPipeTarget pipe in pipesById.Values)
                    {
                        if (matchedPipeIds.Contains(pipe.PipeId))
                            continue;

                        if (string.IsNullOrWhiteSpace(pipe.XDataLinkedAttachmentHandle))
                            continue;

                        if (!attachmentsByHandle.TryGetValue(pipe.XDataLinkedAttachmentHandle, out SpatialAttachmentItem linkedAtt))
                        {
                            // 尝试直接按 Handle 在数据库中定位块参照（兼容块图层未显式传入的情况）
                            try
                            {
                                long handleVal = Convert.ToInt64(pipe.XDataLinkedAttachmentHandle.Trim(), 16);
                                if (db.TryGetObjectId(new Handle(handleVal), out ObjectId linkedObjId) &&
                                    !linkedObjId.IsNull && !linkedObjId.IsErased &&
                                    tr.GetObject(linkedObjId, OpenMode.ForRead, false) is BlockReference linkedBr)
                                {
                                    linkedAtt = CreateAttachmentFromBlock(linkedBr, linkedObjId, tr, options);
                                    if (linkedAtt != null)
                                    {
                                        attachmentsById[linkedObjId] = linkedAtt;
                                        attachmentsByHandle[linkedAtt.Handle] = linkedAtt;
                                    }
                                }
                            }
                            catch
                            {
                                // 忽略无效 Handle 解析异常
                            }
                        }

                        if (linkedAtt != null && !matchedAttachmentIds.Contains(linkedAtt.AttachmentId))
                        {
                            matchedPipeIds.Add(pipe.PipeId);
                            matchedAttachmentIds.Add(linkedAtt.AttachmentId);
                            summary.XDataMatchedCount++;

                            double distCurve = ComputePointToLineStringDistance(linkedAtt.Position, pipe.Geometry, out Point3d closestPt, out _);
                            double distCenter = Distance2D(linkedAtt.Position, pipe.ExtentsCenter);

                            var matchItem = new PipeAttachmentMatchItem
                            {
                                Status = SpatialMatchStatus.UniqueMatch,
                                Channel = SpatialMatchChannel.XDataLink,
                                Pipe = pipe,
                                MatchedAttachment = linkedAtt,
                                DistanceToCurve = distCurve,
                                DistanceToExtentsCenter = distCenter,
                                ClosestPointOnPipe = closestPt,
                                PossibleMatches = new List<MatchItem>
                                {
                                    new MatchItem(linkedAtt.AttachmentId, linkedAtt.Position, distCurve, closestPt, linkedAtt.RotationDegrees ?? 0.0, 0.0, true)
                                },
                                Description = $"通过 XData ({XDataRegAppName}) 恢复配对 [{linkedAtt.Handle}]"
                            };

                            RegisterUniqueMatch(summary, matchItem);
                        }
                    }

                    // 3.2 反向：通过属性块上的 XDataLinkedPipeHandle 查找管线
                    foreach (SpatialAttachmentItem att in attachmentsById.Values)
                    {
                        if (matchedAttachmentIds.Contains(att.AttachmentId))
                            continue;

                        if (!string.IsNullOrWhiteSpace(att.XDataLinkedPipeHandle) &&
                            pipesByHandle.TryGetValue(att.XDataLinkedPipeHandle, out SpatialPipeTarget linkedPipe) &&
                            !matchedPipeIds.Contains(linkedPipe.PipeId))
                        {
                            matchedPipeIds.Add(linkedPipe.PipeId);
                            matchedAttachmentIds.Add(att.AttachmentId);
                            summary.XDataMatchedCount++;

                            double distCurve = ComputePointToLineStringDistance(att.Position, linkedPipe.Geometry, out Point3d closestPt, out _);
                            double distCenter = Distance2D(att.Position, linkedPipe.ExtentsCenter);

                            var matchItem = new PipeAttachmentMatchItem
                            {
                                Status = SpatialMatchStatus.UniqueMatch,
                                Channel = SpatialMatchChannel.XDataLink,
                                Pipe = linkedPipe,
                                MatchedAttachment = att,
                                DistanceToCurve = distCurve,
                                DistanceToExtentsCenter = distCenter,
                                ClosestPointOnPipe = closestPt,
                                PossibleMatches = new List<MatchItem>
                                {
                                    new MatchItem(att.AttachmentId, att.Position, distCurve, closestPt, att.RotationDegrees ?? 0.0, 0.0, true)
                                },
                                Description = $"通过属性块 XData ({XDataRegAppName}) 恢复配对 [{att.Handle}]"
                            };

                            RegisterUniqueMatch(summary, matchItem);
                        }
                    }
                }

                tr.Commit();
            }

            // 4. 第三级：对剩余未绑定管线与属性块执行空间双判据邻近配对
            var remainingPipes = pipesById.Values.Where(p => !matchedPipeIds.Contains(p.PipeId)).ToList();
            var remainingAttachments = attachmentsById.Values.Where(a => !matchedAttachmentIds.Contains(a.AttachmentId)).ToList();

            if (remainingPipes.Count > 0 || remainingAttachments.Count > 0)
            {
                MatchRemainingBySpatialProximity(remainingPipes, remainingAttachments, options, summary, progress);
            }

            return summary;
        }

        /// <summary>
        /// 对指定 CAD 数据库中的管线图层与文字标注图层（DBText / MText）执行三级（Group -&gt; XData -&gt; 空间邻近+平行角）配对（Stage 5 Commit 5.1 &amp; 5.2）。
        /// 自动调用 PipeLabelParser 解析管径（WIDTH）、坡度（PD）、标注管长（LEN）与不规范标注标识（IRREGULAR_LABEL）。
        /// </summary>
        public PipeSpatialMatchSummary MatchDatabaseTextLabels(
            Database db,
            IEnumerable<string> pipeLayers,
            IEnumerable<string> textLayers,
            PipeSpatialMatchOptions options = null,
            ProgressContext progress = null)
        {
            if (db == null)
                throw new ArgumentNullException(nameof(db));

            var baseOpts = options ?? PipeSpatialMatchOptions.FromConfig();
            double textMaxDist = Math.Max(baseOpts.MaxCurveDistance, 85.0);
            var textMatchOpts = new PipeSpatialMatchOptions
            {
                MaxCurveDistance = textMaxDist,
                // 文字标注沿管线曲线平行摆放，不启用 GIS 块专用的外包框中心重合判据
                ExtentsCenterTolerance = 0.0,
                AmbiguityDistanceMargin = baseOpts.AmbiguityDistanceMargin,
                LengthMatchTolerance = baseOpts.LengthMatchTolerance,
                CheckParallelAngle = true,
                ParallelAngleToleranceDegrees = baseOpts.ParallelAngleToleranceDegrees > 0 ? baseOpts.ParallelAngleToleranceDegrees : 35.0,
                UseExistingCadGroups = baseOpts.UseExistingCadGroups,
                UseXDataLinks = baseOpts.UseXDataLinks,
                LengthAttributeAliases = baseOpts.LengthAttributeAliases,
                WidthAttributeAliases = baseOpts.WidthAttributeAliases,
                HeightAttributeAliases = baseOpts.HeightAttributeAliases
            };

            var pipeLayerSet = new HashSet<string>(
                (pipeLayers ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()),
                StringComparer.OrdinalIgnoreCase);

            var textLayerSet = new HashSet<string>(
                (textLayers ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()),
                StringComparer.OrdinalIgnoreCase);

            var pipesById = new Dictionary<ObjectId, SpatialPipeTarget>();
            var pipesByHandle = new Dictionary<string, SpatialPipeTarget>(StringComparer.OrdinalIgnoreCase);
            var attachmentsById = new Dictionary<ObjectId, SpatialAttachmentItem>();
            var attachmentsByHandle = new Dictionary<string, SpatialAttachmentItem>(StringComparer.OrdinalIgnoreCase);

            var summary = new PipeSpatialMatchSummary();
            var matchedPipeIds = new HashSet<ObjectId>();
            var matchedAttachmentIds = new HashSet<ObjectId>();

            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                // 1. 扫描 ModelSpace 提取目标图层上的全部有效管线与文字标注
                foreach (ObjectId objId in ms)
                {
                    if (objId.IsNull || objId.IsErased) continue;

                    Entity ent = tr.GetObject(objId, OpenMode.ForRead, false) as Entity;
                    if (ent == null || string.IsNullOrWhiteSpace(ent.Layer))
                        continue;

                    string layer = ent.Layer.Trim();

                    if (pipeLayerSet.Contains(layer))
                    {
                        if (ent is Polyline pl)
                        {
                            SpatialPipeTarget target = CreatePipeTarget(pl, objId, _geometryFactory);
                            if (target != null && target.Length > 1e-4)
                            {
                                pipesById[objId] = target;
                                pipesByHandle[target.Handle] = target;
                            }
                        }
                        else if (ent is Line line)
                        {
                            SpatialPipeTarget target = CreatePipeTargetFromLine(line, objId, _geometryFactory);
                            if (target != null && target.Length > 1e-4)
                            {
                                pipesById[objId] = target;
                                pipesByHandle[target.Handle] = target;
                            }
                        }
                    }

                    if (textLayerSet.Contains(layer) && (ent is DBText || ent is MText))
                    {
                        SpatialAttachmentItem att = PipeLabelParser.CreateAttachmentFromTextEntity(ent, objId, out _);
                        if (att != null)
                        {
                            attachmentsById[objId] = att;
                            attachmentsByHandle[att.Handle] = att;
                        }
                    }
                }

                summary.TotalPipeCount = pipesById.Count;
                summary.TotalAttachmentCount = attachmentsById.Count;

                // 2. 第一级：校验并提取 CAD 原生 GroupDictionary 中的管线-文字标注 1:1 绑定关系
                if (textMatchOpts.UseExistingCadGroups)
                {
                    DBDictionary groupDict = tr.GetObject(db.GroupDictionaryId, OpenMode.ForRead) as DBDictionary;
                    if (groupDict != null && groupDict.Count > 0)
                    {
                        foreach (DBDictionaryEntry entry in groupDict)
                        {
                            Group grp = tr.GetObject(entry.Value, OpenMode.ForRead, false) as Group;
                            if (grp == null || grp.IsErased)
                                continue;

                            ObjectId[] entIds = grp.GetAllEntityIds();
                            if (entIds == null || entIds.Length == 0)
                                continue;

                            var groupPipes = new List<SpatialPipeTarget>();
                            var groupAttachments = new List<SpatialAttachmentItem>();

                            foreach (ObjectId eid in entIds)
                            {
                                if (pipesById.TryGetValue(eid, out SpatialPipeTarget p))
                                {
                                    groupPipes.Add(p);
                                }
                                else if (attachmentsById.TryGetValue(eid, out SpatialAttachmentItem a))
                                {
                                    groupAttachments.Add(a);
                                }
                                else if (!eid.IsNull && !eid.IsErased && groupPipes.Count > 0)
                                {
                                    Entity fallbackEnt = tr.GetObject(eid, OpenMode.ForRead, false) as Entity;
                                    if (fallbackEnt is DBText || fallbackEnt is MText)
                                    {
                                        SpatialAttachmentItem fallbackAtt = PipeLabelParser.CreateAttachmentFromTextEntity(fallbackEnt, eid, out _);
                                        if (fallbackAtt != null)
                                        {
                                            attachmentsById[eid] = fallbackAtt;
                                            attachmentsByHandle[fallbackAtt.Handle] = fallbackAtt;
                                            groupAttachments.Add(fallbackAtt);
                                        }
                                    }
                                }
                            }

                            if (groupPipes.Count == 0 && groupAttachments.Count == 0)
                                continue;

                            summary.TotalExistingGroupCount++;

                            if (groupPipes.Count == 1 && groupAttachments.Count == 1)
                            {
                                SpatialPipeTarget p = groupPipes[0];
                                SpatialAttachmentItem a = groupAttachments[0];

                                if (!matchedPipeIds.Contains(p.PipeId) && !matchedAttachmentIds.Contains(a.AttachmentId))
                                {
                                    matchedPipeIds.Add(p.PipeId);
                                    matchedAttachmentIds.Add(a.AttachmentId);
                                    summary.ValidGroupPairCount++;

                                    double distCurve = ComputePointToLineStringDistance(a.Position, p.Geometry, out Point3d closestPt, out double segAng);
                                    double distCenter = Distance2D(a.Position, p.ExtentsCenter);
                                    double? angDiff = a.RotationDegrees.HasValue
                                        ? (double?)ComputeParallelAngleDifference(a.RotationDegrees.Value, segAng)
                                        : null;

                                    var matchItem = new PipeAttachmentMatchItem
                                    {
                                        Status = SpatialMatchStatus.UniqueMatch,
                                        Channel = SpatialMatchChannel.CadGroup,
                                        Pipe = p,
                                        MatchedAttachment = a,
                                        GroupId = entry.Value,
                                        GroupName = entry.Key,
                                        DistanceToCurve = distCurve,
                                        DistanceToExtentsCenter = distCenter,
                                        AngleDifferenceDegrees = angDiff,
                                        ClosestPointOnPipe = closestPt,
                                        PossibleMatches = new List<MatchItem>
                                        {
                                            new MatchItem(a.AttachmentId, a.Position, distCurve, closestPt, a.RotationDegrees ?? 0.0, angDiff ?? 0.0, true)
                                        },
                                        Description = $"已通过 CAD Group [{entry.Key}] 1:1 绑定文字标注 [{a.Handle}] '{a.BlockNameOrText}'"
                                    };

                                    RegisterUniqueMatch(summary, matchItem);
                                }
                                else
                                {
                                    summary.InvalidGroupCount++;
                                    summary.InvalidGroups.Add(new PipeAttachmentMatchItem
                                    {
                                        Status = SpatialMatchStatus.InvalidGroupStructure,
                                        Channel = SpatialMatchChannel.CadGroup,
                                        Pipe = p,
                                        MatchedAttachment = a,
                                        GroupId = entry.Value,
                                        GroupName = entry.Key,
                                        ClosestPointOnPipe = p != null ? p.MidPoint : a.Position,
                                        Description = $"Group [{entry.Key}] 中的管线 [{p.Handle}] 或文字标注 [{a.Handle}] 已被其他 Group 重复引用"
                                    });
                                }
                            }
                            else
                            {
                                SpatialPipeTarget firstPipe = groupPipes.FirstOrDefault();
                                SpatialAttachmentItem firstAtt = groupAttachments.FirstOrDefault();
                                summary.InvalidGroupCount++;
                                summary.InvalidGroups.Add(new PipeAttachmentMatchItem
                                {
                                    Status = SpatialMatchStatus.InvalidGroupStructure,
                                    Channel = SpatialMatchChannel.CadGroup,
                                    Pipe = firstPipe,
                                    MatchedAttachment = firstAtt,
                                    GroupId = entry.Value,
                                    GroupName = entry.Key,
                                    ClosestPointOnPipe = firstPipe != null ? firstPipe.MidPoint : (firstAtt != null ? firstAtt.Position : Point3d.Origin),
                                    PossibleMatches = groupAttachments
                                        .Select(att => new MatchItem(
                                            att.AttachmentId,
                                            att.Position,
                                            firstPipe != null ? Distance2D(att.Position, firstPipe.MidPoint) : 0.0,
                                            firstPipe != null ? firstPipe.MidPoint : att.Position,
                                            att.RotationDegrees ?? 0.0,
                                            0.0,
                                            true))
                                        .ToList(),
                                    Description = $"Group [{entry.Key}] 成员结构异常：包含 {groupPipes.Count} 条管线、{groupAttachments.Count} 个文字标注（应为 1:1）"
                                });
                            }
                        }
                    }
                }

                // 3. 第二级：通过 XData (PIPE_CHECK_LINK) 双向恢复管线与文字标注配对
                if (textMatchOpts.UseXDataLinks)
                {
                    foreach (SpatialPipeTarget pipe in pipesById.Values)
                    {
                        if (matchedPipeIds.Contains(pipe.PipeId))
                            continue;

                        if (string.IsNullOrWhiteSpace(pipe.XDataLinkedAttachmentHandle))
                            continue;

                        if (!attachmentsByHandle.TryGetValue(pipe.XDataLinkedAttachmentHandle, out SpatialAttachmentItem linkedAtt))
                        {
                            try
                            {
                                long handleVal = Convert.ToInt64(pipe.XDataLinkedAttachmentHandle.Trim(), 16);
                                if (db.TryGetObjectId(new Handle(handleVal), out ObjectId linkedObjId) &&
                                    !linkedObjId.IsNull && !linkedObjId.IsErased)
                                {
                                    Entity linkedEnt = tr.GetObject(linkedObjId, OpenMode.ForRead, false) as Entity;
                                    if (linkedEnt is DBText || linkedEnt is MText)
                                    {
                                        linkedAtt = PipeLabelParser.CreateAttachmentFromTextEntity(linkedEnt, linkedObjId, out _);
                                        if (linkedAtt != null)
                                        {
                                            attachmentsById[linkedObjId] = linkedAtt;
                                            attachmentsByHandle[linkedAtt.Handle] = linkedAtt;
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                // 忽略无效 Handle 解析异常
                            }
                        }

                        if (linkedAtt != null && !matchedAttachmentIds.Contains(linkedAtt.AttachmentId))
                        {
                            matchedPipeIds.Add(pipe.PipeId);
                            matchedAttachmentIds.Add(linkedAtt.AttachmentId);
                            summary.XDataMatchedCount++;

                            double distCurve = ComputePointToLineStringDistance(linkedAtt.Position, pipe.Geometry, out Point3d closestPt, out double segAng);
                            double distCenter = Distance2D(linkedAtt.Position, pipe.ExtentsCenter);
                            double? angDiff = linkedAtt.RotationDegrees.HasValue
                                ? (double?)ComputeParallelAngleDifference(linkedAtt.RotationDegrees.Value, segAng)
                                : null;

                            var matchItem = new PipeAttachmentMatchItem
                            {
                                Status = SpatialMatchStatus.UniqueMatch,
                                Channel = SpatialMatchChannel.XDataLink,
                                Pipe = pipe,
                                MatchedAttachment = linkedAtt,
                                DistanceToCurve = distCurve,
                                DistanceToExtentsCenter = distCenter,
                                AngleDifferenceDegrees = angDiff,
                                ClosestPointOnPipe = closestPt,
                                PossibleMatches = new List<MatchItem>
                                {
                                    new MatchItem(linkedAtt.AttachmentId, linkedAtt.Position, distCurve, closestPt, linkedAtt.RotationDegrees ?? 0.0, angDiff ?? 0.0, true)
                                },
                                Description = $"通过 XData ({XDataRegAppName}) 恢复文字配对 [{linkedAtt.Handle}] '{linkedAtt.BlockNameOrText}'"
                            };

                            RegisterUniqueMatch(summary, matchItem);
                        }
                    }

                    foreach (SpatialAttachmentItem att in attachmentsById.Values)
                    {
                        if (matchedAttachmentIds.Contains(att.AttachmentId))
                            continue;

                        if (!string.IsNullOrWhiteSpace(att.XDataLinkedPipeHandle) &&
                            pipesByHandle.TryGetValue(att.XDataLinkedPipeHandle, out SpatialPipeTarget linkedPipe) &&
                            !matchedPipeIds.Contains(linkedPipe.PipeId))
                        {
                            matchedPipeIds.Add(linkedPipe.PipeId);
                            matchedAttachmentIds.Add(att.AttachmentId);
                            summary.XDataMatchedCount++;

                            double distCurve = ComputePointToLineStringDistance(att.Position, linkedPipe.Geometry, out Point3d closestPt, out double segAng);
                            double distCenter = Distance2D(att.Position, linkedPipe.ExtentsCenter);
                            double? angDiff = att.RotationDegrees.HasValue
                                ? (double?)ComputeParallelAngleDifference(att.RotationDegrees.Value, segAng)
                                : null;

                            var matchItem = new PipeAttachmentMatchItem
                            {
                                Status = SpatialMatchStatus.UniqueMatch,
                                Channel = SpatialMatchChannel.XDataLink,
                                Pipe = linkedPipe,
                                MatchedAttachment = att,
                                DistanceToCurve = distCurve,
                                DistanceToExtentsCenter = distCenter,
                                AngleDifferenceDegrees = angDiff,
                                ClosestPointOnPipe = closestPt,
                                PossibleMatches = new List<MatchItem>
                                {
                                    new MatchItem(att.AttachmentId, att.Position, distCurve, closestPt, att.RotationDegrees ?? 0.0, angDiff ?? 0.0, true)
                                },
                                Description = $"通过文字 XData ({XDataRegAppName}) 恢复配对 [{att.Handle}] '{att.BlockNameOrText}'"
                            };

                            RegisterUniqueMatch(summary, matchItem);
                        }
                    }
                }

                tr.Commit();
            }

            // 4. 第三级：对剩余未绑定管线与文字标注执行空间邻近 + 平行角配对
            var remainingPipes = pipesById.Values.Where(p => !matchedPipeIds.Contains(p.PipeId)).ToList();
            var remainingAttachments = attachmentsById.Values.Where(a => !matchedAttachmentIds.Contains(a.AttachmentId)).ToList();

            if (remainingPipes.Count > 0 || remainingAttachments.Count > 0)
            {
                MatchRemainingBySpatialProximity(remainingPipes, remainingAttachments, textMatchOpts, summary, progress);
            }

            return summary;
        }

        /// <summary>
        /// 按规划数据配置（PlanningDataSettings）执行三类规划管线与对应文字标注图层的空间+平行角匹配，
        /// 并将解析出的管径（WIDTH）、坡度（PD）、标注管长（LEN）自动组装写入 PipeDatasetContext（Stage 5 Commit 5.1）。
        /// </summary>
        public PipeSpatialMatchSummary MatchAndEnrichPlanTextLabels(
            Database db,
            PipeDatasetContext datasetContext,
            PlanningDataSettings planningSettings = null,
            PipeSpatialMatchOptions options = null,
            ProgressContext progress = null)
        {
            if (db == null)
                throw new ArgumentNullException(nameof(db));

            var cfg = planningSettings ?? CadConfig.PlanningData ?? new PlanningDataSettings();
            PipeSpatialMatchSummary summary;

            // 若配置了标准三类规划管线分类规则，则按类别（新建 / 现状 / 改扩建）隔离配对再汇总，
            // 避免交叉路口处不同类别的管线（如现状管与 2025 新建管）跨图层误抢文字标注。
            if (cfg.Categories != null && cfg.Categories.Count > 0)
            {
                summary = new PipeSpatialMatchSummary();
                foreach (PlanPipeCategoryRule cat in cfg.Categories)
                {
                    if (cat == null) continue;
                    var catSummary = MatchDatabaseTextLabels(db, cat.PipeLayers, cat.TextLayers, options, progress);
                    MergeMatchSummaries(summary, catSummary);
                }
            }
            else
            {
                var allPipeLayers = cfg.GetAllPipeLayers();
                var allTextLayers = cfg.GetAllTextLayers();
                summary = MatchDatabaseTextLabels(db, allPipeLayers, allTextLayers, options, progress);
            }

            if (datasetContext != null)
            {
                // 将唯一匹配与有首选候选的歧义项同步写入 PipeDatasetContext
                var matchedItems = summary.UniqueMatches.Concat(summary.ConflictMatches);
                foreach (PipeAttachmentMatchItem item in matchedItems)
                {
                    if (item?.Pipe == null || item.MatchedAttachment == null)
                        continue;

                    EnrichedPlanPipeInfo enriched = datasetContext.GetOrCreatePlanPipeInfo(item.Pipe.PipeId, item.Pipe.Handle);
                    if (string.IsNullOrWhiteSpace(enriched.LayerName))
                    {
                        enriched.LayerName = item.Pipe.LayerName ?? string.Empty;
                    }

                    if (enriched.Polyline == null)
                    {
                        enriched.Polyline = new CadPolyline
                        {
                            SourceHandles = new List<string> { item.Pipe.Handle },
                            LayerName = item.Pipe.LayerName ?? string.Empty,
                            DatasetName = "plan",
                            Geometry = item.Pipe.Geometry,
                            Attributes = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        };
                    }

                    var attrs = item.MatchedAttachment.Attributes;
                    if (attrs != null)
                    {
                        if (attrs.TryGetValue("WIDTH", out object wObj) && wObj != null &&
                            double.TryParse(wObj.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double wVal))
                        {
                            enriched.TextDiameter = wVal;
                        }

                        if (attrs.TryGetValue("PD", out object pdObj) && pdObj != null &&
                            double.TryParse(pdObj.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double pdVal))
                        {
                            enriched.TextSlope = pdVal;
                        }

                        if (attrs.TryGetValue("LEN", out object lenObj) && lenObj != null &&
                            double.TryParse(lenObj.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lenVal))
                        {
                            enriched.TextLength = lenVal;
                        }
                    }

                    enriched.RawLabelText = item.MatchedAttachment.BlockNameOrText ?? string.Empty;
                    enriched.MatchedTextHandle = item.MatchedAttachment.Handle ?? string.Empty;
                    enriched.SyncToPolylineAttributes();
                }
            }

            return summary;
        }

        private static void MergeMatchSummaries(PipeSpatialMatchSummary target, PipeSpatialMatchSummary source)
        {
            if (target == null || source == null)
                return;

            target.TotalPipeCount += source.TotalPipeCount;
            target.TotalAttachmentCount += source.TotalAttachmentCount;
            target.TotalExistingGroupCount += source.TotalExistingGroupCount;
            target.ValidGroupPairCount += source.ValidGroupPairCount;
            target.InvalidGroupCount += source.InvalidGroupCount;
            target.XDataMatchedCount += source.XDataMatchedCount;
            target.ExtentsCenterMatchedCount += source.ExtentsCenterMatchedCount;
            target.CurveProximityMatchedCount += source.CurveProximityMatchedCount;

            target.UniqueMatches.AddRange(source.UniqueMatches);
            target.ConflictMatches.AddRange(source.ConflictMatches);
            target.UnmatchedPipes.AddRange(source.UnmatchedPipes);
            target.OrphanAttachments.AddRange(source.OrphanAttachments);
            target.InvalidGroups.AddRange(source.InvalidGroups);

            foreach (var kv in source.MatchedAttributesByPipeHandle)
            {
                target.MatchedAttributesByPipeHandle[kv.Key] = kv.Value;
            }

            foreach (var kv in source.MatchByPipeId)
            {
                target.MatchByPipeId[kv.Key] = kv.Value;
            }
        }

        /// <summary>
        /// 对内存中的管线集合与附属要素集合（属性块或文字标注）直接执行空间双判据匹配。
        /// </summary>
        public PipeSpatialMatchSummary MatchInMemory(
            IReadOnlyList<SpatialPipeTarget> pipes,
            IReadOnlyList<SpatialAttachmentItem> attachments,
            PipeSpatialMatchOptions options = null)
        {
            options = options ?? PipeSpatialMatchOptions.FromConfig();
            var summary = new PipeSpatialMatchSummary
            {
                TotalPipeCount = pipes?.Count ?? 0,
                TotalAttachmentCount = attachments?.Count ?? 0
            };

            MatchRemainingBySpatialProximity(
                pipes ?? Array.Empty<SpatialPipeTarget>(),
                attachments ?? Array.Empty<SpatialAttachmentItem>(),
                options,
                summary);

            return summary;
        }

        private void MatchRemainingBySpatialProximity(
            IReadOnlyList<SpatialPipeTarget> pipes,
            IReadOnlyList<SpatialAttachmentItem> attachments,
            PipeSpatialMatchOptions options,
            PipeSpatialMatchSummary summary,
            ProgressContext progress = null)
        {
            if (pipes.Count == 0 && attachments.Count == 0)
                return;

            progress?.SetTotal(Math.Max(1, attachments.Count + pipes.Count));

            // 构建管线空间索引：同时索引“管线外包框扩展区”与“外包框中心点扩展区”
            var pipeTree = new STRtree<SpatialPipeTarget>();
            foreach (SpatialPipeTarget pipe in pipes)
            {
                if (pipe?.Envelope == null) continue;
                var expandedEnv = new Envelope(pipe.Envelope);
                expandedEnv.ExpandBy(Math.Max(options.MaxCurveDistance, options.ExtentsCenterTolerance) + 1.0);
                pipeTree.Insert(expandedEnv, pipe);
            }
            pipeTree.Build();

            // 计算所有可行的 (Pipe, Attachment) 候选配对及其综合代价分
            var allCandidates = new List<CandidatePair>();
            var candidatesByPipe = new Dictionary<string, List<CandidatePair>>(StringComparer.OrdinalIgnoreCase);
            var candidatesByAtt = new Dictionary<string, List<CandidatePair>>(StringComparer.OrdinalIgnoreCase);

            foreach (SpatialAttachmentItem att in attachments)
            {
                progress?.Step();
                if (att == null) continue;

                var ptEnv = new Envelope(att.Position.X, att.Position.X, att.Position.Y, att.Position.Y);
                IList<SpatialPipeTarget> nearbyPipes = pipeTree.Query(ptEnv);
                if (nearbyPipes == null || nearbyPipes.Count == 0)
                    continue;

                foreach (SpatialPipeTarget pipe in nearbyPipes)
                {
                    if (pipe == null) continue;

                    double distCenter = Distance2D(att.Position, pipe.ExtentsCenter);
                    double distCurve = ComputePointToLineStringDistance(
                        att.Position,
                        pipe.Geometry,
                        out Point3d closestPt,
                        out double segAngleDeg);

                    bool isCenterHit = distCenter <= options.ExtentsCenterTolerance;
                    bool isCurveHit = distCurve <= options.MaxCurveDistance;

                    if (!isCenterHit && !isCurveHit)
                        continue;

                    double? angleDiff = null;
                    if (options.CheckParallelAngle && att.RotationDegrees.HasValue)
                    {
                        double diff = ComputeParallelAngleDifference(att.RotationDegrees.Value, segAngleDeg);
                        angleDiff = diff;
                        if (!isCenterHit && diff > options.ParallelAngleToleranceDegrees)
                            continue;
                    }

                    double? lenDiff = null;
                    bool isLengthMatched = false;
                    if (att.DeclaredLength.HasValue && att.DeclaredLength.Value > 0)
                    {
                        lenDiff = Math.Abs(pipe.Length - att.DeclaredLength.Value);
                        isLengthMatched = lenDiff.Value <= options.LengthMatchTolerance;
                    }

                    // 计算综合排序代价 Score（越小越优）：
                    // - 若命中外包框中心（GIS 导出块特征），给予最高优先级基准（0 ~ 10 分区间），并叠加上版 LEN 管长误差惩罚；
                    // - 若仅命中曲线邻近，基准分从 100 起算，叠加曲线距离、管长误差与平行角误差。
                    double score;
                    SpatialMatchChannel channel;
                    if (isCenterHit)
                    {
                        channel = SpatialMatchChannel.ExtentsCenterMatch;
                        double lenPenalty = lenDiff.HasValue ? Math.Min(lenDiff.Value, 50.0) * 0.5 : 0.0;
                        score = distCenter + lenPenalty + Math.Min(distCurve, 200.0) * 0.001;
                    }
                    else
                    {
                        channel = SpatialMatchChannel.CurveProximityMatch;
                        double lenPenalty = lenDiff.HasValue ? Math.Min(lenDiff.Value, 50.0) * 0.25 : 0.0;
                        double angPenalty = angleDiff.HasValue ? angleDiff.Value * 0.02 : 0.0;
                        double regimePenalty = 0.0;
                        if (options.CheckParallelAngle &&
                            !string.IsNullOrWhiteSpace(pipe.LayerName) &&
                            !string.IsNullOrWhiteSpace(att.BlockNameOrText))
                        {
                            bool isPressurePipe = pipe.LayerName.IndexOf("压力", StringComparison.OrdinalIgnoreCase) >= 0;
                            bool isDnLabel = att.BlockNameOrText.IndexOf("DN", StringComparison.OrdinalIgnoreCase) >= 0;
                            if (isPressurePipe != isDnLabel)
                            {
                                regimePenalty = 35.0;
                            }
                        }
                        score = 100.0 + distCurve + lenPenalty + angPenalty + regimePenalty;
                    }

                    var pair = new CandidatePair
                    {
                        Pipe = pipe,
                        Attachment = att,
                        Channel = channel,
                        DistanceToCenter = distCenter,
                        DistanceToCurve = distCurve,
                        LengthDiff = lenDiff,
                        IsLengthMatched = isLengthMatched,
                        AngleDiff = angleDiff,
                        ClosestPointOnPipe = closestPt,
                        Score = score
                    };

                    allCandidates.Add(pair);

                    if (!candidatesByPipe.TryGetValue(pipe.Handle, out var pList))
                    {
                        pList = new List<CandidatePair>();
                        candidatesByPipe[pipe.Handle] = pList;
                    }
                    pList.Add(pair);

                    if (!candidatesByAtt.TryGetValue(att.Handle, out var aList))
                    {
                        aList = new List<CandidatePair>();
                        candidatesByAtt[att.Handle] = aList;
                    }
                    aList.Add(pair);
                }
            }

            // 按综合代价从优到劣执行全局 1:1 贪心最优指派
            allCandidates.Sort((x, y) => x.Score.CompareTo(y.Score));

            var assignedPipes = new Dictionary<string, CandidatePair>(StringComparer.OrdinalIgnoreCase);
            var assignedAttachments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (CandidatePair cand in allCandidates)
            {
                if (assignedPipes.ContainsKey(cand.Pipe.Handle))
                    continue;
                if (assignedAttachments.Contains(cand.Attachment.Handle))
                    continue;

                assignedPipes[cand.Pipe.Handle] = cand;
                assignedAttachments.Add(cand.Attachment.Handle);
            }

            // 生成每条管线的配对结果并检测是否存在空间歧义、一管多标或不规范文字标注
            foreach (SpatialPipeTarget pipe in pipes)
            {
                progress?.Step();
                if (assignedPipes.TryGetValue(pipe.Handle, out CandidatePair best))
                {
                    var pipeCands = candidatesByPipe.TryGetValue(pipe.Handle, out var list)
                        ? list.OrderBy(c => c.Score).ToList()
                        : new List<CandidatePair> { best };

                    // 1. 判断是否存在需人工确认的近距离空间歧义：
                    // 若存在次优候选，且次优候选与最优候选属于同一命中层级、距离差小于 AmbiguityDistanceMargin、
                    // 且最优候选无法通过精确管长 LEN 唯一锁定时，标记为空间歧义。
                    bool hasSpatialAmbiguity = false;
                    if (pipeCands.Count > 1)
                    {
                        CandidatePair second = pipeCands[1];
                        double primaryDist = best.Channel == SpatialMatchChannel.ExtentsCenterMatch
                            ? best.DistanceToCenter
                            : best.DistanceToCurve;
                        double secondaryDist = second.Channel == SpatialMatchChannel.ExtentsCenterMatch
                            ? second.DistanceToCenter
                            : second.DistanceToCurve;

                        if (best.Channel == second.Channel &&
                            Math.Abs(secondaryDist - primaryDist) <= options.AmbiguityDistanceMargin &&
                            !(best.IsLengthMatched && !second.IsLengthMatched))
                        {
                            if (primaryDist > 0.05 || Math.Abs(best.Score - second.Score) <= options.AmbiguityDistanceMargin)
                            {
                                hasSpatialAmbiguity = true;
                            }
                        }
                    }

                    // 2. 检测“一管多标”（单条长干管旁存在多个未被其他管线认领、且以当前管线为最邻近目标的文字标注）
                    var extraLabelsOnSamePipe = new List<CandidatePair>();
                    foreach (CandidatePair cand in pipeCands)
                    {
                        if (string.Equals(cand.Attachment.Handle, best.Attachment.Handle, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (assignedAttachments.Contains(cand.Attachment.Handle))
                            continue;

                        if (candidatesByAtt.TryGetValue(cand.Attachment.Handle, out var attCandList) &&
                            attCandList.Count > 0)
                        {
                            CandidatePair attBestPipe = attCandList.OrderBy(x => x.Score).First();
                            if (string.Equals(attBestPipe.Pipe.Handle, pipe.Handle, StringComparison.OrdinalIgnoreCase))
                            {
                                extraLabelsOnSamePipe.Add(cand);
                            }
                        }
                    }

                    bool hasOnePipeMultiLabel = extraLabelsOnSamePipe.Count > 0;

                    // 3. 检测“不规范文字标注格式”（如并排多管 "2DN1000"、渐变范围管径 "d600-700"）
                    string irregularReason = null;
                    if (best.Attachment.Attributes != null &&
                        best.Attachment.Attributes.TryGetValue("IRREGULAR_LABEL", out object irregObj) &&
                        irregObj != null &&
                        !string.IsNullOrWhiteSpace(irregObj.ToString()))
                    {
                        irregularReason = irregObj.ToString();
                    }
                    else
                    {
                        foreach (CandidatePair extra in extraLabelsOnSamePipe)
                        {
                            if (extra.Attachment.Attributes != null &&
                                extra.Attachment.Attributes.TryGetValue("IRREGULAR_LABEL", out object exIrreg) &&
                                exIrreg != null &&
                                !string.IsNullOrWhiteSpace(exIrreg.ToString()))
                            {
                                irregularReason = exIrreg.ToString();
                                break;
                            }
                        }
                    }

                    bool hasIrregularLabel = !string.IsNullOrWhiteSpace(irregularReason);
                    bool needsManualPendingReview = hasSpatialAmbiguity || hasOnePipeMultiLabel || hasIrregularLabel;

                    // 构建瞬态辅助连线集合：
                    // - 若属于常规 1:1 唯一匹配（ToRecheck 待复核），仅绘制当前管线与唯一配对要素的 1 条连线，避免邻近已配对标注干扰视线；
                    // - 若属于一管多标或空间歧义（Pending 待核查），绘制当前管线到全部关联/竞争标注的多条连线。
                    List<CandidatePair> displayCands;
                    if (hasOnePipeMultiLabel)
                    {
                        displayCands = new List<CandidatePair> { best };
                        displayCands.AddRange(extraLabelsOnSamePipe);
                    }
                    else if (hasSpatialAmbiguity)
                    {
                        displayCands = pipeCands.Take(5).ToList();
                    }
                    else
                    {
                        displayCands = new List<CandidatePair> { best };
                    }

                    var possibleMatches = displayCands
                        .Take(8)
                        .Select(c => new MatchItem(
                            c.Attachment.AttachmentId,
                            c.Attachment.Position,
                            c.Channel == SpatialMatchChannel.ExtentsCenterMatch ? c.DistanceToCenter : c.DistanceToCurve,
                            c.ClosestPointOnPipe,
                            c.Attachment.RotationDegrees ?? 0.0,
                            c.AngleDiff ?? 0.0,
                            true))
                        .ToList();

                    if (best.Channel == SpatialMatchChannel.ExtentsCenterMatch)
                        summary.ExtentsCenterMatchedCount++;
                    else
                        summary.CurveProximityMatchedCount++;

                    string description;
                    if (hasIrregularLabel && hasOnePipeMultiLabel)
                    {
                        string allLabels = string.Join("、", displayCands.Select(c => $"[{c.Attachment.Handle}]'{c.Attachment.BlockNameOrText}'"));
                        description = $"不规范标注且一管多标待核查：管线 [{pipe.Handle}] 沿线存在 {displayCands.Count} 个标注（{allLabels}），且含{irregularReason}，已暂取 [{best.Attachment.Handle}]（距曲线={best.DistanceToCurve:F2}m）";
                    }
                    else if (hasIrregularLabel)
                    {
                        description = $"不规范文字标注待核查：管线 [{pipe.Handle}] 配对标注 [{best.Attachment.Handle}] '{best.Attachment.BlockNameOrText}' 含{irregularReason}（距曲线={best.DistanceToCurve:F2}m）";
                    }
                    else if (hasOnePipeMultiLabel)
                    {
                        var distinctSpecs = displayCands
                            .Select(c => c.Attachment.BlockNameOrText ?? string.Empty)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        string specWarn = distinctSpecs.Count > 1 ? "（注意：沿线标注规格不一致！）" : "（沿线重复标注）";
                        string allLabels = string.Join("、", displayCands.Select(c => $"[{c.Attachment.Handle}]'{c.Attachment.BlockNameOrText}'"));
                        description = $"一管多标待核查{specWarn}：管线 [{pipe.Handle}] (L={pipe.Length:F1}m) 沿线存在 {displayCands.Count} 个文字标注 {allLabels}，已暂取最近 [{best.Attachment.Handle}]（距曲线={best.DistanceToCurve:F2}m）";
                    }
                    else if (hasSpatialAmbiguity)
                    {
                        description = $"空间邻近歧义待核查：管线 [{pipe.Handle}] 附近存在 {pipeCands.Count} 个距离相近的候选属性要素，已暂取最优 [{best.Attachment.Handle}]（距曲线={best.DistanceToCurve:F2}m, 距外包框中心={best.DistanceToCenter:F2}m）";
                    }
                    else
                    {
                        description = $"空间配对成功：管线 [{pipe.Handle}] <-> [{best.Attachment.Handle}]（渠道={best.Channel}, 距曲线={best.DistanceToCurve:F2}m, 距中心={best.DistanceToCenter:F2}m）";
                    }

                    var matchItem = new PipeAttachmentMatchItem
                    {
                        Status = needsManualPendingReview ? SpatialMatchStatus.OneToManyConflict : SpatialMatchStatus.UniqueMatch,
                        Channel = best.Channel,
                        Pipe = pipe,
                        MatchedAttachment = best.Attachment,
                        DistanceToCurve = best.DistanceToCurve,
                        DistanceToExtentsCenter = best.DistanceToCenter,
                        LengthDifference = best.LengthDiff,
                        AngleDifferenceDegrees = best.AngleDiff,
                        ClosestPointOnPipe = best.ClosestPointOnPipe,
                        PossibleMatches = possibleMatches,
                        Description = description
                    };

                    if (needsManualPendingReview)
                    {
                        summary.ConflictMatches.Add(matchItem);
                        if (!pipe.PipeId.IsNull)
                            summary.MatchByPipeId[pipe.PipeId] = matchItem;
                        if (!string.IsNullOrWhiteSpace(pipe.Handle) && best.Attachment.Attributes != null)
                        {
                            summary.MatchedAttributesByPipeHandle[pipe.Handle] =
                                new Dictionary<string, object>(best.Attachment.Attributes, StringComparer.OrdinalIgnoreCase);
                        }
                    }
                    else
                    {
                        RegisterUniqueMatch(summary, matchItem);
                    }
                }
                else
                {
                    // 未分配到专属 1:1 标注的管线：区分“多段共标（邻近平行标注已被相邻管段占用）”与“搜索半径内无标注”
                    if (candidatesByPipe.TryGetValue(pipe.Handle, out var sharedCands) && sharedCands.Count > 0)
                    {
                        CandidatePair nearestShared = sharedCands.OrderBy(c => c.Score).First();
                        string ownerPipeHandle = assignedPipes.Values
                            .FirstOrDefault(p => string.Equals(p.Attachment.Handle, nearestShared.Attachment.Handle, StringComparison.OrdinalIgnoreCase))
                            ?.Pipe?.Handle ?? "-";

                        var sharedMatchItem = new PipeAttachmentMatchItem
                        {
                            Status = SpatialMatchStatus.NoMatchPipe,
                            Channel = SpatialMatchChannel.None,
                            Pipe = pipe,
                            MatchedAttachment = nearestShared.Attachment,
                            DistanceToCurve = nearestShared.DistanceToCurve,
                            DistanceToExtentsCenter = nearestShared.DistanceToCenter,
                            AngleDifferenceDegrees = nearestShared.AngleDiff,
                            ClosestPointOnPipe = nearestShared.ClosestPointOnPipe,
                            PossibleMatches = new List<MatchItem>
                            {
                                new MatchItem(
                                    nearestShared.Attachment.AttachmentId,
                                    nearestShared.Attachment.Position,
                                    nearestShared.DistanceToCurve,
                                    nearestShared.ClosestPointOnPipe,
                                    nearestShared.Attachment.RotationDegrees ?? 0.0,
                                    nearestShared.AngleDiff ?? 0.0,
                                    true)
                            },
                            Description = $"多段共标待核查：管线 [{pipe.Handle}] (图层: {pipe.LayerName}, L={pipe.Length:F1}m) 邻近标注 [{nearestShared.Attachment.Handle}] '{nearestShared.Attachment.BlockNameOrText}'（距曲线={nearestShared.DistanceToCurve:F2}m）已被相邻管线 [{ownerPipeHandle}] 占用"
                        };
                        summary.UnmatchedPipes.Add(sharedMatchItem);
                        if (!pipe.PipeId.IsNull)
                            summary.MatchByPipeId[pipe.PipeId] = sharedMatchItem;
                    }
                    else
                    {
                        var noMatchItem = new PipeAttachmentMatchItem
                        {
                            Status = SpatialMatchStatus.NoMatchPipe,
                            Channel = SpatialMatchChannel.None,
                            Pipe = pipe,
                            ClosestPointOnPipe = pipe.MidPoint,
                            Description = $"无配对标注待核查：管线 [{pipe.Handle}] (图层: {pipe.LayerName}, 长度: {pipe.Length:F2}m) 在搜索半径内未找到匹配的属性块/文字标注"
                        };
                        summary.UnmatchedPipes.Add(noMatchItem);
                        if (!pipe.PipeId.IsNull)
                            summary.MatchByPipeId[pipe.PipeId] = noMatchItem;
                    }
                }
            }

            // 收集未被任何管线 1:1 认领的多余/孤立附属要素（如长干管沿线的第 2~N 个重复标注或含不规范格式的标注）
            foreach (SpatialAttachmentItem att in attachments)
            {
                if (!assignedAttachments.Contains(att.Handle))
                {
                    string irregTag = string.Empty;
                    if (att.Attributes != null &&
                        att.Attributes.TryGetValue("IRREGULAR_LABEL", out object attIrreg) &&
                        attIrreg != null &&
                        !string.IsNullOrWhiteSpace(attIrreg.ToString()))
                    {
                        irregTag = $"（含{attIrreg}）";
                    }

                    if (candidatesByAtt.TryGetValue(att.Handle, out var attCands) && attCands.Count > 0)
                    {
                        CandidatePair nearestPipeCand = attCands.OrderBy(c => c.Score).First();
                        assignedPipes.TryGetValue(nearestPipeCand.Pipe.Handle, out CandidatePair pipeOwnerBest);
                        string ownerLabelInfo = pipeOwnerBest?.Attachment != null
                            ? $"[{pipeOwnerBest.Attachment.Handle}] '{pipeOwnerBest.Attachment.BlockNameOrText}'"
                            : "其他标注";

                        summary.OrphanAttachments.Add(new PipeAttachmentMatchItem
                        {
                            Status = SpatialMatchStatus.OrphanAttachment,
                            Channel = SpatialMatchChannel.None,
                            Pipe = nearestPipeCand.Pipe,
                            MatchedAttachment = att,
                            DistanceToCurve = nearestPipeCand.DistanceToCurve,
                            DistanceToExtentsCenter = nearestPipeCand.DistanceToCenter,
                            AngleDifferenceDegrees = nearestPipeCand.AngleDiff,
                            ClosestPointOnPipe = nearestPipeCand.ClosestPointOnPipe,
                            PossibleMatches = new List<MatchItem>
                            {
                                new MatchItem(
                                    nearestPipeCand.Pipe.PipeId,
                                    att.Position,
                                    nearestPipeCand.DistanceToCurve,
                                    nearestPipeCand.ClosestPointOnPipe,
                                    att.RotationDegrees ?? 0.0,
                                    nearestPipeCand.AngleDiff ?? 0.0,
                                    true)
                            },
                            Description = $"一管多标多余标注待核查{irregTag}：标注 [{att.Handle}] '{att.BlockNameOrText}' 紧邻管线 [{nearestPipeCand.Pipe.Handle}]（距曲线={nearestPipeCand.DistanceToCurve:F2}m），该管线已配对 {ownerLabelInfo}"
                        });
                    }
                    else
                    {
                        SpatialPipeTarget fallbackPipe = null;
                        double minFallbackDist = double.MaxValue;
                        Point3d fallbackClosestPt = att.Position;
                        double fallbackSegAng = 0.0;

                        foreach (SpatialPipeTarget p in pipes)
                        {
                            if (p?.Geometry == null) continue;
                            double d = ComputePointToLineStringDistance(att.Position, p.Geometry, out Point3d cpt, out double sAng);
                            if (d < minFallbackDist)
                            {
                                minFallbackDist = d;
                                fallbackPipe = p;
                                fallbackClosestPt = cpt;
                                fallbackSegAng = sAng;
                            }
                        }

                        var fallbackMatches = new List<MatchItem>();
                        string distHint = string.Empty;
                        if (fallbackPipe != null && minFallbackDist < double.MaxValue)
                        {
                            double angDiff = att.RotationDegrees.HasValue
                                ? ComputeParallelAngleDifference(att.RotationDegrees.Value, fallbackSegAng)
                                : 0.0;
                            fallbackMatches.Add(new MatchItem(
                                fallbackPipe.PipeId,
                                att.Position,
                                minFallbackDist,
                                fallbackClosestPt,
                                att.RotationDegrees ?? 0.0,
                                angDiff,
                                true));
                            distHint = $"（最近管线 [{fallbackPipe.Handle}] 距曲线={minFallbackDist:F2}m, 平行角差={angDiff:F1}°）";
                        }

                        summary.OrphanAttachments.Add(new PipeAttachmentMatchItem
                        {
                            Status = SpatialMatchStatus.OrphanAttachment,
                            Channel = SpatialMatchChannel.None,
                            Pipe = fallbackPipe,
                            MatchedAttachment = att,
                            DistanceToCurve = minFallbackDist,
                            ClosestPointOnPipe = fallbackClosestPt,
                            PossibleMatches = fallbackMatches,
                            Description = $"一管多标多余/超距标注待核查{irregTag}：属性要素 [{att.Handle}] '{att.BlockNameOrText}' (图层: {att.LayerName}) 在常规半径内未配对管线{distHint}"
                        });
                    }
                }
            }
        }

        private static void RegisterUniqueMatch(PipeSpatialMatchSummary summary, PipeAttachmentMatchItem matchItem)
        {
            summary.UniqueMatches.Add(matchItem);
            if (matchItem.Pipe != null)
            {
                if (!matchItem.Pipe.PipeId.IsNull)
                {
                    summary.MatchByPipeId[matchItem.Pipe.PipeId] = matchItem;
                }

                if (!string.IsNullOrWhiteSpace(matchItem.Pipe.Handle) &&
                    matchItem.MatchedAttachment?.Attributes != null)
                {
                    summary.MatchedAttributesByPipeHandle[matchItem.Pipe.Handle] =
                        new Dictionary<string, object>(matchItem.MatchedAttachment.Attributes, StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        /// <summary>
        /// 从 CAD Polyline 构建空间配对目标对象。
        /// </summary>
        public static SpatialPipeTarget CreatePipeTarget(
            Polyline pl,
            ObjectId objId,
            GeometryFactory gf)
        {
            if (pl == null || pl.NumberOfVertices < 2)
                return null;

            var coords = new List<Coordinate>(pl.NumberOfVertices + 1);
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            for (int i = 0; i < pl.NumberOfVertices; i++)
            {
                Point2d pt = pl.GetPoint2dAt(i);
                coords.Add(new Coordinate(pt.X, pt.Y));
                if (pt.X < minX) minX = pt.X;
                if (pt.Y < minY) minY = pt.Y;
                if (pt.X > maxX) maxX = pt.X;
                if (pt.Y > maxY) maxY = pt.Y;
            }

            if (pl.Closed && coords.Count > 1 && !coords[0].Equals2D(coords[coords.Count - 1]))
            {
                coords.Add(new Coordinate(coords[0].X, coords[0].Y));
            }

            LineString ls = gf.CreateLineString(coords.ToArray());
            if (ls.IsEmpty || ls.Length <= 1e-6)
                return null;

            Point3d midPt;
            try
            {
                midPt = pl.GetPointAtDist(pl.Length * 0.5);
            }
            catch
            {
                midPt = new Point3d((minX + maxX) * 0.5, (minY + maxY) * 0.5, 0);
            }

            Point3d extentsCenter = new Point3d((minX + maxX) * 0.5, (minY + maxY) * 0.5, 0);
            try
            {
                Extents3d geoExt = pl.GeometricExtents;
                extentsCenter = new Point3d(
                    (geoExt.MinPoint.X + geoExt.MaxPoint.X) * 0.5,
                    (geoExt.MinPoint.Y + geoExt.MaxPoint.Y) * 0.5,
                    0);
            }
            catch
            {
                // 回退至顶点外包框中心
            }

            return new SpatialPipeTarget
            {
                PipeId = objId,
                Handle = objId.Handle.ToString(),
                LayerName = pl.Layer ?? string.Empty,
                Geometry = ls,
                ExtentsCenter = extentsCenter,
                MidPoint = midPt,
                Envelope = ls.EnvelopeInternal,
                XDataLinkedAttachmentHandle = ReadLinkedHandleFromXData(pl)
            };
        }

        /// <summary>
        /// 从 CAD Line 构建空间配对目标对象。
        /// </summary>
        public static SpatialPipeTarget CreatePipeTargetFromLine(
            Line line,
            ObjectId objId,
            GeometryFactory gf)
        {
            if (line == null || line.Length <= 1e-6)
                return null;

            var coords = new[]
            {
                new Coordinate(line.StartPoint.X, line.StartPoint.Y),
                new Coordinate(line.EndPoint.X, line.EndPoint.Y)
            };
            LineString ls = gf.CreateLineString(coords);
            Point3d center = new Point3d(
                (line.StartPoint.X + line.EndPoint.X) * 0.5,
                (line.StartPoint.Y + line.EndPoint.Y) * 0.5,
                0);

            return new SpatialPipeTarget
            {
                PipeId = objId,
                Handle = objId.Handle.ToString(),
                LayerName = line.Layer ?? string.Empty,
                Geometry = ls,
                ExtentsCenter = center,
                MidPoint = center,
                Envelope = ls.EnvelopeInternal,
                XDataLinkedAttachmentHandle = ReadLinkedHandleFromXData(line)
            };
        }

        /// <summary>
        /// 从 CAD BlockReference 构建空间配对附属要素对象。
        /// 支持按 Config.json 中配置的字段别名列表（LengthAttributeAliases / WidthAttributeAliases / HeightAttributeAliases）
        /// 动态解析管长 DeclaredLength 并归一化标准键名（LEN / WIDTH / HEITH）。
        /// </summary>
        public static SpatialAttachmentItem CreateAttachmentFromBlock(
            BlockReference br,
            ObjectId objId,
            Transaction tr,
            PipeSpatialMatchOptions options = null)
        {
            if (br == null)
                return null;

            Dictionary<string, object> attrs = GeometryHelper.CollectAttributeValuesFromBlockReference(br, tr)
                ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            double? declaredLen = ResolveDeclaredLengthFromAttributes(attrs, options?.LengthAttributeAliases);
            NormalizeCanonicalAttributeAliases(attrs, options);

            return new SpatialAttachmentItem
            {
                AttachmentId = objId,
                Handle = objId.Handle.ToString(),
                LayerName = br.Layer ?? string.Empty,
                BlockNameOrText = br.Name ?? string.Empty,
                Position = br.Position,
                RotationDegrees = (br.Rotation * 180.0 / Math.PI + 360.0) % 360.0,
                DeclaredLength = declaredLen,
                Attributes = attrs,
                XDataLinkedPipeHandle = ReadLinkedHandleFromXData(br)
            };
        }

        /// <summary>
        /// 按配置的候选管长字段别名优先级列表（如 LEN, LENGTH, PIPE_LEN, 管长, 长度, CD）提取并解析管线声明长度。
        /// </summary>
        public static double? ResolveDeclaredLengthFromAttributes(
            IDictionary<string, object> attrs,
            IEnumerable<string> lengthAliases = null)
        {
            if (attrs == null || attrs.Count == 0)
                return null;

            var aliases = lengthAliases ??
                          CadConfig.Current?.Algorithms?.SpatialMatch?.LengthAttributeAliases ??
                          (IEnumerable<string>)new[] { "LEN", "LENGTH", "PIPE_LEN", "管长", "长度", "CD" };

            foreach (string alias in aliases)
            {
                if (string.IsNullOrWhiteSpace(alias))
                    continue;

                if (attrs.TryGetValue(alias.Trim(), out object rawVal) && rawVal != null)
                {
                    string text = rawVal.ToString().Trim();
                    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedLen) &&
                        parsedLen > 0)
                    {
                        return parsedLen;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// 当图纸属性块使用了非标准字段别名（如用 LENGTH 代替 LEN、用 HEIGHT 代替 HEITH、用 DN 代替 WIDTH）时，
        /// 自动在属性字典中补齐标准键名（LEN / WIDTH / HEITH），保证下游合并与对比算法无缝读取。
        /// </summary>
        public static void NormalizeCanonicalAttributeAliases(
            IDictionary<string, object> attrs,
            PipeSpatialMatchOptions options = null)
        {
            if (attrs == null || attrs.Count == 0)
                return;

            var spatialCfg = CadConfig.Current?.Algorithms?.SpatialMatch;

            if (!attrs.ContainsKey("LEN"))
            {
                var lenAliases = options?.LengthAttributeAliases ?? spatialCfg?.LengthAttributeAliases;
                string matchedVal = FindFirstNonEmptyAttributeValue(attrs, lenAliases);
                if (!string.IsNullOrWhiteSpace(matchedVal))
                    attrs["LEN"] = matchedVal;
            }

            if (!attrs.ContainsKey("WIDTH"))
            {
                var widthAliases = options?.WidthAttributeAliases ?? spatialCfg?.WidthAttributeAliases;
                string matchedVal = FindFirstNonEmptyAttributeValue(attrs, widthAliases);
                if (!string.IsNullOrWhiteSpace(matchedVal))
                    attrs["WIDTH"] = matchedVal;
            }

            if (!attrs.ContainsKey("HEITH"))
            {
                var heightAliases = options?.HeightAttributeAliases ?? spatialCfg?.HeightAttributeAliases;
                string matchedVal = FindFirstNonEmptyAttributeValue(attrs, heightAliases);
                if (!string.IsNullOrWhiteSpace(matchedVal))
                    attrs["HEITH"] = matchedVal;
            }
        }

        private static string FindFirstNonEmptyAttributeValue(
            IDictionary<string, object> attrs,
            IEnumerable<string> aliases)
        {
            if (attrs == null || aliases == null)
                return null;

            foreach (string alias in aliases)
            {
                if (string.IsNullOrWhiteSpace(alias))
                    continue;

                if (attrs.TryGetValue(alias.Trim(), out object val) && val != null)
                {
                    string str = val.ToString().Trim();
                    if (!string.IsNullOrEmpty(str))
                        return str;
                }
            }

            return null;
        }

        /// <summary>
        /// 读取实体上挂载的 PIPE_CHECK_LINK XData 配对句柄。
        /// </summary>
        public static string ReadLinkedHandleFromXData(DBObject obj)
        {
            if (obj == null)
                return string.Empty;

            try
            {
                using (ResultBuffer rb = obj.GetXDataForApplication(XDataRegAppName))
                {
                    if (rb == null)
                        return string.Empty;

                    foreach (TypedValue tv in rb)
                    {
                        if (tv.TypeCode == (short)DxfCode.ExtendedDataAsciiString &&
                            tv.Value is string str &&
                            !string.IsNullOrWhiteSpace(str))
                        {
                            return str.Trim();
                        }
                    }
                }
            }
            catch
            {
                // 忽略未注册 RegApp 或读取异常
            }

            return string.Empty;
        }

        /// <summary>
        /// 向实体写入 PIPE_CHECK_LINK XData 配对句柄（需在写事务中且已确保 RegAppTable 包含 PIPE_CHECK_LINK）。
        /// </summary>
        public static void WriteLinkedHandleToXData(DBObject obj, string linkedHandle)
        {
            if (obj == null || string.IsNullOrWhiteSpace(linkedHandle))
                return;

            using (var rb = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, XDataRegAppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, linkedHandle.Trim())))
            {
                obj.XData = rb;
            }
        }

        /// <summary>
        /// 清除实体上的 PIPE_CHECK_LINK XData 配对句柄（用于 Undo 恢复执行前无 XData 的原始状态）。
        /// 在 ObjectARX 中，向 XData 赋值仅包含 ExtendedDataRegAppName 的空 ResultBuffer 即可移除该应用下的扩展数据。
        /// </summary>
        public static void ClearLinkedHandleFromXData(DBObject obj)
        {
            if (obj == null)
                return;

            try
            {
                using (ResultBuffer existing = obj.GetXDataForApplication(XDataRegAppName))
                {
                    if (existing == null)
                        return;
                }

                using (var rb = new ResultBuffer(
                    new TypedValue((int)DxfCode.ExtendedDataRegAppName, XDataRegAppName)))
                {
                    obj.XData = rb;
                }
            }
            catch
            {
                // 忽略清理异常
            }
        }

        /// <summary>
        /// 确保数据库中已注册 PIPE_CHECK_LINK 扩展数据应用名。
        /// </summary>
        public static void EnsureRegAppRegistered(Database db, Transaction tr)
        {
            if (db == null || tr == null)
                return;

            RegAppTable rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (!rat.Has(XDataRegAppName))
            {
                rat.UpgradeOpen();
                var record = new RegAppTableRecord { Name = XDataRegAppName };
                rat.Add(record);
                tr.AddNewlyCreatedDBObject(record, true);
            }
        }

        /// <summary>
        /// 计算二维平面点到 NTS LineString 折线的最短距离、最近投影点及所在折线段的方向角（度，0~360）。
        /// </summary>
        public static double ComputePointToLineStringDistance(
            Point3d pt,
            LineString lineString,
            out Point3d closestPoint,
            out double closestSegmentAngleDegrees)
        {
            closestPoint = pt;
            closestSegmentAngleDegrees = 0.0;

            if (lineString == null || lineString.IsEmpty)
                return double.MaxValue;

            Coordinate[] coords = lineString.Coordinates;
            if (coords == null || coords.Length < 2)
                return double.MaxValue;

            double minDistSq = double.MaxValue;
            double bestX = coords[0].X;
            double bestY = coords[0].Y;
            double bestAngle = 0.0;

            for (int i = 0; i < coords.Length - 1; i++)
            {
                double x1 = coords[i].X;
                double y1 = coords[i].Y;
                double x2 = coords[i + 1].X;
                double y2 = coords[i + 1].Y;

                double dx = x2 - x1;
                double dy = y2 - y1;
                double lenSq = dx * dx + dy * dy;

                double projX, projY;
                if (lenSq <= 1e-12)
                {
                    projX = x1;
                    projY = y1;
                }
                else
                {
                    double t = ((pt.X - x1) * dx + (pt.Y - y1) * dy) / lenSq;
                    if (t < 0.0) t = 0.0;
                    else if (t > 1.0) t = 1.0;

                    projX = x1 + t * dx;
                    projY = y1 + t * dy;
                }

                double distSq = (pt.X - projX) * (pt.X - projX) + (pt.Y - projY) * (pt.Y - projY);
                if (distSq < minDistSq)
                {
                    minDistSq = distSq;
                    bestX = projX;
                    bestY = projY;
                    if (lenSq > 1e-12)
                    {
                        bestAngle = (Math.Atan2(dy, dx) * (180.0 / Math.PI) + 360.0) % 360.0;
                    }
                }
            }

            closestPoint = new Point3d(bestX, bestY, 0);
            closestSegmentAngleDegrees = bestAngle;
            return Math.Sqrt(minDistSq);
        }

        /// <summary>
        /// 计算两个角度（度，0~360）作为无向直线时的平行夹角差（范围 0~90 度）。
        /// </summary>
        public static double ComputeParallelAngleDifference(double angleA, double angleB)
        {
            double diff = Math.Abs(angleA - angleB) % 180.0;
            if (diff > 90.0)
                diff = 180.0 - diff;
            return diff;
        }

        private static double Distance2D(Point3d a, Point3d b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private sealed class CandidatePair
        {
            public SpatialPipeTarget Pipe { get; set; }
            public SpatialAttachmentItem Attachment { get; set; }
            public SpatialMatchChannel Channel { get; set; }
            public double DistanceToCenter { get; set; }
            public double DistanceToCurve { get; set; }
            public double? LengthDiff { get; set; }
            public bool IsLengthMatched { get; set; }
            public double? AngleDiff { get; set; }
            public Point3d ClosestPointOnPipe { get; set; }
            public double Score { get; set; }
        }
    }
}
