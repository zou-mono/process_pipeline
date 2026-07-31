using Autodesk.AutoCAD.DatabaseServices;
using NetTopologySuite.Geometries;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace process_pipeline.Models
{
       public class PolylineGraph
        {
            public Dictionary<int, GraphNode> Nodes { get; } =
                new Dictionary<int, GraphNode>();

            public Dictionary<int, GraphEdge> Edges { get; } =
                new Dictionary<int, GraphEdge>();
        }

        public class GraphNode
        {
            public int Id { get; set; }

            public Coordinate Coordinate { get; set; }

            /// <summary>
            /// 与该节点相连的边 ID。
            /// EdgeIds.Count 就是 degree。
            /// </summary>
            public List<int> EdgeIds { get; } = new List<int>();
        }

        public class GraphEdge
        {
            public int Id { get; set; }

            public int StartNodeId { get; set; }

            public int EndNodeId { get; set; }

            public CadPolyline Polyline { get; set; }

            public int GetOtherNodeId(int nodeId)
            {
                if (nodeId == StartNodeId)
                    return EndNodeId;

                if (nodeId == EndNodeId)
                    return StartNodeId;

                throw new InvalidOperationException("Edge does not connect to node.");
            }
        }

        public class GraphChain
        {
            public int StartNodeId { get; set; }

            public int EndNodeId { get; set; }

            /// <summary>
            /// 链中边的顺序。
            /// </summary>
            public List<int> EdgeIds { get; } = new List<int>();

            /// <summary>
            /// 每条边在加入链时，是从哪个节点开始走的。
            /// 用于决定该边坐标是否需要反转。
            /// </summary>
            public List<int> EdgeStartNodeIds { get; } = new List<int>();
        }
}
