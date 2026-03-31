using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Threading; // 引入 CancellationToken
using Autodesk.AutoCAD.ApplicationServices; // CAD应用程序核心
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.DatabaseServices;   // CAD数据库操作
using Autodesk.AutoCAD.EditorInput;       // 命令行交互
using Autodesk.AutoCAD.Geometry;          // 几何对象（点、线）
using Autodesk.AutoCAD.Runtime;
using process_pipeline.Core;           // 命令特性（关键）
using process_pipeline.Forms;
using process_pipeline.Utils;
using AcadDb = Autodesk.AutoCAD.DatabaseServices;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.GraphicsSystem; // 必须引入这个命名空间

namespace process_pipeline.Commands
{
    /// <summary>
    /// 管线相关命令（一个类封装所有管线业务命令）
    /// </summary>
    public class FlowArrowCommands : CadCommandBase // 继承Core层的基础类，复用上下文
    {
        [CommandMethod("MATCHARROW", CommandFlags.Modal | CommandFlags.NoUndoMarker)]
        public override void Execute()
        {
            if (Doc == null) return;

            //var targetLayers = new HashSet<string>(Properties.Settings.Default.PipeLayerName.Split(','));
            //var targetLayers = new HashSet<string>
            //{
            //    "3-污水管-2025新建", "3-污水管-规划扩建", "3-污水管-现状",
            //    "3-污水压力管-规划新建", "3-污水压力管-现状"
            //};

            // 1. 直接 new 服务，填参数
            var service = new FlowArrowService(Doc.Database, Doc.Editor, false)
            {
                MaxBufferDistance = 50.0,
                ArrowLayerName = CadConfig.ArrowLayers,
                AngleTolerance = 30.0,
                PipeLayerName = CadConfig.PipeLayers
            };

            // 2. 启动引擎！
            service.Run(Properties.Settings.Default.taskFlowArrow, false);
        }
    }

    public class FlowArrowService : CadBase<Dictionary<ObjectId, ProblemItem>>
    {
            // 业务配置项
        public double MaxBufferDistance { get; set; } = CadConfig.MaxBufferDistance;
        public double AngleTolerance { get; set; } = CadConfig.AngleTolerance;
        public HashSet<string> ArrowLayerName { get; set; } = CadConfig.ArrowLayers;
        public HashSet<string> PipeLayerName { get; set; } = CadConfig.PipeLayers;

        //double maxBufferDistance = 50.0;   // 距离阈值
        //double angleTolerance = 30.0;      // 角度偏差阈值（度），e.g., ±45°
        //string auxLayerName = "核查辅助线";

        private readonly AcadDb.Database _db;
        private readonly Editor _ed;
        private readonly bool _useEditor = true; // true = 使用Editor 方法

        private ProgressContext _context;

        public FlowArrowService(AcadDb.Database db, Editor ed, bool useEditor = false) : base(db, ed)
        {
            _useEditor = useEditor;
            _ed = ed;
            _db = db;
        }

        public Dictionary<ObjectId,ProblemItem> RunChecker() {
            return Execute(null);
        }

        // 增加两个 Action 委托作为参数，默认值为 null，保证向下兼容
        protected override Dictionary<ObjectId, ProblemItem> Execute(ProgressContext context)
        {
            List<ObjectId> pipeIds;
            Dictionary<ObjectId, (DBObject, Point3d, double)> arrowData;

            //if (_useEditor)
            //    arrowData = SelectArrows();
            //else
            arrowData = GetArrowsFromDatabase();

            // 获取管线ID列表
            //if (_useEditor)
            //    pipeIds = SelectPipes();
            //else
            pipeIds = GetPipesFromDatabase();

            //DbgLog.Write(_ed, $"\n找到箭头: {arrowData.Count} 个，管线: {pipeIds.Count} 条");

            if (arrowData == null || pipeIds == null || arrowData.Count == 0 || pipeIds.Count == 0)
                return new Dictionary<ObjectId, ProblemItem>();

            return _RunChecker(pipeIds.ToArray(), arrowData, context);
        }

