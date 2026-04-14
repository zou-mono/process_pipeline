using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using process_pipeline.Core;
using process_pipeline.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace process_pipeline.Commands
{
    public class SelectByHandleCommands : CadCommandBase
    {
        [CommandMethod("SelByHandle", CommandFlags.Modal | CommandFlags.NoUndoMarker)]
        public override void Execute()
        {
            var service = new SelectByHandleService(Doc.Database, Doc.Editor);
            service.Run("根据句柄值选择实体");
        }
    }

    public class SelectByHandleService : CadBase
    {
        short _gripLimit = 100; // 默认夹点上限值

        public SelectByHandleService(Autodesk.AutoCAD.DatabaseServices.Database db, Editor ed) : base(db, ed) {
            _gripLimit = CadConfig.GripLimit;
            
            // 夹点上限值
            Application.SetSystemVariable("GRIPOBJLIMIT", _gripLimit);
        }

        protected override void ExecuteVoid(ProgressContext context, List<ObjectId> objectids) { }

        protected override void ExecuteVoid(ProgressContext context)
        {
            try
            {
                // 1. 提示用户输入句柄值
                PromptStringOptions pso = new PromptStringOptions("\n请输入要素的句柄值: ");
                pso.AllowSpaces = false; // 句柄无空格，禁止输入空格
                PromptResult pr = Ed.GetString(pso);

                if (pr.Status != PromptStatus.OK || string.IsNullOrEmpty(pr.StringResult))
                {
                    Ed.WriteMessage("\n错误：句柄值不能为空！");
                    return;
                }
                string handleStr = pr.StringResult.Trim();

                if (!long.TryParse(handleStr, NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out long handleValue) || handleValue == 0)
                {
                    Ed.WriteMessage("\n错误：输入的句柄格式无效！（必须是有效的十六进制数，不能为 0）");
                    return;
                }

                ObjectId objId = ObjectId.Null;
                try
                {  
                    Handle handle = new Handle(handleValue);
                    objId = Db.GetObjectId(false, handle, 0);
                    SelectByHandles(new[] { objId });
                }
                catch
                {
                    Ed.WriteMessage($"\n错误：未找到句柄为 [{handleStr}] 的要素！");
                    return;
                }
            }
            catch (System.Exception ex)
            {
                Ed.WriteMessage($"\n操作失败：{ex.Message}");
            }
        }

        public void SelectByHandles(IEnumerable<ObjectId> selObjs, bool bZoomToExtent=true)
        {
            if (selObjs == null || !selObjs.Any()) return;

            try
            {
                // 转成数组，便于处理
                var objectIds = selObjs.Where(id => !id.IsNull && !id.IsErased).ToArray();
                //using (var docLock = Doc.LockDocument())
                // 事务处理 + 打开实体
                Extents3d totalExtents = new Extents3d();
                bool hasValidEntity = false;

                using (Transaction tr = Db.TransactionManager.StartTransaction())
                //using (OpenCloseTransaction tr = Db.TransactionManager.StartOpenCloseTransaction())
                {
                    // 1. 高亮所有实体 + 计算联合包围盒            
                    foreach (var oid in objectIds)
                    {
                        if (oid.IsNull || oid.IsErased) continue;

                        Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;

                        ent.Highlight();  // 高亮当前实体

                        if (bZoomToExtent) 
                        { 
                            totalExtents.AddExtents(ent.GeometricExtents);
                        }
                            
                        hasValidEntity = true;    
                    }
                    //tr.Commit();
                    //Ed.WriteMessage($"\n成功：已选中句柄为 [{handleStr}] 的要素，并跳转到其范围！");
                }

                if (!hasValidEntity)
                {
                    Ed.WriteMessage("\n没有找到任何有效的实体");
                    return;
                }

                // 2. 批量设置为当前选中（夹点、高亮等）
                //Ed.SetImpliedSelection(objectIds);

                if (objectIds.Length > _gripLimit && _gripLimit > 0)
                {
                    // 3. 如果超过上限，只取前 gripLimit 个
                    ObjectId[] limitedIds = objectIds.Take(_gripLimit).ToArray();
    
                    // 执行选中
                    Ed.SetImpliedSelection(limitedIds);
    
                    // 可选：给用户一个提示，告诉他们为什么没全选
                    Ed.WriteMessage($"\n提示：选中的对象总数({objectIds.Length})超过了系统夹点限制({_gripLimit})，已为您选中前 {_gripLimit} 个。");
                }
                else
                {
                    // 4. 没超过上限，正常全选
                    Ed.SetImpliedSelection(objectIds);
                }

                if (bZoomToExtent) 
                { 
                    Ed.ZoomToExtents(totalExtents);
                }

                //Ed.Regen();
            }
            catch (System.Exception ex)
            {
                Ed.WriteMessage($"\n操作失败：{ex.Message}");
            }
        }
    }
}
