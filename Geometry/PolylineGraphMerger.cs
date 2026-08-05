using Autodesk.AutoCAD.DatabaseServices;
using NetTopologySuite.Geometries;
using process_pipeline.Models;
using process_pipeline.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
//using pGraphNode = process_pipeline.Models.GraphNode;

namespace process_pipeline.Geometry
{
    /// <summary>
    /// 对 CadPolyline 进行属性感知的拓扑合并。
    /// 
    /// 合并逻辑：
    /// 1. 按 LayerName 和指定属性，例如 WIDTH，进行分组；
    /// 2. 每组内把 polyline 视为图的边；
    /// 3. polyline 起点和终点视为图节点；
    /// 4. 端点距离小于 NodeTolerance 的点归并成同一节点；
    /// 5. 沿 degree == 2 的节点继续合并；
    /// 6. 遇到 degree != 2 的节点停止；
    /// 7. 保留合并前的 SourcePolylines。
    /// </summary>
    public class PolylineGraphMerger
    {
        private readonly GeometryFactory _geometryFactory;

        public PolylineGraphMerger(GeometryFactory geometryFactory)
        {
            _geometryFactory = geometryFactory;
        }

        /// <summary>
        /// 对输入 polyline 进行拓扑合并。
        /// </summary>
        /// <param name="polylines">
        /// 原始 B 图层多段线集合。
        /// </param>
        /// <param name="options">
        /// 合并参数。
        /// </param>
        /// <returns>
        /// 合并后的 CadPolyline 集合。
        /// </returns>
        public List<CadPolyline> Merge(
            IList<CadPolyline> polylines,
            PolylineMergeOptions options)
        {
            if (options == null)
                options = new PolylineMergeOptions();

            var result = new List<CadPolyline>();

            if (polylines == null || polylines.Count == 0)
                return result;

            // 过滤掉空几何和极短线。
            var validPolylines = polylines
                .Where(x => x != null)
                .Where(x => x.Geometry != null)
                .Where(x => !x.Geometry.IsEmpty)
                .Where(x => x.Geometry.NumPoints >= 2)
                .Where(x => x.Geometry.Length >= options.MinPolylineLength)
                .ToList();

            // 按属性分组。
            // WIDTH 不同的 polyline 会进入不同组，因此绝不会被合并。
            var groups = validPolylines
                .GroupBy(x => BuildMergeGroupKey(x, options))
                .ToList();

            foreach (var group in groups)
            {
                var mergedInGroup = MergeOneGroup(group.ToList(), options);
                result.AddRange(mergedInGroup);
            }

            return result;
        }

