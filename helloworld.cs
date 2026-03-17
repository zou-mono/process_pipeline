using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Autodesk.AutoCAD.ApplicationServices; // CAD应用程序核心
using Autodesk.AutoCAD.DatabaseServices;   // CAD数据库操作
using Autodesk.AutoCAD.EditorInput;       // 命令行交互
using Autodesk.AutoCAD.Geometry;          // 几何对象（点、线）
using Autodesk.AutoCAD.Runtime;           // 命令特性（关键）

namespace process_pipeline
{
    // 必须添加 [CommandClass] 特性，CAD才能识别命令类
    public class helloworldCommands
    {
        [CommandMethod("HELLOWORLD")]
        public void helloworld() { 
             // 1. 获取当前CAD文档和命令行编辑器
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;

            // 2. 命令行输出文字（最小验证）
            ed.WriteMessage("\n=== 你好，AutoCAD 2019！这是Hello World ===");

            //// 3. 画一条简单直线（可选，验证对象创建）
            //using (Transaction tr = doc.TransactionManager.StartTransaction())
            //{
            //    // 打开模型空间
            //    BlockTable bt = tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead) as BlockTable;
            //    BlockTableRecord ms = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord;

            //    // 创建直线（起点(0,0,0)，终点(100,100,0)）
            //    Line line = new Line(new Point3d(0, 0, 0), new Point3d(100, 100, 0));
            //    ms.AppendEntity(line); // 添加到模型空间
            //    tr.AddNewlyCreatedDBObject(line, true); // 注册到事务

            //    // 提交事务（关键，否则图形不显示）
            //    tr.Commit();

            //    // 命令行提示
            //    ed.WriteMessage("\n✅ 已在模型空间创建一条直线（0,0→100,100）！");
            //}
        }
    }
}
