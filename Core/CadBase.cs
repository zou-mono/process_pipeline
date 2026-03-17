using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;

namespace process_pipeline.Core
{
    /// <summary>
    /// CAD基础类：封装通用的Document/Editor/Database，所有命令类继承
    /// </summary>
    public class CadBase
    {
        // 通用CAD上下文（所有命令都需要）
        protected Document Doc { get; private set; }
        protected Editor Ed { get; private set; }
        protected Database Db { get; private set; }

        public CadBase()
        {
            // 初始化CAD上下文（避免每个命令重复写这几行）
            Doc = Application.DocumentManager.MdiActiveDocument;
            Ed = Doc.Editor;
            Db = Doc.Database;
        }

        /// <summary>
        /// 简化的命令行输出（封装WriteMessage，统一格式）
        /// </summary>
        protected void WriteLog(string message)
        {
            Ed.WriteMessage($"\n[管线处理工具] {message}");
        }
    }
}
