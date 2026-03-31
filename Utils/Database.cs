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

    public class PaletteRefreshManager
    {
        // 【核心升级】用来存储发生变化的特定对象的 ID
        // 使用 HashSet 是因为它能自动去重（同一个对象被修改多次，只记录一次）
        private static HashSet<ObjectId> _changedObjectIds = new HashSet<ObjectId>();
        private AcadDb.Database _currentDb;

        // 当你的 PaletteSet 打开时，调用这个方法开始监听
        public void StartListening(Document doc)
        {
            _currentDb = doc.Database;
        
            // 1. 监听底层数据库的增、删、改
            _currentDb.ObjectAppended += Db_ObjectChanged;
            _currentDb.ObjectModified += Db_ObjectChanged;
            _currentDb.ObjectErased += Db_ObjectErased;

            // 2. 监听命令结束（用于统一结算）
            doc.CommandEnded += Doc_CommandEnded;
        }

        // 当 PaletteSet 关闭时，务必注销事件，防止内存泄漏
        public void StopListening(Document doc)
        {
            if (_currentDb != null)
            {
                _currentDb.ObjectAppended -= Db_ObjectChanged;
                _currentDb.ObjectModified -= Db_ObjectChanged;
                _currentDb.ObjectErased -= Db_ObjectErased;
            }
            doc.CommandEnded -= Doc_CommandEnded;
        }

        // 提取出来的公共方法
        public static void TriggerPaletteRefreshIfNeeded()
        {
            if (_changedObjectIds.Count > 0)
            {
                // 【关键技巧】：先减后加，防止多次点击导致重复订阅 Idle 事件
                Application.Idle -= SafeRefreshPalette_Idle; 
                Application.Idle += SafeRefreshPalette_Idle;
            }
        }

        // 数据库对象发生变化时的回调（极速执行）
        private void Db_ObjectChanged(object sender, ObjectEventArgs e)
        {
            RecordIfTargetObject(e.DBObject);
        }

        // 2. 处理 删除 和 撤销删除 (ObjectErasedEventArgs)
        private void Db_ObjectErased(object sender, ObjectErasedEventArgs e)
        {
            // 提示：e.Erased 为 true 表示对象被删除，为 false 表示对象被 UNDO 恢复
            RecordIfTargetObject(e.DBObject);
        }

        // 3. 核心的极速判断逻辑（抽离出来复用）
        private void RecordIfTargetObject(DBObject obj)
        {
            if (obj == null) return;

            // 将 DBObject 安全转换为 Entity（所有有图层的图形对象都是 Entity）
            Entity ent = obj as Entity;
            if (ent == null) return; 

            // 极速判断：只看类型
            if (ent is Polyline || ent is Line || ent is BlockReference)
            {
                try
                {
                    // 极速判断：只看图层
                    if (CadConfig.PipeLayers.Contains(ent.Layer))
                    {
                        // 记录发生变化的 ObjectId
                        _changedObjectIds.Add(obj.ObjectId);
                    }
                    else if (CadConfig.ArrowLayers.Contains(ent.Layer) && (ent is BlockReference)) 
                    {
                        Extents3d ext = ent.GeometricExtents;
                        Point3d min = ext.MinPoint;
                        Point3d max = ext.MaxPoint;
                        double MaxBufferDistance = CadConfig.MaxBufferDistance * 1.5;

                        Point3d p = Geometry.RepresentativePoint(ent);   // 箭头的中心点

                        using (OpenCloseTransaction tr = _currentDb.TransactionManager.StartOpenCloseTransaction())
                        {
                            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(_currentDb.CurrentSpaceId, OpenMode.ForRead);

                            foreach (ObjectId id in btr)
                            {
                                string dxfName = id.ObjectClass.DxfName;
                                if (dxfName == "LINE" || dxfName == "LWPOLYLINE" || dxfName == "POLYLINE")
                                {
                                    Entity _ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                                    if (_ent != null && CadConfig.PipeLayers.Contains(_ent.Layer))
                                    {
                                        // AABB 过滤候选管道
                                        if (p.X >= min.X - MaxBufferDistance && p.X <= max.X + MaxBufferDistance &&
                                            p.Y >= min.Y - MaxBufferDistance && p.Y <= max.Y + MaxBufferDistance)
                                        {
                                            _changedObjectIds.Add(obj.ObjectId);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // 对象被深度擦除时读取 Layer 可能会报错，直接忽略即可
                }
            }
        }

        // 命令结束时的统一结算
        private void Doc_CommandEnded(object sender, CommandEventArgs e)
        {
            if (_changedObjectIds.Count > 0)
            {
                Application.Idle += SafeRefreshPalette_Idle;
            }
        }

        private static void SafeRefreshPalette_Idle(object sender, EventArgs e)
        {
            Application.Idle -= SafeRefreshPalette_Idle;
        
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            // 1. 把收集到的 ID 拿出来，并立刻清空集合，迎接下一次命令
            List<ObjectId> idsToProcess = _changedObjectIds.ToList();
            _changedObjectIds.Clear();

            // 在这里执行你耗时的 List<ProblemItem> 重新计算
            // 并更新 DataGridView
            RefreshMyDataGridView(doc, idsToProcess);
        }

        private static void RefreshMyDataGridView(Document doc, List<ObjectId> idsToProcess)
        {
            // 你的具体刷新代码...
            if (palCheckArrow.Instance.IsVisible) { 
                var service = new FlowArrowService(doc.Database, doc.Editor, useEditor: false);
                service.Run(Properties.Settings.Default.taskFlowArrow, true, idsToProcess);
            }
        }
    }
}
