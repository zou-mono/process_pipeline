using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
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

namespace process_pipeline.Commands
{
    public class MatchPipe : CadCommandBase
    {
        [CommandMethod("MatchPipe", CommandFlags.Session | CommandFlags.Redraw)]
        public override void Execute()
        {
            var service = new ClearLayerService(Doc.Database, Doc.Editor);
            service.Run("匹配管道数据");
        }
    }

    public class MatchPipeService : CadBase<Dictionary<ObjectId, ProblemItem>>
    {
        public MatchPipeService(Autodesk.AutoCAD.DatabaseServices.Database db, Editor ed) : base(db, ed)
        {
        }

        protected override Dictionary<ObjectId, ProblemItem> Execute(ProgressContext context)
        {
            throw new NotImplementedException();
        }

        protected override Dictionary<ObjectId, ProblemItem> Execute(ProgressContext context, List<ObjectId> objectIds)
        {
            throw new NotImplementedException();
        }

        public static List<CompareResult> PipeMatcher(
            List<CadPolyline> baseLayerPolylines,
            List<CadPolyline> targetLayerPolylines,
            GeometryFactory geometryFactory,
            PolylineMatchOptions match_options,
            PolylineMergeOptions merge_options)
        {
            if (baseLayerPolylines == null)
                baseLayerPolylines = new List<CadPolyline>();

            if (targetLayerPolylines == null)
                targetLayerPolylines = new List<CadPolyline>();

            if (match_options == null)
                match_options = new PolylineMatchOptions();

            if (merge_options == null)
                merge_options = new PolylineMergeOptions();

            // ============================================================
            // 1. A 是基准图层，必须先 merge。
            // ============================================================
            var merger = new PolylineGraphMerger(geometryFactory);
            var mergedBasePolylines = merger.Merge(baseLayerPolylines, merge_options);

            // ============================================================
            // 2. 用 merged A 建 STRtree。
            // ============================================================
            STRtree<CadPolyline> mergedBaseIndex =
                BuildPolylineIndex(mergedBasePolylines);

            var results = new List<CompareResult>();

            // ============================================================
            // 3. 主循环遍历 B。
            // ============================================================
            foreach (CadPolyline targetPolyline in targetLayerPolylines)
            {
                if (!GeometryHelper.IsValidPolylineGeometry(targetPolyline))
                    continue;

                //CompareResult matchResult =
                //    FindBestMergedBase(
                //        targetPolyline,
                //        mergedBaseIndex,
                //        geometryFactory,
                //        match_options);

                //results.Add(matchResult);
                List<CompareResult> targetResults =
                    FindSegmentMatchesForTarget(
                        targetPolyline,
                        mergedBaseIndex,
                        geometryFactory,
                        match_options);

                if (targetResults == null || targetResults.Count == 0)
                {
                    // 保留原来的行为：
                    // 如果 target 完全没有匹配，仍然返回一条未匹配结果，
                    // 这样调用方可以知道这条 target 没匹配上。
                    results.Add(new CompareResult
                    {
                        TargetPolyline = targetPolyline
                    });
                }
                else
                {
                    results.AddRange(targetResults);
                }
            }

            return results;
        }

