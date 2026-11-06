using System.IO;
using Newtonsoft.Json;
using Autodesk.AutoCAD.Runtime;
using process_pipeline.Core;

namespace Cad.Plugin
{
    public class Test_Dwg_Stats : CadCommandBase
    {
        [CommandMethod("test_dwg_stats")]
        public override void Execute()
        {
            string dwgPath = @"D:\Codes\Tools\process_pipeline\Data\新版数据.dwg";
            string outputPath = @"D:\temp\DwgStats.json";

            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var result = DwgStatisticsService.ReadAndCountByLayer(dwgPath);
            var json = JsonConvert.SerializeObject(result, Formatting.Indented);

            File.WriteAllText(outputPath, json);
        }
    }
}
