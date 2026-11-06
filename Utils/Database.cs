using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using process_pipeline.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AcadDb = Autodesk.AutoCAD.DatabaseServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.Geometry;
using process_pipeline.Forms;
using process_pipeline.Commands;
using process_pipeline.Geometry;

namespace process_pipeline.Utils
{
    public class Database 
    {
        Document Doc = AcadApp.DocumentManager.MdiActiveDocument;
        AcadDb.Database Db = AcadApp.DocumentManager.MdiActiveDocument.Database;

        public ObjectId EnsureAuxLayer(string layerName)
        {
            using (Transaction tr = Db.TransactionManager.StartTransaction())
            {
                LayerTable lt = (LayerTable)tr.GetObject(Db.LayerTableId, OpenMode.ForWrite);

                //string layerName = "核查辅助线";

                if (lt.Has(layerName))
                {
                    return lt[layerName];
                }

                // 创建新图层
                LayerTableRecord ltr = new LayerTableRecord();
                ltr.Name = layerName;
                ltr.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(ColorMethod.ByAci, 1); // 红色（可改）
                ltr.IsLocked = false;
                ltr.IsOff = false;
                ltr.IsFrozen = false;

                ObjectId layerId = lt.Add(ltr);
                tr.AddNewlyCreatedDBObject(ltr, true);

                tr.Commit();

                return lt[layerName];
            }
        }    
    }

    /// <summary>
    /// 面板增量刷新调度器（Commit 3.3）。
    /// 
    /// 架构设计说明：
    /// 1. 彻底解耦业务规则硬编码：
    ///    原有的 RecordIfTargetObject 直接硬编码了 PipeLayers、ArrowLayers、FlowArrowService.ArrowToPipes
    ///    以及 1.5 倍缓冲区的全图空间遍历逻辑。
    ///    现在改为从当前文档会话（DocumentCheckSession.ActiveRule）获取 ICheckRule 实例，
    ///    通过 rule.GetAffectedTargetIds(obj) 统一计算受影响的管线 ID 集合，符合开闭原则（OCP）。
    /// 2. 增强多文档监听安全性：
    ///    StartListening 前先执行解绑，防止重复订阅导致单次图元修改触发多次重算。
    /// </summary>
    public class PaletteRefreshManager
    {
        // 用来存储发生变化的特定受影响目标管线 ID（HashSet 自动去重）
        private static HashSet<ObjectId> _changedObjectIds = new HashSet<ObjectId>();
        private AcadDb.Database _currentDb;
        private ICheckRule _activeRule;

        // 当 PaletteSet 打开或切换文档时，调用此方法开始监听
        public void StartListening(Document doc)
        {
            if (doc == null) return;

            StopListening(doc);

            _currentDb = doc.Database;
            var session = DocumentSessionManager.GetOrCreateSession(_currentDb);
            _activeRule = session?.ActiveRule ?? new FlowArrowService(_currentDb, doc.Editor, false);
            if (session != null && session.ActiveRule == null)
            {
                session.ActiveRule = _activeRule;
            }

            _changedObjectIds = new HashSet<ObjectId>();
        
            // 1. 监听底层数据库的增、删、改
            _currentDb.ObjectAppended += Db_ObjectChanged;
            _currentDb.ObjectModified += Db_ObjectChanged;
            _currentDb.ObjectErased += Db_ObjectErased;

            // 2. 监听命令结束（用于统一结算）
            doc.CommandEnded += Doc_CommandEnded;
        }

        // 当 PaletteSet 关闭或切换文档时，注销事件，防止内存泄漏
        public void StopListening(Document doc)
        {
            if (_currentDb != null)
            {
                _currentDb.ObjectAppended -= Db_ObjectChanged;
                _currentDb.ObjectModified -= Db_ObjectChanged;
                _currentDb.ObjectErased -= Db_ObjectErased;
            }
            if (doc != null)
            {
                doc.CommandEnded -= Doc_CommandEnded;
            }
            _changedObjectIds?.Clear();
        }

        // 供人工修复操作（如翻转管线）主动触发增量刷新
        public static void TriggerPaletteRefreshIfNeeded()
        {
            if (_changedObjectIds != null && _changedObjectIds.Count > 0)
            {
                // 先减后加，防止多次点击导致重复订阅 Idle 事件
                Application.Idle -= SafeRefreshPalette_Idle; 
                Application.Idle += SafeRefreshPalette_Idle;
            }
        }