        /// <summary>
        /// 为当前 target 寻找一组分段匹配结果。
        /// 
        /// 重要修改：
        /// 原来的 FindBestMergedBase 是：
        ///     一条 target -> 一个 best base。
        /// 
        /// 现在改成：
        ///     一条 target -> 多个 base 分段匹配。
        /// 
        /// 这样可以处理：
        /// target 比某条 base 长，target 的一部分匹配 base，
        /// 剩余部分继续匹配其他 base。
        /// </summary>
        private static List<CompareResult> FindSegmentMatchesForTarget(
            CadPolyline targetPolyline,
            STRtree<CadPolyline> mergedBaseIndex,
            GeometryFactory geometryFactory,
            PolylineMatchOptions options)
        {
            var empty = new List<CompareResult>();

            if (targetPolyline.SourceHandles[0] == "5114") {
                Console.WriteLine("debug");
            }

            if (!GeometryHelper.IsValidPolylineGeometry(targetPolyline))
                return empty;

            List<CadPolyline> candidates =
                QueryCandidateBases(
                    mergedBaseIndex,
                    targetPolyline,
                    options);

            if (candidates == null || candidates.Count == 0)
                return empty;

            var candidateResults = new List<CompareResult>();

            foreach (CadPolyline candidateBase in candidates)
            {
                if (!GeometryHelper.IsValidPolylineGeometry(candidateBase))
                    continue;

                if (options.UseBufferRoughFilter)
                {
                    if (!PassBufferRoughFilter(
                            candidateBase,
                            targetPolyline,
                            options))
                    {
                        continue;
                    }
                }

                CompareResult current =
                    ScoreMatch(
                        targetPolyline,
                        candidateBase,
                        //geometryFactory,
                        options);

                if (current == null)
                    continue;

                if (!current.IsMatched)
                    continue;

                if (current.CoveredIntervalOnTarget == null)
                    continue;

                candidateResults.Add(current);
            }

            if (candidateResults.Count == 0)
                return empty;

            // 从所有候选结果中挑选一组不明显重叠的 target 区间。
            // 这样同一条 target 可以被多个 base 分段覆盖。
            return SelectNonOverlappingTargetMatches(
                candidateResults,
                options);
        }

        // 建 base 的空间索引
        private static STRtree<CadPolyline> BuildPolylineIndex(
            IEnumerable<CadPolyline> polylines)
        {
            var index = new STRtree<CadPolyline>();

            if (polylines == null)
                return index;

            foreach (CadPolyline polyline in polylines)
            {
                if (!GeometryHelper.IsValidPolylineGeometry(polyline))
                    continue;

                Envelope env = polyline.Geometry.EnvelopeInternal;

                if (env == null || env.IsNull)
                    continue;

                index.Insert(env, polyline);
            }

            index.Build();

            return index;
        }

        // 为当前这条 target 多段线，寻找最匹配的 merged base 多段线
        private static CompareResult FindBestMergedBase(
            CadPolyline targetPolyline,
            STRtree<CadPolyline> mergedBaseIndex,
            GeometryFactory geometryFactory,
            PolylineMatchOptions options)
        {
            var emptyResult = new CompareResult
            {
                TargetPolyline = targetPolyline
            };

            if (targetPolyline.SourceHandles[0] == "520B") {
                Console.WriteLine("debug");
            }

            if (!GeometryHelper.IsValidPolylineGeometry(targetPolyline))
                return emptyResult;

            List<CadPolyline> candidates =
                QueryCandidateBases(
                    mergedBaseIndex,
                    targetPolyline,
                    options);

            CompareResult best = null;

            foreach (CadPolyline candidateBase in candidates)
            {
                if (!GeometryHelper.IsValidPolylineGeometry(candidateBase))
                    continue;

                if (options.UseBufferRoughFilter)
                {
                    if (!PassBufferRoughFilter(
                            candidateBase,
                            targetPolyline,
                            options))
                    {
                        continue;
                    }
                }

                CompareResult current =
                    ScoreMatch(
                        targetPolyline,
                        candidateBase,
                        //geometryFactory,
                        options);

                if (current == null)
                    continue;

                if (!current.IsMatched)
                    continue;

                if (best == null || current.Score < best.Score)
                    best = current;
            }

            return best ?? emptyResult;
        }

        private static List<CadPolyline> QueryCandidateBases(
            STRtree<CadPolyline> mergedBaseIndex,
            CadPolyline targetPolyline,
            PolylineMatchOptions options)
        {
            var result = new List<CadPolyline>();

            if (mergedBaseIndex == null)
                return result;

            if (!GeometryHelper.IsValidPolylineGeometry(targetPolyline))
                return result;

            double expand = options.CandidateSearchExpand > 0
                ? options.CandidateSearchExpand
                : options.Tolerance;

            Envelope searchEnv = new Envelope(
                targetPolyline.Geometry.EnvelopeInternal);

            searchEnv.ExpandBy(expand);

            IList<CadPolyline> queryResult =
                mergedBaseIndex.Query(searchEnv);

            if (queryResult == null || queryResult.Count == 0)
                return result;

            foreach (CadPolyline candidate in queryResult)
            {
                if (!GeometryHelper.IsValidPolylineGeometry(candidate))
                    continue;

                result.Add(candidate);
            }

            return result;
        }

