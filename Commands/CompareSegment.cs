using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using process_pipeline.Core;
using process_pipeline.Geometry;
using process_pipeline.Models;
using process_pipeline.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AcadDb = Autodesk.AutoCAD.DatabaseServices;

namespace process_pipeline.Commands
{
    /// <summary>
    /// 线段覆盖比较。
    /// 
    /// 这是整个算法的核心类。
    /// 
    /// 主要功能：
    /// 1. 对 targetSegments 建立 STRtree 空间索引；
    /// 2. 遍历 baseSegments；
    /// 3. 查询每条 base segment 附近的 target candidate；
    /// 4. 对候选线段做共线判断；
    /// 5. 将 target segment 投影到 base segment 上；
    /// 6. 得到覆盖区间；
    /// 7. 合并覆盖区间；
    /// 8. 计算缺失区间；
    /// 9. 把缺失区间转成实际 CadSegment。
    /// </summary>
    public class CompareSegment : CadCommandBase
    {
        [CommandMethod("CompareSegment", CommandFlags.Session | CommandFlags.Redraw)]
        public override void Execute()
        {
            var geometryFactory = new GeometryFactory();
            var service = new CompareSegmentService(Doc.Database, Doc.Editor, geometryFactory);
            service.Run("比较segment");
        }
    }

    public class CompareSegmentService : CadBase<Dictionary<ObjectId, ProblemItem>> { 
        private readonly GeometryFactory _geometryFactory;
        private readonly AcadDb.Database _db;
        private readonly Editor _ed;

        public CompareSegmentService(AcadDb.Database db, Editor ed, GeometryFactory geometryFactory) : base(db, ed)
        {
            _ed = ed;
            _db = db;
            _geometryFactory = geometryFactory;
        }

        protected override Dictionary<ObjectId, ProblemItem> Execute(ProgressContext context)
        {
            throw new NotImplementedException();
        }

