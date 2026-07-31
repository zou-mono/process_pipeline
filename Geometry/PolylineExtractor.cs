using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using process_pipeline.Models;
using NetTopologySuite.Geometries;
using Coordinate = NetTopologySuite.Geometries.Coordinate;

namespace process_pipeline.Geometry
{
    public static class PolylineExtractor
    {
        /// <summary>
        /// 从 Entity 中提取 CadSegment。
        /// 
        /// 参数 transform 的作用：
        /// 1. 如果从当前主图直接提取，传 Matrix3d.Identity；
        /// 2. 如果从 BlockReference 内部提取，传 BlockReference.BlockTransform；
        /// 3. 如果从 side database 提取但坐标本来一致，也传 Matrix3d.Identity；
        /// 4. 如果从 side database 提取后需要模拟插入位置，则传对应插入矩阵。
        /// </summary>
        public static List<CadSegment> ExtractSegmentsFromEntity(
            Entity entity,
            string datasetName,
            GeometryFactory geometryFactory,
            double arcMaxSegmentLength,
            Matrix3d transform,
            string sourceFilePath,
            bool isFromSideDatabase)
        {
            var result = new List<CadSegment>();

            if (entity is Line line)
            {
                result.Add(CreateSegmentFromLine(
                    line,
                    datasetName,
                    geometryFactory,
                    transform,
                    sourceFilePath,
                    isFromSideDatabase));
            }
            else if (entity is Polyline polyline)
            {
                result.AddRange(CreateSegmentsFromPolyline(
                    polyline,
                    datasetName,
                    geometryFactory,
                    arcMaxSegmentLength,
                    transform,
                    sourceFilePath,
                    isFromSideDatabase));
            }

            return result;
        }

        private static CadSegment CreateSegmentFromLine(
            Line line,
            string datasetName,
            GeometryFactory geometryFactory,
            Matrix3d transform,
            string sourceFilePath,
            bool isFromSideDatabase)
        {
            Point3d p0 = line.StartPoint.TransformBy(transform);
            Point3d p1 = line.EndPoint.TransformBy(transform);

            var s = new Coordinate(p0.X, p0.Y);
            var e = new Coordinate(p1.X, p1.Y);

            return new CadSegment
            {
                Start = s,
                End = e,
                Geometry = geometryFactory.CreateLineString(new[] { s, e }),
                SourceObjectIds = new List<ObjectId> { line.ObjectId },
                SourceHandle = line.Handle.ToString(),
                SourceFilePath = sourceFilePath,
                IsFromSideDatabase = isFromSideDatabase,
                SourceSegmentIndex = 0,
                LayerName = line.Layer,
                DatasetName = datasetName
            };
        }

        private static List<CadSegment> CreateSegmentsFromPolyline(
            Polyline pl,
            string datasetName,
            GeometryFactory geometryFactory,
            double arcMaxSegmentLength,
            Matrix3d transform,
            string sourceFilePath,
            bool isFromSideDatabase)
        {
            var result = new List<CadSegment>();

            int vertexCount = pl.NumberOfVertices;

            if (vertexCount < 2)
                return result;

            int segmentCount = pl.Closed ? vertexCount : vertexCount - 1;

            for (int i = 0; i < segmentCount; i++)
            {
                int j = (i + 1) % vertexCount;

                Point2d rawP0 = pl.GetPoint2dAt(i);
                Point2d rawP1 = pl.GetPoint2dAt(j);

                double bulge = pl.GetBulgeAt(i);

                if (Math.Abs(bulge) < 1e-12)
                {
                    Point3d tp0 = new Point3d(rawP0.X, rawP0.Y, 0).TransformBy(transform);
                    Point3d tp1 = new Point3d(rawP1.X, rawP1.Y, 0).TransformBy(transform);

                    var s = new Coordinate(tp0.X, tp0.Y);
                    var e = new Coordinate(tp1.X, tp1.Y);

                    if (Distance(s, e) < 1e-12)
                        continue;

                    result.Add(new CadSegment
                    {
                        Start = s,
                        End = e,
                        Geometry = geometryFactory.CreateLineString(new[] { s, e }),
                        SourceObjectIds = new List<ObjectId> { pl.ObjectId },
                        SourceHandle = pl.Handle.ToString(),
                        SourceFilePath = sourceFilePath,
                        IsFromSideDatabase = isFromSideDatabase,
                        SourceSegmentIndex = i,
                        LayerName = pl.Layer,
                        DatasetName = datasetName
                    });
                }
                else
                {
                    var arcSegments = TessellateBulgeSegment(
                        rawP0,
                        rawP1,
                        bulge,
                        pl.ObjectId,
                        pl.Handle.ToString(),
                        i,
                        pl.Layer,
                        datasetName,
                        geometryFactory,
                        arcMaxSegmentLength,
                        transform,
                        sourceFilePath,
                        isFromSideDatabase);

                    result.AddRange(arcSegments);
                }
            }

            return result;
        }