        // 是否通过 Buffer 粗筛
        private static bool PassBufferRoughFilter(
            CadPolyline basePolyline,
            CadPolyline targetPolyline,
            PolylineMatchOptions options)
        {
            if (!GeometryHelper.IsValidPolylineGeometry(basePolyline))
                return false;

            if (!GeometryHelper.IsValidPolylineGeometry(targetPolyline))
                return false;

            double tolerance = options.Tolerance;

            if (tolerance <= 0)
                return false;

            NtsGeometry baseGeometry = basePolyline.Geometry;
            NtsGeometry targetGeometry = targetPolyline.Geometry;

            if (baseGeometry == null || targetGeometry == null)
                return false;

            /*
             * 先做 Envelope 预筛。
             * 
             * 这一步比 Buffer 便宜很多。
             * 如果扩展 tolerance 之后的外包框都不相交，
             * 那么后面不需要再做 Buffer/Intersection。
             */
            Envelope baseEnv = new Envelope(baseGeometry.EnvelopeInternal);
            Envelope targetEnv = new Envelope(targetGeometry.EnvelopeInternal);

            baseEnv.ExpandBy(tolerance);
            targetEnv.ExpandBy(tolerance);

            if (!baseEnv.Intersects(targetEnv))
                return false;

            NtsGeometry baseBuffer = baseGeometry.Buffer(tolerance);
            NtsGeometry targetBuffer = targetGeometry.Buffer(tolerance);

            if (baseBuffer == null || targetBuffer == null)
                return false;

            if (baseBuffer.IsEmpty || targetBuffer.IsEmpty)
                return false;

            if (!baseBuffer.Intersects(targetBuffer))
                return false;

            NtsGeometry intersection = baseBuffer.Intersection(targetBuffer);

            if (intersection == null || intersection.IsEmpty)
                return false;

            double baseArea = baseBuffer.Area;
            double targetArea = targetBuffer.Area;

            if (baseArea <= 0 || targetArea <= 0)
                return false;

            /*
             * 关键说明：
             * 
             * 这里必须用较小 buffer 面积作为分母。
             * 
             * 如果 target 比 base 长，但 base 是 target 的一部分，
             * intersection 可能接近 baseBuffer.Area。
             * 
             * 如果此时用 targetBuffer.Area 作为分母，overlapRatio 会很低，
             * 从而误杀包含型匹配。
             */
            double minArea = Math.Min(baseArea, targetArea);

            double overlapRatio = intersection.Area / minArea;

            return overlapRatio >= options.MinBufferOverlapRatio;
        }

