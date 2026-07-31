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

        public static List<CompareResult> MatchTargetPolylinesToMergedBaseLayer(
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

                CompareResult matchResult =
                    FindBestMergedBaseForTargetPolyline(
                        targetPolyline,
                        mergedBaseIndex,
                        geometryFactory,
                        match_options);

                results.Add(matchResult);
            }

            return results;
        }

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

        private static CompareResult FindBestMergedBaseForTargetPolyline(
            CadPolyline targetPolyline,
            STRtree<CadPolyline> mergedBaseIndex,
            GeometryFactory geometryFactory,
            PolylineMatchOptions options)
        {
            var emptyResult = new CompareResult
            {
                TargetPolyline = targetPolyline
            };

            if (!GeometryHelper.IsValidPolylineGeometry(targetPolyline))
                return emptyResult;

            List<CadPolyline> candidates =
                QueryBaseCandidatesForTarget(
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
                    EvaluateTargetAgainstBase(
                        targetPolyline,
                        candidateBase,
                        geometryFactory,
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

        private static List<CadPolyline> QueryBaseCandidatesForTarget(
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

            NtsGeometry baseBuffer = basePolyline.Geometry.Buffer(tolerance);
            NtsGeometry targetBuffer = targetPolyline.Geometry.Buffer(tolerance);

            if (baseBuffer == null || targetBuffer == null)
                return false;

            if (!baseBuffer.Intersects(targetBuffer))
                return false;

            NtsGeometry intersection = baseBuffer.Intersection(targetBuffer);

            if (intersection == null || intersection.IsEmpty)
                return false;

            double minArea = Math.Min(baseBuffer.Area, targetBuffer.Area);

            if (minArea <= 0)
                return false;

            double overlapRatio = intersection.Area / minArea;

            return overlapRatio >= options.MinBufferOverlapRatio;
        }

        private static CompareResult EvaluateTargetAgainstBase(
            CadPolyline targetPolyline,
            CadPolyline candidateBasePolyline,
            GeometryFactory geometryFactory,
            PolylineMatchOptions options)
        {
            if (!GeometryHelper.IsValidPolylineGeometry(targetPolyline))
                return null;

            if (!GeometryHelper.IsValidPolylineGeometry(candidateBasePolyline))
                return null;

            LineString targetLine = targetPolyline.Geometry as LineString;
            LineString baseLine = candidateBasePolyline.Geometry as LineString;

            if (targetLine == null || baseLine == null)
                return null;

            if (targetLine.Length <= 0 || baseLine.Length <= 0)
                return null;

            List<Coordinate> samplePoints =
                SampleLineStringByStep(
                    targetLine,
                    options.SampleStep);

            if (samplePoints.Count == 0)
                return null;

            int badCount = 0;
            double totalDistance = 0;
            double maxDistance = 0;

            double minMeasure = double.MaxValue;
            double maxMeasure = double.MinValue;

            int acceptedProjectionCount = 0;

            foreach (Coordinate point in samplePoints)
            {
                ProjectionResult projection =
                    ProjectPointToLineStringMeasure(
                        point,
                        baseLine);

                if (projection == null)
                    continue;

                double distance = projection.Distance;

                totalDistance += distance;
                maxDistance = Math.Max(maxDistance, distance);

                if (distance > options.Tolerance)
                {
                    badCount++;
                }
                else
                {
                    minMeasure = Math.Min(minMeasure, projection.Measure);
                    maxMeasure = Math.Max(maxMeasure, projection.Measure);
                    acceptedProjectionCount++;
                }
            }

            if (acceptedProjectionCount == 0)
                return null;

            double averageDistance =
                totalDistance / samplePoints.Count;

            double badPointRatio =
                (double)badCount / samplePoints.Count;

            if (badPointRatio > options.MaxBadPointRatio)
                return null;

            if (averageDistance > options.MaxAverageDistance)
                return null;

            if (maxDistance > options.MaxDistance)
                return null;

            if (minMeasure == double.MaxValue ||
                maxMeasure == double.MinValue)
            {
                return null;
            }

            var interval = new SegmentInterval(
                Math.Min(minMeasure, maxMeasure),
                Math.Max(minMeasure, maxMeasure));

            if (interval.Length < options.MinProjectedLength)
                return null;

            var result = new CompareResult
            {
                TargetPolyline = targetPolyline,
                BestBasePolyline = candidateBasePolyline,
                CoveredIntervalOnBase = interval,
                AverageDistance = averageDistance,
                MaxDistance = maxDistance,
                BadPointRatio = badPointRatio
            };

            result.Score = CalculateMatchScore(
                targetLine,
                baseLine,
                interval,
                averageDistance,
                maxDistance,
                badPointRatio,
                options);

            return result;
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
                    ProjectPointToSegment(point, a, b);

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

        private static ProjectionResultOnSegment ProjectPointToSegment(
            Coordinate p,
            Coordinate a,
            Coordinate b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;

            double len2 = dx * dx + dy * dy;

            if (len2 <= 0)
            {
                double d0 = p.Distance(a);

                return new ProjectionResultOnSegment
                {
                    T = 0,
                    Distance = d0,
                    Coordinate = new Coordinate(a)
                };
            }

            double t =
                ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;

            if (t < 0)
                t = 0;

            if (t > 1)
                t = 1;

            Coordinate projected = new Coordinate(
                a.X + t * dx,
                a.Y + t * dy);

            return new ProjectionResultOnSegment
            {
                T = t,
                Distance = p.Distance(projected),
                Coordinate = projected
            };
        }
    }
}
