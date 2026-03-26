using Autodesk.AutoCAD.EditorInput;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AcadDb = Autodesk.AutoCAD.DatabaseServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.ApplicationServices;

namespace process_pipeline.Core
{
    public interface ICadCommand
    {
        // 1. 核心：使用 => 动态获取当前文档，完美解决文档切换、关闭、新建的问题！
        Document Doc { get; }
        Editor Ed { get; }
        AcadDb.Database Db { get; }

        void Execute();
    }

    public abstract class CadCommandBase : ICadCommand
    {
        // 1. 经典写法：每次调用 Doc，都会执行 get 里面的代码，实时获取当前文档！
        public Document Doc
        {
            get 
            { 
                return AcadApp.DocumentManager.MdiActiveDocument; 
            }
        }

        // 顺手封装 Db，如果 Doc 为 null，就返回 null
        public AcadDb.Database Db
        {
            get 
            { 
                if (Doc == null) return null;
                return Doc.Database; 
            }
        }

        // 顺手封装 Ed
        public Editor Ed
        {
            get 
            { 
                if (Doc == null) return null;
                return Doc.Editor; 
            }
        }

        // 2. 强制子类重写 Execute
        public abstract void Execute();
    }
}