        protected override Dictionary<ObjectId, ProblemItem> Execute(ProgressContext context, List<ObjectId> pipeIds)
        { 
            Dictionary<ObjectId, (DBObject, Point3d, double)> arrowData = GetArrowsFromDatabase();

            if (arrowData == null || pipeIds == null || arrowData.Count == 0 || pipeIds.Count == 0)
                return new Dictionary<ObjectId, ProblemItem>();

            return _RunChecker(pipeIds.ToArray(), arrowData, context);
        }

        // 筛选箭头
        private Dictionary<ObjectId, (DBObject, Point3d, double)> SelectArrows()
        {
            Dictionary<ObjectId, (DBObject, Point3d, double)> arrowData = new Dictionary<ObjectId, (DBObject, Point3d, double)>();

            TypedValue[] arrowFilter = new TypedValue[]
            {
                new TypedValue((int)DxfCode.Operator, "<AND"),
                    new TypedValue((int)DxfCode.Start, "INSERT"),
                    new TypedValue((int)DxfCode.Operator, "<OR"),
                        new TypedValue((int)DxfCode.LayerName, "污水-流向"),
                        new TypedValue((int)DxfCode.LayerName, "3-标注-流向"),
                    new TypedValue((int)DxfCode.Operator, "OR>"),
                new TypedValue((int)DxfCode.Operator, "AND>")
                //new TypedValue((int)DxfCode.BlockName, "jt-fy")
            };
            SelectionFilter arrowSelFilter = new SelectionFilter(arrowFilter);
            PromptSelectionResult arrowRes = _ed.SelectAll(arrowSelFilter);

            if (arrowRes.Status != PromptStatus.OK)
            {
                //_ed.WriteMessage("\n没有找到箭头块！");
                return null;
            }

            ObjectId[] arrowIds = arrowRes.Value.GetObjectIds();

            // 3. 收集所有箭头的位置（ObjectId → Point3d）
            //using (Transaction tr = _db.TransactionManager.StartTransaction())    
            using (OpenCloseTransaction tr = _db.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (ObjectId id in arrowIds)
                {
                    //BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    //string _handle = id.Handle.ToString();
                    DBObject obj = tr.GetObject(id, OpenMode.ForRead);

                    if (br != null)
                    {
                        //double rotDeg = (br.Rotation * (180.0 / Math.PI) + 180.0) % 360.0;  //默认正西是起始，弧度转度，模360
                        double rotDeg = Geometry.ArrowAngle(br);
                        arrowData[id] = (obj, br.Position, rotDeg);
                    }
                }
                //tr.Abort();
            }

            return arrowData.Count > 0 ? arrowData : null;
        }

        // 筛选管道 （正确使用 <AND> + 两个 <OR>）
        private List<ObjectId> SelectPipes()
        {
            TypedValue[] pipeFilter = new TypedValue[]
            {
                new TypedValue((int)DxfCode.Operator, "<AND"),
                    new TypedValue((int)DxfCode.Operator, "<OR"),
                        new TypedValue((int)DxfCode.Start, "LINE"),
                        new TypedValue((int)DxfCode.Start, "LWPOLYLINE"),
                        new TypedValue((int)DxfCode.Start, "POLYLINE"),
                    new TypedValue((int)DxfCode.Operator, "OR>"),
                    new TypedValue((int)DxfCode.Operator, "<OR"),
                        new TypedValue((int)DxfCode.LayerName, "3-污水管-2025新建"),
                        new TypedValue((int)DxfCode.LayerName, "3-污水管-规划扩建"),
                        new TypedValue((int)DxfCode.LayerName, "3-污水管-现状"),
                        new TypedValue((int)DxfCode.LayerName, "3-污水压力管-规划新建"),
                        new TypedValue((int)DxfCode.LayerName, "3-污水压力管-现状"),
                    new TypedValue((int)DxfCode.Operator, "OR>"),
                new TypedValue((int)DxfCode.Operator, "AND>")
            };
            SelectionFilter pipeSelFilter = new SelectionFilter(pipeFilter);
            PromptSelectionResult pipeRes = _ed.SelectAll(pipeSelFilter);

            if (pipeRes.Status != PromptStatus.OK)
            {
                //_ed.WriteMessage("\n没有找到符合条件的管线！");
                return null;
            }

            return pipeRes.Value.GetObjectIds().ToList(); ;
        }