        /// <summary>
        /// 评估当前 target 与某条候选 base 的匹配程度。
        /// 
        /// 重要修改：
        /// 原算法把 target 全长都拿去和 base 比较，
        /// 如果 target 比 base 长，target 多出来的部分会被统计为 bad point。
        /// 
        /// 新算法改为：
        /// 沿 target 采样，找出当前 base 能覆盖 target 的连续区间。
        /// 当前 base 覆盖不到的 target 区间，不在这里作为 bad point 处理，
        /// 而是留给其他 base 继续匹配。
        /// </summary>
        private static CompareResult ScoreMatch(
            CadPolyline targetPolyline,
            CadPolyline candidateBasePolyline,
            //GeometryFactory geometryFactory,
            PolylineMatchOptions options)
        {
            if (!GeometryHelper.IsValidPolylineGeometry(targetPolyline))
                return null;

            if (!GeometryHelper.IsValidPolylineGeometry(candidateBasePolyline))
                return null;

            if (targetPolyline.SourceHandles[0] == "5114") {
                Console.WriteLine("debug");
            }

            LineString targetLine = targetPolyline.Geometry as LineString;
            LineString baseLine = candidateBasePolyline.Geometry as LineString;

            if (targetLine == null || baseLine == null)
                return null;

            if (targetLine.Length <= 0 || baseLine.Length <= 0)
                return null;

            List<MeasuredSamplePoint> samplePoints =
                SampleLineStringByStepWithMeasure(
                    targetLine,
                    options.SampleStep);

            if (samplePoints.Count == 0)
                return null;

            /*
             * acceptedRuns 的含义：
             * 
             * 每一个 run 表示 target 上一段连续的、可以被当前 base 覆盖的采样点。
             * 
             * 例如 target 很长，当前 base 只覆盖中间一段：
             * 
             * target:  ------------------------------
             * base:          ------------
             * accepted:      [xxxxxxxxxx]
             * 
             * 那么这里只会生成中间那一段 run。
             */
            var acceptedRuns = new List<List<AcceptedProjectionSample>>();
            List<AcceptedProjectionSample> currentRun = null;

            foreach (MeasuredSamplePoint sample in samplePoints)
            {
                ProjectionResult projection =
                    ProjectPointToLineStringMeasure(
                        sample.Point,
                        baseLine);

                bool accepted = false;

                if (projection != null &&
                    projection.Distance <= options.Tolerance)
                {
                    /*
                     * 方向一致性检查：
                     * 
                     * 仅仅距离近不够。
                     * 如果 target 和 base 在此处构成明显夹角，
                     * 即使投影距离很小，也不能认为它们是同一条管线。
                     */
                    bool directionAccepted =
                        CadMath.IsDirectionCompatibleAtMeasures(
                            targetLine,
                            sample.Measure,
                            baseLine,
                            projection.Measure,
                            options.MaxDirectionAngleDegrees);

                    accepted = directionAccepted;
                }

                //bool accepted =
                //    projection != null &&
                //    projection.Distance <= options.Tolerance;

                if (accepted)
                {
                    if (currentRun == null)
                    {
                        currentRun = new List<AcceptedProjectionSample>();
                        acceptedRuns.Add(currentRun);
                    }

                    currentRun.Add(new AcceptedProjectionSample
                    {
                        TargetMeasure = sample.Measure,
                        BaseMeasure = projection.Measure,
                        Distance = projection.Distance
                    });
                }
                else
                {
                    /*
                     * 关键修改点：
                     * 
                     * 以前这里会 badCount++。
                     * 
                     * 现在不能这么做。
                     * 因为当前 base 覆盖不到 target 的地方，
                     * 并不代表 target 这一段是坏点，
                     * 它可能应该匹配另一条 base。
                     * 
                     * 所以这里仅仅结束当前连续覆盖段。
                     */
                    currentRun = null;
                }
            }

            if (acceptedRuns.Count == 0)
                return null;

            CompareResult bestRunResult = null;

            foreach (List<AcceptedProjectionSample> run in acceptedRuns)
            {
                if (run == null || run.Count == 0)
                    continue;

                double targetStart = run.Min(x => x.TargetMeasure);
                double targetEnd = run.Max(x => x.TargetMeasure);

                double targetCoveredLength = Math.Max(0.0, targetEnd - targetStart);

                /*
                 * 复用你已有的 options.MinProjectedLength。
                 * 
                 * 原来它表示投影到 base 上的最小长度。
                 * 现在这里也用它过滤 target 上过短的覆盖段。
                 * 
                 * 如果你后面想更清晰，可以再单独加 MinMatchedTargetLength。
                 * 但为了少改类，这里先复用原参数。
                 */
                if (targetCoveredLength < options.MinProjectedLength)
                    continue;

                double baseStart = run.Min(x => x.BaseMeasure);
                double baseEnd = run.Max(x => x.BaseMeasure);

                double baseCoveredLength = Math.Max(0.0, baseEnd - baseStart);

                if (baseCoveredLength < options.MinProjectedLength)
                    continue;

                double averageDistance = run.Average(x => x.Distance);
                double maxDistance = run.Max(x => x.Distance);

                /*
                 * 新语义下：
                 * 当前 run 内的点全部都是 distance <= tolerance 的 accepted 点。
                 * 所以这个分段结果内部的 BadPointRatio 设为 0。
                 * 
                 * target 上未被当前 base 覆盖的部分，不在这里算 bad point，
                 * 而是交给其他 base 继续匹配。
                 */
                double badPointRatio = 0.0;

                var intervalOnTarget = new SegmentInterval(
                    Math.Min(targetStart, targetEnd),
                    Math.Max(targetStart, targetEnd));

                var intervalOnBase = new SegmentInterval(
                    Math.Min(baseStart, baseEnd),
                    Math.Max(baseStart, baseEnd));

                var result = new CompareResult
                {
                    TargetPolyline = targetPolyline,
                    BestBasePolyline = candidateBasePolyline,

                    // 原有字段：当前 target 片段投影到 base 上的区间。
                    CoveredIntervalOnBase = intervalOnBase,

                    // 新增字段：当前 base 覆盖 target 的哪个区间。
                    CoveredIntervalOnTarget = intervalOnTarget,

                    AverageDistance = averageDistance,
                    MaxDistance = maxDistance,
                    BadPointRatio = badPointRatio
                };

                result.Score = CalculateSegmentMatchScore(
                    targetLine,
                    baseLine,
                    intervalOnTarget,
                    intervalOnBase,
                    averageDistance,
                    maxDistance,
                    options);

                if (bestRunResult == null || result.Score < bestRunResult.Score)
                    bestRunResult = result;
            }

            return bestRunResult;
        }