        protected override Dictionary<ObjectId, ProblemItem> Execute(ProgressContext context, List<ObjectId> objectIds)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// 执行方向性比较。
        /// 
        /// baseSegments：
        ///     基准线段集合。
        /// 
        /// targetSegments：
        ///     用来覆盖 baseSegments 的目标线段集合。
        /// 
        /// tolerance：
        ///     几何容差，单位与 CAD 图纸单位一致。
        /// 
        /// 返回：
        ///     baseSegments 中没有被 targetSegments 覆盖的部分。
        /// 
        /// 例如：
        /// Compare(A, B, tol)
        ///     表示找出 A 中没有被 B 覆盖的部分。
        /// 
        /// Compare(B, A, tol)
        ///     表示找出 B 中没有被 A 覆盖的部分，即 B 的多余部分。
        /// </summary>
        public CompareResult CompareCoverage(
            IList<CadSegment> baseSegments,
            IList<CadSegment> targetSegments,
            double tolerance)
        {
            var result = new CompareResult
            {
                BaseSegmentCount = baseSegments.Count,
                TargetSegmentCount = targetSegments.Count
            };

            var targetIndex = BuildSpatialIndex(targetSegments);

            foreach (var baseSeg in baseSegments)
            {
                if (baseSeg.Length <= tolerance)
                    continue;

                // 查询包围盒附近的候选线段。
                var queryEnv = new Envelope(baseSeg.Geometry.EnvelopeInternal);
                queryEnv.ExpandBy(tolerance);

                IList<CadSegment> candidates = targetIndex.Query(queryEnv);

                var coveredIntervals = new List<SegmentInterval>();

                foreach (var targetSeg in candidates)
                {
                    if (targetSeg.Length <= tolerance)
                        continue;

                    // 第一步：判断是否近似共线。
                    if (!Geometry.Math.IsCollinear(baseSeg, targetSeg, tolerance))
                        continue;

                    // 第二步：把 targetSeg 投影到 baseSeg 参数轴上。
                    var interval = Geometry.Math.ProjectSegmentToBase(baseSeg, targetSeg);

                    // 第三步：判断投影区间是否和 [0,1] 有重叠。
                    if (!Geometry.Math.HasOverlapWithUnitInterval(interval, tolerance))
                        continue;

                    // 第四步：裁剪到 [0,1]。
                    var clipped = Geometry.Math.ClipToUnitInterval(interval);

                    // 避免极小区间。
                    if (System.Math.Abs(clipped.End - clipped.Start) <= 1e-12)
                        continue;

                    coveredIntervals.Add(clipped);
                }

                // 合并 target 对 base 的所有覆盖区间。
                var mergedCovered = IntervalUtils.MergeIntervals(coveredIntervals, tolerance);

                // 这一段是新增的：把已覆盖区间转成实际重叠线段。
                foreach (var coveredInterval in mergedCovered)
                {
                    var coveredSeg = Geometry.Math.CreateSubSegment(
                        baseSeg,
                        coveredInterval,
                        _geometryFactory);

                    if (coveredSeg.Length > tolerance)
                    {
                        result.CoveredSegments.Add(coveredSeg);
                    }
                }

                // 求 baseSeg 上未被覆盖的区间。
                var missingIntervals = IntervalUtils.GetMissingIntervalsInUnit(mergedCovered, tolerance);

                // 把缺失区间转成实际线段。
                foreach (var missingInterval in missingIntervals)
                {
                    var missingSeg = Geometry.Math.CreateSubSegment(
                        baseSeg,
                        missingInterval,
                        _geometryFactory);

                    if (missingSeg.Length > tolerance)
                    {
                        result.MissingSegments.Add(missingSeg);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 对目标线段集合建立 STRtree 空间索引。
        /// 
        /// STRtree 是 R-tree 的一种变体，
        /// 非常适合“一次构建，多次查询”的空间检索场景。
        /// </summary>
        private STRtree<CadSegment> BuildSpatialIndex(IList<CadSegment> segments)
        {
            var index = new STRtree<CadSegment>();

            foreach (var seg in segments)
            {
                if (seg.Geometry == null)
                    continue;

                index.Insert(seg.Geometry.EnvelopeInternal, seg);
            }

            index.Build();

            return index;
        }
    }

    public static class CadSegmentExtractionService
    {
        /// <summary>
        /// 从指定 Database 的 ModelSpace 中提取某个图层的线段。
        /// 
        /// 参数说明：
        /// db:
        ///     要读取的 Database，可以是当前图，也可以是 side database。
        /// 
        /// targetLayer:
        ///     需要提取的图层名。
        /// 
        /// datasetName:
        ///     数据集标识，例如 A 或 B。
        /// 
        /// transform:
        ///     坐标变换矩阵。
        ///     如果不需要变换，传 Matrix3d.Identity。
        /// 
        /// sourceFilePath:
        ///     来源 DWG 路径。
        /// 
        /// isFromSideDatabase:
        ///     是否来自 side database。
        /// </summary>
        public static List<CadSegment> ExtractSegmentsFromDatabaseModelSpace(
            AcadDb.Database db,
            string targetLayer,
            string datasetName,
            GeometryFactory geometryFactory,
            double arcMaxSegmentLength,
            Matrix3d transform,
            string sourceFilePath,
            bool isFromSideDatabase)
        {
            var result = new List<CadSegment>();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt =
                    (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

                BlockTableRecord modelSpace =
                    (BlockTableRecord)tr.GetObject(
                        bt[BlockTableRecord.ModelSpace],
                        OpenMode.ForRead);

                foreach (ObjectId id in modelSpace)
                {
                    if (!id.IsValid || id.IsErased)
                        continue;

                    Entity entity = tr.GetObject(id, OpenMode.ForRead) as Entity;

                    if (entity == null)
                        continue;

                    if (!(entity is Line) && !(entity is Polyline))
                        continue;

                    if (!string.Equals(entity.Layer, targetLayer, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var segs = PolylineExtractor.ExtractSegmentsFromEntity(
                        entity,
                        datasetName,
                        geometryFactory,
                        arcMaxSegmentLength,
                        transform,
                        sourceFilePath,
                        isFromSideDatabase);

                    result.AddRange(segs);
                }

                tr.Commit();
            }

            return result;
        }

        /// <summary>
        /// 从当前 DWG 中的 BlockReference 内部提取指定图层的线段。
        /// 
        /// 注意：
        /// 这里只处理一层块。
        /// 如果存在嵌套块，需要递归展开。
        /// </summary>
        public static List<CadSegment> ExtractSegmentsFromBlockReference(
            AcadDb.Database db,
            Transaction tr,
            BlockReference blockRef,
            string targetLayer,
            string datasetName,
            GeometryFactory geometryFactory,
            double arcMaxSegmentLength)
        {
            var result = new List<CadSegment>();

            Matrix3d blockTransform = blockRef.BlockTransform;

            BlockTableRecord blockDef =
                (BlockTableRecord)tr.GetObject(
                    blockRef.BlockTableRecord,
                    OpenMode.ForRead);

            foreach (ObjectId id in blockDef)
            {
                if (!id.IsValid || id.IsErased)
                    continue;

                Entity entity = tr.GetObject(id, OpenMode.ForRead) as Entity;

                if (entity == null)
                    continue;

                if (!(entity is Line) && !(entity is Polyline))
                    continue;

                if (!string.Equals(entity.Layer, targetLayer, StringComparison.OrdinalIgnoreCase))
                    continue;

                var segs = PolylineExtractor.ExtractSegmentsFromEntity(
                    entity,
                    datasetName,
                    geometryFactory,
                    arcMaxSegmentLength,
                    blockTransform,
                    db.Filename,
                    false);

                result.AddRange(segs);
            }

            return result;
        }
    }

}