        // ========== 数据库遍历方法（安全模式，无 Editor）==========
        private Dictionary<ObjectId, (DBObject, Point3d, double)> GetArrowsFromDatabase()
        {
            var arrowData = new Dictionary<ObjectId, (DBObject, Point3d, double)>();

            //using (Transaction tr = _db.TransactionManager.StartTransaction())
            using (OpenCloseTransaction tr = _db.TransactionManager.StartOpenCloseTransaction())
            {
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(_db.CurrentSpaceId, OpenMode.ForRead);

                foreach (ObjectId id in btr)
                {
                    if (id.ObjectClass.DxfName == "INSERT")
                    {
                        //string _handle = id.Handle.ToString();

                        DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                        Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;

                        if (ent != null && CadConfig.ArrowLayers.Contains(ent.Layer))
                        {
                            double rotDeg = Geometry.ArrowAngle(br);
                            arrowData[id] = (obj, br.Position, rotDeg);
                        }
                    }
                }

                //tr.Abort();
            }

            return arrowData.Count > 0 ? arrowData : null;
        }

        private List<ObjectId> GetPipesFromDatabase()
        {
            var pipeIds = new List<ObjectId>();

            //using (Transaction tr = _db.TransactionManager.StartTransaction())
            using (OpenCloseTransaction tr = _db.TransactionManager.StartOpenCloseTransaction())
            {
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(_db.CurrentSpaceId, OpenMode.ForRead);

                foreach (ObjectId id in btr)
                {
                    string dxfName = id.ObjectClass.DxfName;
                    if (dxfName == "LINE" || dxfName == "LWPOLYLINE" || dxfName == "POLYLINE")
                    {
                        Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent != null && PipeLayerName.Contains(ent.Layer))
                        {
                            pipeIds.Add(id);
                        }
                    }
                }

                //tr.Abort();
            }

            if (pipeIds.Count == 0)
            {
                //_ed.WriteMessage("\n没有找到符合条件的管线！");
                return null;
            }

            return pipeIds;
        }

        private Dictionary<ObjectId, ProblemItem> _RunChecker(ObjectId[] pipeObjs, 
            Dictionary<ObjectId, (DBObject, Point3d Position, double Rotation)> arrowData,
            ProgressContext context)
        {
            //var problems = new List<ProblemItem>();
            Dictionary<ObjectId, ProblemItem> problems = palCheckArrow.Instance.CurrentProblems
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            // 清空辅助线图层
            //ClearLayerCommands cl = new ClearLayerCommands();
            //cl.ClearLayer(auxLayerName);

            // 确保图层存在
            Utils.Database database = new Utils.Database();
            //ObjectId auxlayerID = database.EnsureAuxLayer(auxLayerName);  // 辅助线图层ID

            // 4. 关联处理（遍历管线，找最近匹配箭头）
            //using (Transaction tr = _db.TransactionManager.StartTransaction())
            
            // 触发回调 1：告诉外部总共有多少条管线需要处理
            context?.SetTotal(pipeObjs.Length);

            using (OpenCloseTransaction tr = Db.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (ObjectId pipeId in pipeObjs)
                {
                    DBObject obj = tr.GetObject(pipeId, OpenMode.ForRead);
                    string pipe_handle = obj.Handle.ToString(); // 如 "7B2A"
            
                    // 触发回调 2：告诉外部当前处理完了一个，进度条可以动了
                    Thread.Sleep(20);
                    context?.Step();

                    // 检查是否被用户按 ESC 取消（仅在有 UI 时生效）
                    if (context != null && context.Token.IsCancellationRequested)
                    {
                        break; 
                    }

                    if (pipeId.IsErased || !pipeId.IsValid) continue;

                    if (pipe_handle == "5107")
                    {
                        DbgLog.Write(_ed, $"\n管线 {pipe_handle}正在调试");
                    }

                    Entity pipe_ent = tr.GetObject(pipeId, OpenMode.ForRead) as Entity;

                    if (pipe_ent == null) continue;

                    Checker(problems, pipeId, pipe_ent, arrowData);
                }

                return problems;
            }
        }