        /// <summary>
        /// 分段匹配评分。
        /// 
        /// 注意：
        /// 这里不再用 target 全长和 base 投影长度做强比较。
        /// 因为当前结果只表示 target 的一个局部区间被当前 base 覆盖。
        /// 
        /// 分数越小越好。
        /// </summary>
        private static double CalculateSegmentMatchScore(
            LineString targetLine,
            LineString baseLine,
            SegmentInterval intervalOnTarget,
            SegmentInterval intervalOnBase,
            double averageDistance,
            double maxDistance,
            PolylineMatchOptions options)
        {
            if (targetLine == null || baseLine == null)
                return double.MaxValue;

            if (intervalOnTarget == null || intervalOnBase == null)
                return double.MaxValue;

            double targetSegmentLength = intervalOnTarget.Length;
            double baseSegmentLength = intervalOnBase.Length;

            if (targetSegmentLength <= 0 || baseSegmentLength <= 0)
                return double.MaxValue;

            /*
             * 分段长度差比例：
             * 
             * 比较的是：
             * 当前被 base 覆盖的 target 区间长度
             * 和
             * 它在 base 上对应的覆盖区间长度。
             * 
             * 而不是 target 全长和 base 全长。
             */
            double lengthDiffRatio =
                Math.Abs(targetSegmentLength - baseSegmentLength) /
                Math.Max(targetSegmentLength, baseSegmentLength);

            /*
             * 覆盖长度奖励：
             * 
             * 在距离差不多的情况下，优先选择覆盖 target 更长的候选。
             * 
             * 注意这是负项，所以覆盖越长，score 越低。
             * 系数不要太大，避免长但不准的匹配压过短但准确的匹配。
             */
            double coverageReward =
                -targetSegmentLength * 0.01;

            double score =
                averageDistance
                + maxDistance * 0.1
                + lengthDiffRatio * options.Tolerance
                + coverageReward;

            return score;
        }

        private static double CalculateMatchScore(
            LineString targetLine,
            LineString baseLine,
            SegmentInterval interval,
            double averageDistance,
            double maxDistance,
            double badPointRatio,
            PolylineMatchOptions options)
        {
            double targetLength = targetLine.Length;
            double projectedLength = interval != null
                ? interval.Length
                : 0;

            double lengthDiffRatio = 1.0;

            if (targetLength > 0)
            {
                lengthDiffRatio =
                    Math.Abs(projectedLength - targetLength) / targetLength;
            }

            double score =
                averageDistance
                + badPointRatio * options.Tolerance * 2.0
                + lengthDiffRatio * options.Tolerance
                + maxDistance * 0.1;

            return score;
        }

        private static List<Coordinate> SampleLineStringByStep(
            LineString line,
            double step)
        {
            var result = new List<Coordinate>();

            if (line == null || line.Length <= 0)
                return result;

            if (step <= 0)
                step = line.Length;

            double length = line.Length;

            Coordinate start = GetPointAtMeasure(line, 0);

            if (start != null)
                result.Add(start);

            double m = step;

            while (m < length)
            {
                Coordinate p = GetPointAtMeasure(line, m);

                if (p != null)
                    result.Add(p);

                m += step;
            }

            Coordinate end = GetPointAtMeasure(line, length);

            if (end != null)
                result.Add(end);

            return RemoveConsecutiveDuplicateCoordinates(result);
        }

