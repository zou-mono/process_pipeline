using System.IO;
using Newtonsoft.Json;
using Autodesk.AutoCAD.Runtime;

namespace Cad.Plugin
{
    public class DwgStatisticsCommand
    {
        [CommandMethod("RUN_DWG_STATS")]
        public void RunDwgStats()
        {
            // 改成你自己的 DWG 路径
            string dwgPath = @"D:\资料\部门内部文件\小工具\管线CAD\盐田初始.dwg";

            // 输出给测试项目读取
            string outputPath = @"D:\temp\DwgStats.json";

            var result = DwgStatisticsService.ReadAndCountByLayer(dwgPath);
            var json = JsonConvert.SerializeObject(result, Formatting.Indented);

            File.WriteAllText(outputPath, json);
        }
    }
}