        public void Checker(Dictionary<ObjectId, ProblemItem> problems, ObjectId pipeId, Entity pipe_ent, 
            Dictionary<ObjectId, (DBObject, Point3d Position, double Rotation)> arrowData) 
        { 
            //string pipe_handle = obj.Handle.ToString(); // 如 "7B2A"

            Extents3d ext = pipe_ent.GeometricExtents;
            Point3d min = ext.MinPoint;
            Point3d max = ext.MaxPoint;

            // AABB 过滤候选箭头
            List<ObjectId> candidates = new List<ObjectId>();
            foreach (var kv in arrowData)
            {
                Point3d p = kv.Value.Position;
                if (p.X >= min.X - MaxBufferDistance && p.X <= max.X + MaxBufferDistance &&
                    p.Y >= min.Y - MaxBufferDistance && p.Y <= max.Y + MaxBufferDistance)
                {
                    candidates.Add(kv.Key);
                }
            }

            if (candidates.Count == 0)
            {
                problems[pipeId] = new ProblemItem
                {
                    PipeId = pipeId,
                    Type = ProblemType.NoAdjacentItems,
                    Level = ProblemLevel.Error,
                    IsFixed = false,
                    Location = Geometry.RepresentativePoint(pipe_ent),
                    Description = "无匹配箭头（管线附近无任何箭头）"
                };
                return;
            }

            double minDist = double.MaxValue;
            //double pipeDir = 0.0;
            double pipeSegAngle = double.MaxValue;
            double dist = double.MaxValue;
            double arrowRealAngle = double.MaxValue;
            //double angleDiff = double.MaxValue;
            //double realDiff = double.MaxValue;
            Point3d ap;
            Point3d closePoint;
            DBObject aobj;

            // 收集所有“可能匹配”的箭头（方向在阈值内或接近180°）
            //var possibleMatches = new List<(ObjectId Id, double Dist, Point3d closePoint, 
            //    double SegAngle, double ArrowRot, bool IsReverse)>();

            List<MatchItem> possibleMatches = new List<MatchItem>();
            MatchItem closestArrow = null;

            // 对每个候选计算距离
            foreach (ObjectId aid in candidates)
            {
                //pipeDir = Geometry.GetPipeDirection(ent);  // 获取管线局部方向（度）
                (aobj, ap, arrowRealAngle) = arrowData[aid];

                //obj = tr.GetObject(aid, OpenMode.ForRead);
                //string arrow_handle = obj.Handle.ToString(); // 如 "7B2A"

                // 计算到管线的最短距离（遍历段）
                (dist, pipeSegAngle, closePoint) = Geometry.GetClosestSegmentInfo(pipe_ent, ap);
                //double dist = Geometry.GetMinDistanceToPipe(ent, ap);

                //if (dist > maxBufferDistance || dist >= minDist) continue;
                if (dist > MaxBufferDistance) continue;

                double diff = Math.Abs(arrowRealAngle - pipeSegAngle);
                diff = Math.Min(diff, 360 - diff);

                bool isNearSame = diff <= AngleTolerance;   // 方向一致
                bool isNearReverse = Math.Abs(diff - 180) <= AngleTolerance;  // 方向相反

                // 如果既不接近同向也不接近反向 → 忽略，不算匹配箭头，也不放入 problem
                if (!isNearSame && !isNearReverse) continue;

                MatchItem matchItem = new MatchItem(aid, ap, dist, closePoint, pipeSegAngle, arrowRealAngle, isNearReverse);
                possibleMatches.Add(matchItem);

                if (dist < minDist)
                {
                    minDist = dist;
                    closestArrow = matchItem;
                }
            }

            // 如果没有一个可能匹配的箭头
            if (possibleMatches.Count == 0)
            {
                problems[pipeId] = new ProblemItem
                {
                    PipeId = pipeId,
                    Type = ProblemType.NoAdjacentItems,
                    Level = ProblemLevel.Error,
                    IsFixed = false,
                    Location = Geometry.RepresentativePoint(pipe_ent),
                    Description = $"无匹配箭头（在管线{MaxBufferDistance}米范围内没有找到符合方向的箭头）"
                };
                //return problems;
            }
            else
            {
                List<MatchItem> reverse_items = possibleMatches.Where(x => x.IsReverse).ToList() ?? new List<MatchItem>();
                List<MatchItem> same_items = possibleMatches.Where(x => x.IsReverse == false).ToList() ?? new List<MatchItem>();
                int reverseCount = reverse_items.Count;
                int sameCount = possibleMatches.Count - reverseCount;

                // 如果既有同向又有反向 → 冲突问题
                if (reverseCount > 0 && sameCount > 0)
                {
                    int takeCount = 2;
                    var sortedList = possibleMatches.OrderBy(x => x.Dist).ToList();
                    takeCount = Math.Min(takeCount, sortedList.Count);
                    var topItems = sortedList.Take(takeCount).ToList();
                    int _reverseCount = topItems.Count(x => x.IsReverse);       // 反向数量
                    int _forwardCount = topItems.Count(x => !x.IsReverse);     // 同向数量

                    if (_reverseCount > _forwardCount)
                    {
                        problems[pipeId] = new ProblemItem
                        {
                            PipeId = pipeId,
                            Type = ProblemType.DirectionConflict,
                            Level = ProblemLevel.Warning,
                            IsFixed = false,
                            Location = Geometry.RepresentativePoint(pipe_ent),
                            PossibleMatches = possibleMatches,
                            Description = "管线和箭头方向不一致"
                        };
                    }
                    else if (_reverseCount == _forwardCount)
                    {
                        problems[pipeId] = new ProblemItem
                        {
                            PipeId = pipeId,
                            Type = ProblemType.OneToMany,
                            Level = ProblemLevel.Error,
                            IsFixed = false,
                            Location = Geometry.RepresentativePoint(pipe_ent),
                            PossibleMatches = possibleMatches,
                            Description = $"与管线关联的多个箭头方向冲突（同向 {sameCount} 个，反向 {reverseCount} 个）"
                        };
                    }
                }
                else if (reverseCount == possibleMatches.Count && reverseCount > 0)
                {
                    if (closestArrow != null && minDist <= MaxBufferDistance)
                    {
                        if (closestArrow.IsReverse)
                        {
                            problems[pipeId] = new ProblemItem
                            {
                                PipeId = pipeId,
                                Type = ProblemType.DirectionConflict,
                                Level = ProblemLevel.Warning,
                                IsFixed = false,
                                Location = Geometry.RepresentativePoint(pipe_ent),
                                PossibleMatches = possibleMatches,
                                Description = "管线和箭头方向不一致"
                            };
                        }
                    }
                }
                else { 
                    if (problems.ContainsKey(pipeId)) 
                    { 
                        problems[pipeId].IsFixed = true;
                    }
                }
            }
        }

        protected override void OnSuccess(Dictionary<ObjectId, ProblemItem> result, bool bOnlyUpdate = false)
        {
            if (result == null || result.Count == 0)
            {
                Ed.WriteMessage("\n检查通过，无问题。\n");
                return;
            }

            if (bOnlyUpdate)
            {
                palCheckArrow.Instance.Update(result);
            }
            else { 
                // 弹窗展示
                palCheckArrow.Instance.Show(result);    
            }
        }
    }
}