        /// <summary>
        /// 按步长采样 LineString，并记录每个采样点在 LineString 上的累计长度位置。
        /// 
        /// 这是对现有 SampleLineStringByStep 的增强版，
        /// 没有替代原函数，避免影响其他代码。
        /// </summary>
        private static List<MeasuredSamplePoint> SampleLineStringByStepWithMeasure(
            LineString line,
            double step)
        {
            var result = new List<MeasuredSamplePoint>();

            if (line == null || line.Length <= 0)
                return result;

            if (step <= 0)
                step = line.Length;

            double length = line.Length;

            Coordinate start = GetPointAtMeasure(line, 0);

            if (start != null)
            {
                result.Add(new MeasuredSamplePoint
                {
                    Point = start,
                    Measure = 0
                });
            }

            double m = step;

            while (m < length)
            {
                Coordinate p = GetPointAtMeasure(line, m);

                if (p != null)
                {
                    result.Add(new MeasuredSamplePoint
                    {
                        Point = p,
                        Measure = m
                    });
                }

                m += step;
            }

            Coordinate end = GetPointAtMeasure(line, length);

            if (end != null)
            {
                result.Add(new MeasuredSamplePoint
                {
                    Point = end,
                    Measure = length
                });
            }

            return result;
        }

        private static Coordinate GetPointAtMeasure(
            LineString line,
            double measure)
        {
            if (line == null)
                return null;

            Coordinate[] coords = line.Coordinates;

            if (coords == null || coords.Length == 0)
                return null;

            if (measure <= 0)
                return new Coordinate(coords[0]);

            double totalLength = line.Length;

            if (measure >= totalLength)
                return new Coordinate(coords[coords.Length - 1]);

            double accumulated = 0;

            for (int i = 0; i < coords.Length - 1; i++)
            {
                Coordinate a = coords[i];
                Coordinate b = coords[i + 1];

                double segLen = a.Distance(b);

                if (segLen <= 0)
                    continue;

                if (accumulated + segLen >= measure)
                {
                    double t = (measure - accumulated) / segLen;

                    return new Coordinate(
                        a.X + (b.X - a.X) * t,
                        a.Y + (b.Y - a.Y) * t);
                }

                accumulated += segLen;
            }

            return new Coordinate(coords[coords.Length - 1]);
        }

        /// <summary>
        /// 从候选分段结果中选择一组尽量不重叠、分数较好的匹配。
        /// 
        /// 简单贪心策略：
        /// 1. 先按 Score 从小到大排序；
        /// 2. 分数相近时，优先选择覆盖 target 更长的；
        /// 3. 如果新候选与已选结果在 target 上明显重叠，则跳过。
        /// 
        /// 这样可以让一条 target 被多个 base 分段覆盖。
        /// </summary>
        private static List<CompareResult> SelectNonOverlappingTargetMatches(
            List<CompareResult> candidates,
            PolylineMatchOptions options)
        {
            var selected = new List<CompareResult>();

            if (candidates == null || candidates.Count == 0)
                return selected;

            // 当前阶段不做严格的“非重叠剔除”。
            // 原因：
            // 1. 区间来自采样 + 投影估算，不是严格切割；
            // 2. 边界会有天然误差；
            // 3. 硬性重叠判断容易误删本该保留的正确匹配段。
            //
            // 因此这里只做排序保留，让后续更高层逻辑决定如何处理分段关系。
            selected = candidates
                .Where(x => x != null && x.CoveredIntervalOnTarget != null)
                .OrderBy(x => x.Score)
                .ThenByDescending(x => x.CoveredIntervalOnTarget.Length)
                .ToList();

            //double overlapRatioThreshold =
            //    options.Tolerance > 0
            //        ? options.overlapRatio
            //        : 0.0;

            //foreach (CompareResult candidate in candidates
            //    .Where(x => x != null && x.CoveredIntervalOnTarget != null)
            //    .OrderBy(x => x.Score)
            //    .ThenByDescending(x => x.CoveredIntervalOnTarget.Length))
            //{
            //    bool conflict = selected.Any(existing =>
            //        IsTargetIntervalConflict(
            //            existing.CoveredIntervalOnTarget,
            //            candidate.CoveredIntervalOnTarget,
            //            overlapRatioThreshold));

            //    if (conflict)
            //        continue;

            //    selected.Add(candidate);
            //}

            return selected
                .OrderBy(x => x.CoveredIntervalOnTarget.Start)
                .ToList();
        }

