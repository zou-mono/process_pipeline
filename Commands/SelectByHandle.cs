using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using process_pipeline.Core;
using process_pipeline.Util;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace process_pipeline.Commands
{
    public class SelectByHandleCommands : CadBase
    {
        [CommandMethod("SelByHandle")]
        public void Execute()
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
                    SelectByHandle(objId);
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

        public void SelectByHandle(ObjectId selObj)
        {
            try
            {
                string handleStr = selObj.ToString();

                // 事务处理 + 打开实体
                using (Transaction tr = Db.TransactionManager.StartTransaction())
                {
                    Entity ent = tr.GetObject(selObj, OpenMode.ForRead) as Entity;
                    if (ent == null)
                    {
                        Ed.WriteMessage($"\n错误：句柄 [{handleStr}] 对应的不是可显示的实体！");
                        return;
                    }

                    ent.Highlight();
                    // 5. 设置为当前选中（高亮）
                    Ed.SetImpliedSelection(new ObjectId[] { selObj });

                    // 6. 跳转到实体范围（缩放）
                    Extents3d extents = ent.GeometricExtents;
                    Ed.ZoomToExtents(extents);

                    tr.Commit();

                    //Ed.WriteMessage($"\n成功：已选中句柄为 [{handleStr}] 的要素，并跳转到其范围！");
                }
            }
            catch (System.Exception ex)
            {
                Ed.WriteMessage($"\n操作失败：{ex.Message}");
            }
        }
    }
}
