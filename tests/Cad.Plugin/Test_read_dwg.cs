using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.DatabaseServices.Filters;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Cad.Plugin;
using Newtonsoft.Json;
using process_pipeline.Core;
using process_pipeline.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using NLog;

namespace process_pipeline.Tests.Cad.Plugin
{
    public class Test_read_dwg: CadCommandBase
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        [CommandMethod("test_read_dwg")]
        public override void Execute()
        {
            string file_path = @"D:\资料\部门内部文件\小工具\管线CAD\提资\勘测管.dwg";
            var doc = Application.DocumentManager.MdiActiveDocument;
            Database sideDb = new Database(false, true);

            var ed = doc.Editor;
            sideDb.ReadDwgFile(
                file_path,
                FileOpenMode.OpenForReadAndAllShare,
                allowCPConversion: true,
                password: "");
            sideDb.CloseInput(true);

            var result = new LayerStatistics();

            using (var tr = sideDb.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(sideDb.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null)
                        continue;

                    result.TotalCount++;

                    string layerName = string.IsNullOrWhiteSpace(ent.Layer) ? "0" : ent.Layer.Trim();

                    if (!result.LayerCountMap.ContainsKey(layerName))
                        result.LayerCountMap[layerName] = 0;

                    result.LayerCountMap[layerName]++;
                }

                ed.WriteMessage($"实体的数量: {result.TotalCount}\n");
                Logger.Info($"实体的数量: {result.TotalCount}");

                // 以只读方式打开包含Group的DWG文件
                sideDb.CloseInput(true); // 释放文件锁

                // 1. 获取组字典 (GroupDictionary)
                DBDictionary groupDict = tr.GetObject(sideDb.GroupDictionaryId, OpenMode.ForRead) as DBDictionary;

                int groupCount = groupDict.Count;
                ed.WriteMessage($"共有 {groupCount} 个 Group 待处理。\n");
                Logger.Info($"共有 {groupCount} 个 Group 待处理。");

                // 用于存储结果：每个Group对应一个 (块参照ID, 多段线ID)
                List<Tuple<ObjectId, ObjectId>> groupPairs = new List<Tuple<ObjectId, ObjectId>>();

                // 2. 遍历组字典中的所有Group
                int index = 0;
                foreach (DBDictionaryEntry entry in groupDict)
                {
                    index++;
                    Group group = tr.GetObject(entry.Value, OpenMode.ForRead) as Group;
                    if (group == null) continue;

                    // 打印当前Group信息
                    //ed.WriteMessage($"Group [{index}/{groupCount}] 名称: {entry.Key}, 内部实体数: {group.NumEntities}\n");
                    //Logger.Debug($"Group [{index}/{groupCount}] 名称: {entry.Key}, 内部实体数: {group.NumEntities}");

                    // 获取该Group包含的所有实体的ID集合
                    ObjectId[] entityIds = group.GetAllEntityIds();

                    ObjectId blockRefId = ObjectId.Null;
                    ObjectId polylineId = ObjectId.Null;

                    // 3. 遍历组内的实体，按类型分类
                    foreach (ObjectId id in entityIds)
                    {
                        // 注意：这里只打开读，不修改
                        Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;

                        // 判断是否为块参照
                        if (ent is BlockReference)
                        {
                            blockRefId = id;
                            
                            BlockReference br = (BlockReference)ent;
                            BlockTableRecord btr =
                                tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;

                            foreach (ObjectId bid in btr)
                            {
                                AttributeDefinition attDef =
                                    tr.GetObject(bid, OpenMode.ForRead) as AttributeDefinition;

                                if (attDef == null)
                                    continue;

                                if (attDef.Constant)
                                {
                                    Logger.Debug($"Group [{index}/{groupCount}] " +
                                        $"名称: {entry.Key}, 内部实体数: {group.NumEntities}, " +
                                        $"常量属性定义 Tag: {attDef.Tag}, TextString: {attDef.TextString}");
                                }
                                else
                                {
                                    Logger.Debug($"Group [{index}/{groupCount}] " +
                                        $"名称: {entry.Key}, 内部实体数: {group.NumEntities}, " +
                                        $"属性定义 Tag: {attDef.Tag}, Default: {attDef.TextString}, Prompt: {attDef.Prompt}"
                                    );
                                }
                            }
                        }
                        // 判断是否为多段线（包含轻量多段线、二维/三维多段线）
                        else if (ent is Polyline || ent is Polyline2d || ent is Polyline3d)
                        {
                            polylineId = id;
                        }
                    }

                    // 4. 如果该组正好包含一个块参照和一个多段线，则存入列表
                    if (!blockRefId.IsNull && !polylineId.IsNull)
                    {
                        groupPairs.Add(new Tuple<ObjectId, ObjectId>(blockRefId, polylineId));

                        // 👇 如果你需要做几何运算，强烈建议在此处提取几何数据，
                        //    而不是把ObjectId传出去（因为侧数据库关闭后ID就失效了）
                        // 示例：获取块参照的位置
                        BlockReference br = tr.GetObject(blockRefId, OpenMode.ForRead) as BlockReference;
                        Point3d position = br.Position;

                        // 获取多段线的顶点集
                        Polyline pl = tr.GetObject(polylineId, OpenMode.ForRead) as Polyline;
                        // 注意：如果是Polyline2d或3d，需要做类型转换处理
                    }
                }

                tr.Commit();
            }
        }
    }

    public class LayerStatistics
    {
        public int TotalCount { get; set; }

        public Dictionary<string, int> LayerCountMap { get; set; } = new Dictionary<string, int>();
    }
}