        /// <summary>
        /// 供面板内部已自行管理状态快照的事务操作（如 ReverseCurveOperation 执行与撤销）调用，
        /// 从待刷新队列中移除这些 ObjectId，防止后台 Idle 增量重算覆盖 Undo/Redo 恢复的精确快照。
        /// </summary>
        public static void SuppressIds(IEnumerable<ObjectId> ids)
        {
            if (_changedObjectIds == null || ids == null) return;
            foreach (ObjectId id in ids)
            {
                _changedObjectIds.Remove(id);
            }
            if (_changedObjectIds.Count == 0)
            {
                Application.Idle -= SafeRefreshPalette_Idle;
            }
        }

        // 数据库对象发生变化时的回调（极速执行）
        private void Db_ObjectChanged(object sender, ObjectEventArgs e)
        {
            ArrowCacheManager.UpdateArrow(e.DBObject);
            RecordIfTargetObject(e.DBObject);
        }

        // 处理 删除 和 撤销删除 (ObjectErasedEventArgs)
        private void Db_ObjectErased(object sender, ObjectErasedEventArgs e)
        {
            if (e.Erased)
            {
                ArrowCacheManager.RemoveArrow(e.DBObject.Id);
            }
            // e.Erased 为 true 表示对象被删除，为 false 表示对象被 UNDO 恢复
            RecordIfTargetObject(e.DBObject);
        }

        // 委托给当前文档会话的所有已激活 ICheckRule.GetAffectedTargetIds 计算受影响的目标管线 ID，
        // 并在监听到【上版数据】图层或 Group 发生增删改时自动标记 DatasetContext 上版数据缓存失效（Stage 2 Commit 2.3）
        private void RecordIfTargetObject(DBObject obj)
        {
            if (obj == null) return;

            _changedObjectIds = _changedObjectIds ?? new HashSet<ObjectId>();

            var session = DocumentSessionManager.GetOrCreateSession(_currentDb);
            if (session != null)
            {
                if (obj is Group)
                {
                    session.DatasetContext.InvalidatePreviousData();
                }
                else if (obj is Entity ent && !string.IsNullOrWhiteSpace(ent.Layer))
                {
                    var prevPipeLayers = CadConfig.PreviousData.PipeLayers;
                    var prevAttrLayers = CadConfig.PreviousData.AttributeLayers;
                    bool isPrevPipe = prevPipeLayers != null &&
                                      prevPipeLayers.Any(l => string.Equals(l, ent.Layer, StringComparison.OrdinalIgnoreCase));
                    bool isPrevAttr = prevAttrLayers != null &&
                                      prevAttrLayers.Any(l => string.Equals(l, ent.Layer, StringComparison.OrdinalIgnoreCase));

                    if (isPrevPipe || isPrevAttr)
                    {
                        session.DatasetContext.InvalidatePreviousData();
                    }
                }
            }

            var rulesToQuery = new List<ICheckRule>();
            if (session?.ActiveRules != null && session.ActiveRules.Count > 0)
            {
                rulesToQuery.AddRange(session.ActiveRules.Values.Where(r => r != null));
            }
            else
            {
                var fallbackRule = session?.ActiveRule ?? _activeRule;
                if (fallbackRule != null)
                {
                    rulesToQuery.Add(fallbackRule);
                }
            }

            foreach (ICheckRule rule in rulesToQuery)
            {
                var affected = rule.GetAffectedTargetIds(obj);
                if (affected == null) continue;

                foreach (ObjectId targetId in affected)
                {
                    if (!targetId.IsNull)
                    {
                        _changedObjectIds.Add(targetId);
                    }
                }
            }
        }

        // 命令结束时的统一结算
        private void Doc_CommandEnded(object sender, CommandEventArgs e)
        {
            if (_changedObjectIds != null && _changedObjectIds.Count > 0)
            {
                Application.Idle -= SafeRefreshPalette_Idle;
                Application.Idle += SafeRefreshPalette_Idle;
            }
        }

        private static void SafeRefreshPalette_Idle(object sender, EventArgs e)
        {
            Application.Idle -= SafeRefreshPalette_Idle;
        
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null || _changedObjectIds == null || _changedObjectIds.Count == 0) return;

            // 1. 把收集到的 ID 拿出来，并立刻清空集合，迎接下一次命令
            List<ObjectId> idsToProcess = _changedObjectIds.ToList();
            _changedObjectIds.Clear();

            // 2. 触发面板表格的局部增量重算与单行刷新
            GraphicManager.RefreshDataGrid(doc, idsToProcess);
        }
    }
}