        /// <summary>
        /// 判断两个 target 区间是否冲突。
        /// 
        /// 允许很小的重叠，避免因为采样步长或容差造成边界误差。
        /// </summary>
        private static bool IsTargetIntervalConflict(
            SegmentInterval a,
            SegmentInterval b,
            double overlapRatioThreshold)
        {
            if (a == null || b == null)
                return false;

            double start = Math.Max(a.Start, b.Start);
            double end = Math.Min(a.End, b.End);

            double overlap = end - start;

            if (overlap <= 0)
                return false;

            double shorterLength = Math.Min(a.Length, b.Length);
            if (shorterLength <= 0)
                return false;

            double overlapRatio = overlap / shorterLength;

            // 只有当重叠已经占到较短区间的大部分时，
            // 才认为是真正冲突。
            return overlapRatio >= overlapRatioThreshold;

            //return overlap > overlapTolerance;
        }

        private static List<Coordinate> RemoveConsecutiveDuplicateCoordinates(
            List<Coordinate> coordinates)
        {
            var result = new List<Coordinate>();

            if (coordinates == null || coordinates.Count == 0)
                return result;

            Coordinate last = null;

            foreach (Coordinate c in coordinates)
            {
                if (c == null)
                    continue;

                if (last == null || !last.Equals2D(c))
                {
                    result.Add(c);
                    last = c;
                }
            }

            return result;
        }

        private static ProjectionResult ProjectPointToLineStringMeasure(
            Coordinate point,
            LineString line)
        {
            if (point == null || line == null)
                return null;

            Coordinate[] coords = line.Coordinates;

            if (coords == null || coords.Length < 2)
                return null;

            double accumulated = 0;

            double bestDistance = double.MaxValue;
            double bestMeasure = 0;
            Coordinate bestCoordinate = null;

            for (int i = 0; i < coords.Length - 1; i++)
            {
                Coordinate a = coords[i];
                Coordinate b = coords[i + 1];

                double segLen = a.Distance(b);

                if (segLen <= 0)
                    continue;

                ProjectionResultOnSegment segProjection =
                    CadMath.ProjectPointToSegment(point, a, b);

                double measure =
                    accumulated + segProjection.T * segLen;

                if (segProjection.Distance < bestDistance)
                {
                    bestDistance = segProjection.Distance;
                    bestMeasure = measure;
                    bestCoordinate = segProjection.Coordinate;
                }

                accumulated += segLen;
            }

            return new ProjectionResult
            {
                Measure = bestMeasure,
                Distance = bestDistance,
                Coordinate = bestCoordinate
            };
        }

        /// <summary>
        /// 带 target 里程的采样点。
        /// 这是为了知道某个采样点位于 target 的哪一个长度位置。
        /// </summary>
        private class MeasuredSamplePoint
        {
            public Coordinate Point { get; set; }

            /// <summary>
            /// 当前点在 source LineString 上的累计长度位置。
            /// </summary>
            public double Measure { get; set; }
        }

        /// <summary>
        /// 一个被接受的投影采样点。
        /// 表示 target 上某个点可以被当前 base 覆盖。
        /// </summary>
        private class AcceptedProjectionSample
        {
            /// <summary>
            /// 采样点在 target 上的位置。
            /// </summary>
            public double TargetMeasure { get; set; }

            /// <summary>
            /// 采样点投影到 base 上的位置。
            /// </summary>
            public double BaseMeasure { get; set; }

            /// <summary>
            /// 点到 base 的距离。
            /// </summary>
            public double Distance { get; set; }
        }
    }
}