        /// <summary>
        /// 构造分组键。
        /// 
        /// 分组键由：
        /// 1. LayerName，可选；
        /// 2. MergeAttributeNames 中指定的属性；
        /// 共同组成。
        /// </summary>
        private string BuildMergeGroupKey(
            CadPolyline polyline,
            PolylineMergeOptions options)
        {
            var sb = new StringBuilder();

            if (options.RequireSameLayer)
            {
                sb.Append("LAYER=");
                sb.Append(polyline.LayerName ?? string.Empty);
                sb.Append("|");
            }

            if (options.MergeAttributeNames != null)
            {
                foreach (var attrName in options.MergeAttributeNames)
                {
                    sb.Append(attrName);
                    sb.Append("=");

                    object value = polyline.GetAttribute(attrName);
                    sb.Append(value?.ToString() ?? "<NULL>");

                    sb.Append("|");
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// 合并一个属性组内的 polyline。
        /// </summary>
        private List<CadPolyline> MergeOneGroup(
            IList<CadPolyline> groupPolylines,
            PolylineMergeOptions options)
        {
            var result = new List<CadPolyline>();

            if (groupPolylines == null || groupPolylines.Count == 0)
                return result;

            var graph = BuildGraph(groupPolylines, options);

            var visitedEdges = new HashSet<int>();

            // 1. 先从 degree != 2 的节点开始追踪。
            // 这样可以自然得到开放链。
            foreach (var node in graph.Nodes.Values)
            {
                if (node.EdgeIds.Count == 2)
                    continue;

                foreach (int edgeId in node.EdgeIds)
                {
                    if (visitedEdges.Contains(edgeId))
                        continue;

                    var chain = TraceChainFromNode(
                        graph,
                        startNodeId: node.Id,
                        startEdgeId: edgeId,
                        visitedEdges: visitedEdges);

                    if (chain.EdgeIds.Count > 0)
                    {
                        var merged = CreateMergedPolylineFromChain(
                            graph,
                            chain,
                            options);

                        if (merged != null)
                            result.Add(merged);
                    }
                }
            }

            // 2. 再处理闭环。
            // 如果一个连通分量内所有节点 degree == 2，
            // 上面的流程找不到 degree != 2 的起点。
            if (options.MergeClosedLoops)
            {
                foreach (var edge in graph.Edges.Values)
                {
                    if (visitedEdges.Contains(edge.Id))
                        continue;

                    var chain = TraceClosedLoop(
                        graph,
                        startEdgeId: edge.Id,
                        visitedEdges: visitedEdges);

                    if (chain.EdgeIds.Count > 0)
                    {
                        var merged = CreateMergedPolylineFromChain(
                            graph,
                            chain,
                            options);

                        if (merged != null)
                            result.Add(merged);
                    }
                }
            }
            else
            {
                // 如果不合并闭环，则把剩余未访问边原样输出。
                foreach (var edge in graph.Edges.Values)
                {
                    if (visitedEdges.Contains(edge.Id))
                        continue;

                    visitedEdges.Add(edge.Id);
                    result.Add(CreateSinglePolyline(edge));
                }
            }

            return result;
        }

        /// <summary>
        /// 建立拓扑图。
        /// </summary>
        private PolylineGraph BuildGraph(
            IList<CadPolyline> polylines,
            PolylineMergeOptions options)
        {
            var graph = new PolylineGraph();

            // 用网格哈希加速端点归并。
            // 避免每个端点都和已有所有节点做 O(n) 比较。
            var nodeIndex = new Dictionary<string, List<Models.GraphNode>>();

            int edgeId = 0;

            foreach (var polyline in polylines)
            {
                var coords = polyline.Geometry.Coordinates;

                if (coords == null || coords.Length < 2)
                    continue;

                Coordinate start = coords[0];
                Coordinate end = coords[coords.Length - 1];

                Models.GraphNode startNode = GetOrCreateNode(
                    graph,
                    nodeIndex,
                    start,
                    options.NodeTolerance);

                Models.GraphNode endNode = GetOrCreateNode(
                    graph,
                    nodeIndex,
                    end,
                    options.NodeTolerance);

                var edge = new GraphEdge
                {
                    Id = edgeId++,
                    StartNodeId = startNode.Id,
                    EndNodeId = endNode.Id,
                    Polyline = polyline
                };

                graph.Edges.Add(edge.Id, edge);

                startNode.EdgeIds.Add(edge.Id);
                endNode.EdgeIds.Add(edge.Id);
            }

            return graph;
        }

        /// <summary>
        /// 根据坐标获取已有节点，或者创建新节点。
        /// 
        /// 这里使用简单网格索引：
        /// 将坐标按 tolerance 网格化，
        /// 只搜索当前格子和周围 8 个格子中的节点。
        /// 
        /// 这样性能比全量搜索高很多。
        /// </summary>
        private Models.GraphNode GetOrCreateNode(
            PolylineGraph graph,
            Dictionary<string, List<Models.GraphNode>> nodeIndex,
            Coordinate coordinate,
            double tolerance)
        {
            if (tolerance <= 0)
                tolerance = 1e-9;

            int gx = (int)Math.Floor(coordinate.X / tolerance);
            int gy = (int)Math.Floor(coordinate.Y / tolerance);

            Models.GraphNode nearest = null;
            double nearestDistance = double.MaxValue;

            // 搜索周围 3x3 网格。
            for (int ix = gx - 1; ix <= gx + 1; ix++)
            {
                for (int iy = gy - 1; iy <= gy + 1; iy++)
                {
                    string key = BuildGridKey(ix, iy);

                    if (!nodeIndex.TryGetValue(key, out var nodes))
                        continue;

                    foreach (var node in nodes)
                    {
                        double dist = node.Coordinate.Distance(coordinate);

                        if (dist <= tolerance && dist < nearestDistance)
                        {
                            nearest = node;
                            nearestDistance = dist;
                        }
                    }
                }
            }

            if (nearest != null)
                return nearest;

            var newNode = new Models.GraphNode
            {
                Id = graph.Nodes.Count,
                Coordinate = new Coordinate(coordinate.X, coordinate.Y)
            };

            graph.Nodes.Add(newNode.Id, newNode);

            string currentKey = BuildGridKey(gx, gy);

            if (!nodeIndex.TryGetValue(currentKey, out var list))
            {
                list = new List<Models.GraphNode>();
                nodeIndex[currentKey] = list;
            }

            list.Add(newNode);

            return newNode;
        }

        private string BuildGridKey(int gx, int gy)
        {
            return gx + "," + gy;
        }

        /// <summary>
        /// 从指定节点和指定边开始，追踪一条开放链。
        /// 
        /// 追踪规则：
        /// 1. 当前边加入链；
        /// 2. 走到边的另一端节点；
        /// 3. 如果另一端节点 degree == 2，则继续找下一条未访问边；
        /// 4. 如果另一端节点 degree != 2，则停止。
        /// </summary>
        private GraphChain TraceChainFromNode(
            PolylineGraph graph,
            int startNodeId,
            int startEdgeId,
            HashSet<int> visitedEdges)
        {
            var chain = new GraphChain
            {
                StartNodeId = startNodeId
            };

            int currentNodeId = startNodeId;
            int currentEdgeId = startEdgeId;

            while (true)
            {
                if (visitedEdges.Contains(currentEdgeId))
                    break;

                var edge = graph.Edges[currentEdgeId];

                visitedEdges.Add(currentEdgeId);

                chain.EdgeIds.Add(currentEdgeId);
                chain.EdgeStartNodeIds.Add(currentNodeId);

                int nextNodeId = edge.GetOtherNodeId(currentNodeId);
                currentNodeId = nextNodeId;

                var nextNode = graph.Nodes[currentNodeId];

                if (nextNode.EdgeIds.Count != 2)
                    break;

                int nextEdgeId = -1;

                foreach (int id in nextNode.EdgeIds)
                {
                    if (id == currentEdgeId)
                        continue;

                    if (visitedEdges.Contains(id))
                        continue;

                    nextEdgeId = id;
                    break;
                }

                if (nextEdgeId < 0)
                    break;

                currentEdgeId = nextEdgeId;
            }

            chain.EndNodeId = currentNodeId;

            return chain;
        }

        /// <summary>
        /// 追踪闭环。
        /// 
        /// 闭环中所有节点 degree == 2。
        /// 任意选一条未访问边开始，沿着 degree == 2 的节点走，
        /// 直到回到起点或无路可走。
        /// </summary>
        private GraphChain TraceClosedLoop(
            PolylineGraph graph,
            int startEdgeId,
            HashSet<int> visitedEdges)
        {
            var startEdge = graph.Edges[startEdgeId];

            var chain = new GraphChain
            {
                StartNodeId = startEdge.StartNodeId
            };

            int startNodeId = startEdge.StartNodeId;
            int currentNodeId = startNodeId;
            int currentEdgeId = startEdgeId;

            while (true)
            {
                if (visitedEdges.Contains(currentEdgeId))
                    break;

                var edge = graph.Edges[currentEdgeId];

                visitedEdges.Add(currentEdgeId);

                chain.EdgeIds.Add(currentEdgeId);
                chain.EdgeStartNodeIds.Add(currentNodeId);

                int nextNodeId = edge.GetOtherNodeId(currentNodeId);
                currentNodeId = nextNodeId;

                if (currentNodeId == startNodeId)
                    break;

                var nextNode = graph.Nodes[currentNodeId];

                bool found = false;
                int nextEdgeId = -1;

                foreach (int id in nextNode.EdgeIds)
                {
                    if (id == currentEdgeId)
                        continue;

                    if (visitedEdges.Contains(id))
                        continue;

                    nextEdgeId = id;
                    found = true;
                    break;
                }

                if (!found)
                    break;

                currentEdgeId = nextEdgeId;
            }

            chain.EndNodeId = currentNodeId;

            return chain;
        }

        /// <summary>
        /// 根据追踪得到的 chain 创建合并后的 CadPolyline。
        /// </summary>
        private CadPolyline CreateMergedPolylineFromChain(
            PolylineGraph graph,
            GraphChain chain,
            PolylineMergeOptions options)
        {
            if (chain == null || chain.EdgeIds.Count == 0)
                return null;

            var coordinates = new List<Coordinate>();
            var sourcePolylines = new List<CadPolyline>();

            for (int i = 0; i < chain.EdgeIds.Count; i++)
            {
                int edgeId = chain.EdgeIds[i];
                int edgeStartNodeId = chain.EdgeStartNodeIds[i];

                var edge = graph.Edges[edgeId];

                bool useForward = edge.StartNodeId == edgeStartNodeId;

                var edgeCoords = edge.Polyline.Geometry.Coordinates;

                if (!useForward)
                {
                    edgeCoords = edgeCoords.Reverse().ToArray();
                }

                AppendCoordinates(
                    coordinates,
                    edgeCoords,
                    options.RemoveDuplicateConsecutivePoints);

                sourcePolylines.Add(edge.Polyline);
            }

            if (coordinates.Count < 2)
                return null;

            var line = _geometryFactory.CreateLineString(coordinates.ToArray());

            var firstSource = sourcePolylines[0];

            var sourceHandles = GraphicManager.CloneHandles(
                sourcePolylines.SelectMany(x =>
                    x.SourceHandles ?? Enumerable.Empty<string>()));

            var merged = new CadPolyline
            {
                SourceHandles = sourceHandles,
                LayerName = firstSource.LayerName,
                DatasetName = firstSource.DatasetName,
                Geometry = line,
                Attributes = CloneAttributes(firstSource.Attributes),
                SourcePolylines = sourcePolylines
            };

            // 重新生成合并后 polyline 的 CadSegment。
            RebuildSegments(merged);

            return merged;
        }

        /// <summary>
        /// 未合并的单条边原样转换为 CadPolyline。
        /// </summary>
        private CadPolyline CreateSinglePolyline(GraphEdge edge)
        {
            var source = edge.Polyline;

            var sourceHandles = GraphicManager.CloneHandles(source.SourceHandles);

            var copied = new CadPolyline
            {
                SourceHandles = sourceHandles,
                LayerName = source.LayerName,
                DatasetName = source.DatasetName,
                Geometry = source.Geometry,
                Attributes = CloneAttributes(source.Attributes),
                SourcePolylines = new List<CadPolyline> { source }
            };

            RebuildSegments(copied);

            return copied;
        }

        /// <summary>
        /// 追加坐标，并可选择移除相邻重复点。
        /// </summary>
        private void AppendCoordinates(
            List<Coordinate> target,
            Coordinate[] source,
            bool removeDuplicateConsecutivePoints)
        {
            if (source == null || source.Length == 0)
                return;

            foreach (var c in source)
            {
                if (removeDuplicateConsecutivePoints && target.Count > 0)
                {
                    var last = target[target.Count - 1];

                    if (last.Equals2D(c))
                        continue;
                }

                target.Add(new Coordinate(c.X, c.Y));
            }
        }

        /// <summary>
        /// 复制属性字典。
        /// </summary>
        private Dictionary<string, object> CloneAttributes(
            Dictionary<string, object> attributes)
        {
            if (attributes == null)
                return new Dictionary<string, object>();

            return attributes.ToDictionary(x => x.Key, x => x.Value);
        }

        /// <summary>
        /// 根据 Geometry 重建 CadSegment。
        /// </summary>
        private void RebuildSegments(CadPolyline polyline)
        {
            polyline.Segments.Clear();

            var coords = polyline.Geometry.Coordinates;

            for (int i = 0; i < coords.Length - 1; i++)
            {
                Coordinate s = coords[i];
                Coordinate e = coords[i + 1];

                if (s.Distance(e) <= 0)
                    continue;

                var seg = new CadSegment
                {
                    Start = s,
                    End = e,
                    Geometry = _geometryFactory.CreateLineString(new[] { s, e }),
                    SourceHandles = GraphicManager.CloneHandles(polyline.SourceHandles),
                    SourceSegmentIndex = i,
                    LayerName = polyline.LayerName,
                    DatasetName = polyline.DatasetName
                };

                polyline.Segments.Add(seg);
            }
        }
    }
}