        /// <summary>
        /// 将 bulge 弧段离散化。
        /// 
        /// 注意：
        /// 离散点先在原始 DWG 坐标中生成，
        /// 然后每个点再 TransformBy(transform) 转到目标坐标系。
        /// </summary>
        private static List<CadSegment> TessellateBulgeSegment(
            Point2d p0,
            Point2d p1,
            double bulge,
            ObjectId sourceObjectId,
            string sourceHandle,
            int sourceSegmentIndex,
            string layerName,
            string datasetName,
            GeometryFactory geometryFactory,
            double arcMaxSegmentLength,
            Matrix3d transform,
            string sourceFilePath,
            bool isFromSideDatabase)
        {
            var result = new List<CadSegment>();

            double x0 = p0.X;
            double y0 = p0.Y;
            double x1 = p1.X;
            double y1 = p1.Y;

            double chord = p0.GetDistanceTo(p1);

            if (chord < 1e-12)
                return result;

            double theta = 4.0 * Math.Atan(bulge);
            double radius = chord / (2.0 * Math.Sin(Math.Abs(theta) / 2.0));

            double mx = (x0 + x1) / 2.0;
            double my = (y0 + y1) / 2.0;

            double dx = (x1 - x0) / chord;
            double dy = (y1 - y0) / chord;

            double nx = -dy;
            double ny = dx;

            double h = radius * Math.Cos(Math.Abs(theta) / 2.0);

            double sign = Math.Sign(bulge);

            double cx = mx + sign * nx * h;
            double cy = my + sign * ny * h;

            double startAngle = Math.Atan2(y0 - cy, x0 - cx);

            double arcLength = Math.Abs(radius * theta);
            int pieces = Math.Max(2, (int)Math.Ceiling(arcLength / Math.Max(arcMaxSegmentLength, 1e-6)));

            var points = new List<Coordinate>();

            for (int i = 0; i <= pieces; i++)
            {
                double t = (double)i / pieces;
                double angle = startAngle + theta * t;

                double x = cx + radius * Math.Cos(angle);
                double y = cy + radius * Math.Sin(angle);

                Point3d transformedPoint = new Point3d(x, y, 0).TransformBy(transform);

                points.Add(new Coordinate(transformedPoint.X, transformedPoint.Y));
            }

            for (int i = 0; i < points.Count - 1; i++)
            {
                var s = points[i];
                var e = points[i + 1];

                if (Distance(s, e) < 1e-12)
                    continue;

                result.Add(new CadSegment
                {
                    Start = s,
                    End = e,
                    Geometry = geometryFactory.CreateLineString(new[] { s, e }),
                    SourceObjectIds = new List<ObjectId> { sourceObjectId } ,
                    SourceHandle = sourceHandle,
                    SourceFilePath = sourceFilePath,
                    IsFromSideDatabase = isFromSideDatabase,
                    SourceSegmentIndex = sourceSegmentIndex,
                    LayerName = layerName,
                    DatasetName = datasetName
                });
            }

            return result;
        }

        private static double Distance(Coordinate a, Coordinate b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
