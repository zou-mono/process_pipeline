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
    /// 合并逻辑：
    /// 1. 只按不会改变拓扑语义的字段分组，例如 LayerName / DatasetName；
    /// 2. 每组内把 polyline 视为图的边；
    /// 3. polyline 起点和终点视为图节点；
    /// 4. 端点距离小于 NodeTolerance 的点归并成同一节点；
    /// 5. 先基于完整原始图计算节点 degree；
    /// 6. 只有原始 degree == 2 的节点才可能被穿过；
    /// 7. 在建图阶段预计算可合并连接，只有 WIDTH 等合并属性一致时才允许穿过；
    /// 8. 追踪时只沿可合并连接继续合并；
    /// 9. 保留合并前的 SourcePolylines。
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

            // 注意：这里只按不会改变网络拓扑语义的字段分组。
            // 例如 Layer / Dataset。
            // 不要按 WIDTH 分组，否则会改变节点 degree。
            var groups = validPolylines
                .GroupBy(x => BuildTopologyGroupKey(x, options))
                .ToList();

            //// 按属性分组。
            //// WIDTH 不同的 polyline 会进入不同组，因此绝不会被合并。
            //var groups = validPolylines
            //    .GroupBy(x => BuildMergeGroupKey(x, options))
            //    .ToList();

            foreach (var group in groups)
            {
                var mergedInGroup = MergeOneGroup(group.ToList(), options);
                result.AddRange(mergedInGroup);
            }

            return result;
        }

        private string BuildTopologyGroupKey(
            CadPolyline polyline,
            PolylineMergeOptions options)
        {
            var sb = new StringBuilder();

            if (polyline == null)
                return "<NULL_POLYLINE>";

            if (options == null)
                options = new PolylineMergeOptions();

            /*
             * 注意：
             * 这里不能加入 WIDTH 这类合并属性。
             * 
             * 原因：
             * WIDTH 会改变子图结构，
             * 导致原始 degree != 2 的节点在子图中变成 degree == 2，
             * 从而错误穿越分叉节点。
             */

            if (options.RequireSameLayer)
            {
                sb.Append("LAYER=");
                sb.Append(NormalizeMergeKeyValue(polyline.LayerName));
                sb.Append("|");
            }

            /*
             * 如果 DatasetName 也是网络边界，可以加入。
             * 如果不是，也可以不加。
             */
            if (!string.IsNullOrWhiteSpace(polyline.DatasetName))
            {
                sb.Append("DATASET=");
                sb.Append(NormalizeMergeKeyValue(polyline.DatasetName));
                sb.Append("|");
            }

            return sb.ToString();
        }

        private string NormalizeMergeKeyValue(object value)
        {
            if (value == null)
                return "<NULL>";

            if (value is double d)
                return d.ToString("0.########");

            if (value is float f)
                return f.ToString("0.########");

            if (value is decimal m)
                return m.ToString("0.########");

            if (value is int || value is long || value is short)
                return value.ToString();

            string text = value.ToString();

            if (string.IsNullOrWhiteSpace(text))
                return "<EMPTY>";

            text = text.Trim();

            if (double.TryParse(
                    text,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double number))
            {
                return number.ToString(
                    "0.########",
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            return text.ToUpperInvariant();
        }

        /// <summary>
        /// 构造分组键。
        /// 
        /// 分组键由：
        /// 1. LayerName，可选；
        /// 2. MergeAttributeNames 中指定的属性；
        /// 共同组成。
        /// </summary>
        //private string BuildMergeGroupKey(
        //    CadPolyline polyline,
        //    PolylineMergeOptions options)
        //{
        //    var sb = new StringBuilder();

        //    if (options.RequireSameLayer)
        //    {
        //        sb.Append("LAYER=");
        //        sb.Append(polyline.LayerName ?? string.Empty);
        //        sb.Append("|");
        //    }

        //    if (options.MergeAttributeNames != null)
        //    {
        //        foreach (var attrName in options.MergeAttributeNames)
        //        {
        //            sb.Append(attrName);
        //            sb.Append("=");

        //            object value = polyline.GetAttribute(attrName);
        //            sb.Append(value?.ToString() ?? "<NULL>");

        //            sb.Append("|");
        //        }
        //    }

        //    return sb.ToString();
        //}

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
                        visitedEdges: visitedEdges,
                        options: options);

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
                        visitedEdges: visitedEdges,
                        options: options);

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

            /*
             * 关键：
             * graph.EdgeIds 保留原始完整拓扑。
             * 在完整拓扑建好之后，再根据原始 degree 和属性规则，
             * 预先计算每个节点处哪些边允许继续合并。
             */
            BuildMergeableConnections(graph, options);
            return graph;
        }

        /// <summary>
        /// 基于原始拓扑和属性规则，预先建立“可合并连接”。
        /// 
        /// 注意：
        /// 1. 原始拓扑 degree 使用 node.EdgeIds.Count；
        /// 2. 只有原始 degree == 2 的节点才可能被穿过；
        /// 3. 如果两条边的合并属性不同，则不允许穿过；
        /// 4. 不修改 node.EdgeIds，避免破坏原始拓扑。
        /// </summary>
        private void BuildMergeableConnections(
            PolylineGraph graph,
            PolylineMergeOptions options)
        {
            if (graph == null || graph.Nodes == null || graph.Edges == null)
                return;

            foreach (var node in graph.Nodes.Values)
            {
                if (node == null)
                    continue;

                if (node.NextMergeableEdgeByEdgeId == null)
                    node.NextMergeableEdgeByEdgeId = new Dictionary<int, int>();
                else
                    node.NextMergeableEdgeByEdgeId.Clear();

                /*
                 * 关键点：
                 * 必须使用原始完整拓扑的 degree。
                 * 如果这里不是 2，就绝对不能穿过。
                 */
                if (node.EdgeIds == null || node.EdgeIds.Count != 2)
                    continue;

                int edgeIdA = node.EdgeIds[0];
                int edgeIdB = node.EdgeIds[1];

                if (!graph.Edges.TryGetValue(edgeIdA, out GraphEdge edgeA))
                    continue;

                if (!graph.Edges.TryGetValue(edgeIdB, out GraphEdge edgeB))
                    continue;

                /*
                 * degree == 2 只是拓扑上允许穿过。
                 * 合并属性一致才真正允许合并。
                 */
                if (!CanMergeEdgesByAttributes(edgeA, edgeB, options))
                    continue;

                /*
                 * 建立双向可合并连接。
                 * 表示从 A 进来可以走到 B；
                 * 从 B 进来也可以走到 A。
                 */
                node.NextMergeableEdgeByEdgeId[edgeIdA] = edgeIdB;
                node.NextMergeableEdgeByEdgeId[edgeIdB] = edgeIdA;
            }
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
        /// 注意：
        /// 是否可以穿过某个 degree == 2 节点，已经在 BuildMergeableConnections 中预计算完成。
        /// 这里不再现场判断 WIDTH 等属性。
        /// </summary>
        private GraphChain TraceChainFromNode(
            PolylineGraph graph,
            int startNodeId,
            int startEdgeId,
            HashSet<int> visitedEdges,
            PolylineMergeOptions options)
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

                var currentEdge = graph.Edges[currentEdgeId];

                visitedEdges.Add(currentEdgeId);

                chain.EdgeIds.Add(currentEdgeId);
                chain.EdgeStartNodeIds.Add(currentNodeId);

                int nextNodeId =
                    currentEdge.GetOtherNodeId(currentNodeId);

                currentNodeId = nextNodeId;

                var nextNode = graph.Nodes[currentNodeId];

                /*
                 * 核心逻辑：
                 * 能否继续合并，已经在 BuildMergeableConnections 中算好了。
                 * 
                 * 如果当前节点：
                 * 1. 原始 degree != 2；
                 * 2. 或两边 WIDTH 不一致；
                 * 3. 或其他合并属性不一致；
                 * 
                 * 都不会有 NextMergeableEdgeByEdgeId 映射。
                 */
                if (nextNode.NextMergeableEdgeByEdgeId == null ||
                    !nextNode.NextMergeableEdgeByEdgeId.TryGetValue(
                        currentEdgeId,
                        out int nextEdgeId))
                {
                    break;
                }

                if (visitedEdges.Contains(nextEdgeId))
                    break;

                currentEdgeId = nextEdgeId;
            }

            chain.EndNodeId = currentNodeId;

            return chain;
        }

        private bool CanMergeEdgesByAttributes(
            GraphEdge edgeA,
            GraphEdge edgeB,
            PolylineMergeOptions options)
        {
            if (edgeA == null || edgeB == null)
                return false;

            if (edgeA.Polyline == null || edgeB.Polyline == null)
                return false;

            if (options == null)
                return true;

            if (options.MergeAttributeNames == null ||
                options.MergeAttributeNames.Count == 0)
            {
                return true;
            }

            foreach (string attrName in options.MergeAttributeNames)
            {
                if (string.IsNullOrWhiteSpace(attrName))
                    continue;

                object valueA =
                    GetMergeAttributeValue(
                        edgeA.Polyline,
                        attrName);

                object valueB =
                    GetMergeAttributeValue(
                        edgeB.Polyline,
                        attrName);

                string textA =
                    NormalizeMergeKeyValue(valueA);

                string textB =
                    NormalizeMergeKeyValue(valueB);

                if (!string.Equals(
                        textA,
                        textB,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private object GetMergeAttributeValue(
            CadPolyline polyline,
            string attributeName)
        {
            if (polyline == null)
                return null;

            if (string.IsNullOrWhiteSpace(attributeName))
                return null;

            if (polyline.Attributes == null)
                return null;

            foreach (var kv in polyline.Attributes)
            {
                if (string.Equals(
                        kv.Key,
                        attributeName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return kv.Value;
                }
            }

            return null;
        }

        /// <summary>
        /// 追踪剩余未访问边。
        /// 
        /// 名字虽然叫 TraceClosedLoop，但现在它不仅用于完整闭环，
        /// 也用于处理第一阶段没有从 degree != 2 节点访问到的剩余边。
        /// 
        /// 是否能继续穿过节点，使用 BuildMergeableConnections 预计算的结果。
        /// </summary>
        private GraphChain TraceClosedLoop(
            PolylineGraph graph,
            int startEdgeId,
            HashSet<int> visitedEdges,
            PolylineMergeOptions options)
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

                var currentEdge = graph.Edges[currentEdgeId];

                visitedEdges.Add(currentEdgeId);

                chain.EdgeIds.Add(currentEdgeId);
                chain.EdgeStartNodeIds.Add(currentNodeId);

                int nextNodeId =
                    currentEdge.GetOtherNodeId(currentNodeId);

                currentNodeId = nextNodeId;

                /*
                 * 如果已经回到起点，说明形成了完整闭环。
                 */
                if (currentNodeId == startNodeId)
                    break;

                var nextNode = graph.Nodes[currentNodeId];

                /*
                 * 是否可以继续走，完全由建图阶段预计算结果决定。
                 */
                if (nextNode.NextMergeableEdgeByEdgeId == null ||
                    !nextNode.NextMergeableEdgeByEdgeId.TryGetValue(
                        currentEdgeId,
                        out int nextEdgeId))
                {
                    break;
                }

                if (visitedEdges.Contains(nextEdgeId))
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
