using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using process_pipeline.Core;
using process_pipeline.Utils;
using process_pipeline.Geometry;
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
            service.Run(CadConfig.TaskNames.SelectByHandle);
        }
    }

    public class SelectByHandleService : CadBase
    {
        short _gripLimit = 100; // 默认夹点上限值

        public SelectByHandleService(Autodesk.AutoCAD.DatabaseServices.Database db, Editor ed) : base(db, ed) {
            _gripLimit = CadConfig.GripLimit;
            try
            {
                object curVal = Application.GetSystemVariable("GRIPOBJLIMIT");
                if (curVal == null || Convert.ToInt32(curVal) != _gripLimit)
                {
                    Application.SetSystemVariable("GRIPOBJLIMIT", _gripLimit);
                }
            }
            catch
            {
                // 忽略系统变量读写异常
            }
        }

        protected override void ExecuteVoid(ProgressContext context, List<ObjectId> objectids) { }

        protected override void ExecuteVoid(ProgressContext context)
        {
            try
            {
                // 1. 提示用户输入句柄值
                PromptStringOptions pso = new PromptStringOptions("\n请输入要素的句柄值: ");
                pso.AllowSpaces = false; // 句柄无空格，禁止输入空格
                pso.AppendKeywordsToMessage = false; 
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

        public void SelectByHandles(
            IEnumerable<ObjectId> selObjs,
            bool bZoomToExtent = true,
            IEnumerable<ObjectId> additionalZoomObjs = null)
        {
            if (selObjs == null || !selObjs.Any()) return;

            try
            {
                // 转成数组，便于处理
                var initialIds = selObjs.Where(id => !id.IsNull && !id.IsErased).ToArray();
                var expandedIds = new List<ObjectId>(initialIds.Length * 2);
                var seenIds = new HashSet<ObjectId>();

                Extents3d totalExtents = new Extents3d();
                bool hasValidEntity = false;

                using (OpenCloseTransaction tr = Db.TransactionManager.StartOpenCloseTransaction())
                {
                    // 1. 收集目标实体及其所属可选择 CAD Group 的全部同组成员，并计算联合包围盒
                    foreach (var oid in initialIds)
                    {
                        if (oid.IsNull || oid.IsErased) continue;

                        Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;

                        if (seenIds.Add(oid))
                        {
                            expandedIds.Add(oid);
                            if (bZoomToExtent)
                            {
                                try { totalExtents.AddExtents(ent.GeometricExtents); } catch { }
                            }
                            hasValidEntity = true;
                        }

                        // 若该图元已绑定至 CAD Group（通过 PersistentReactorIds 指向 Group），则将同组其它成员（如配对的 ws 属性块或管线）一并纳入选中集合
                        ObjectIdCollection reactorIds = ent.GetPersistentReactorIds();
                        if (reactorIds != null && reactorIds.Count > 0)
                        {
                            foreach (ObjectId rId in reactorIds)
                            {
                                if (rId.IsNull || !rId.IsValid || rId.IsErased)
                                    continue;

                                if (tr.GetObject(rId, OpenMode.ForRead, false) is Group grp && !grp.IsErased && grp.Selectable)
                                {
                                    ObjectId[] memberIds = grp.GetAllEntityIds();
                                    if (memberIds == null) continue;

                                    foreach (ObjectId mid in memberIds)
                                    {
                                        if (mid.IsNull || !mid.IsValid || mid.IsErased || !seenIds.Add(mid))
                                            continue;

                                        if (tr.GetObject(mid, OpenMode.ForRead, false) is Entity memberEnt)
                                        {
                                            expandedIds.Add(mid);
                                            if (bZoomToExtent)
                                            {
                                                try { totalExtents.AddExtents(memberEnt.GeometricExtents); } catch { }
                                            }
                                            hasValidEntity = true;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // 2. 若需要缩放视口且传入了关联图元（如尚未成组的配对属性块/文字），将其几何范围一并纳入缩放视口而不强制加入预选集（避免 CAD 默认蓝色选中特效将未成组属性块染蓝）
                    if (bZoomToExtent && additionalZoomObjs != null)
                    {
                        foreach (ObjectId zId in additionalZoomObjs)
                        {
                            if (zId.IsNull || !zId.IsValid || zId.IsErased || seenIds.Contains(zId))
                                continue;

                            if (tr.GetObject(zId, OpenMode.ForRead, false) is Entity zoomEnt)
                            {
                                try { totalExtents.AddExtents(zoomEnt.GeometricExtents); } catch { }
                            }
                        }
                    }
                }

                var objectIds = expandedIds.ToArray();

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

                try { Ed.UpdateScreen(); } catch { }
            }
            catch (System.Exception ex)
            {
                Ed.WriteMessage($"\n操作失败：{ex.Message}");
            }
        }
    }
}
