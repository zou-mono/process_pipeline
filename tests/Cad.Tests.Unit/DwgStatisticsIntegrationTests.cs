using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using NUnit.Framework;
using Cad.Tests.Shared;

namespace Cad.Tests.Unit
{
    [TestFixture]
    public class DwgStatisticsIntegrationTests
    {
        private const string SampleDwgPath = @"D:\资料\部门内部文件\小工具\管线CAD\盐田初始.dwg";
        private const string OutputPath = @"D:\temp\DwgStats.json";

        [Test]
        public void ReadDwg_Should_Return_LayerStatistics()
        {
            Assert.That(File.Exists(SampleDwgPath), Is.True, $"测试 DWG 文件不存在：{SampleDwgPath}");

            // 这里假定 AutoCAD 已经执行过 RUN_DWG_STATS 命令并生成了 JSON
            Assert.That(File.Exists(OutputPath), Is.True, $"结果文件不存在：{OutputPath}");

            string json = File.ReadAllText(OutputPath);
            Assert.That(json, Is.Not.Empty, "结果文件为空。");

            var stats = JsonConvert.DeserializeObject<LayerStatistics>(json);

            Assert.That(stats, Is.Not.Null, "反序列化失败。");
            Assert.That(stats.TotalCount, Is.GreaterThan(0), "DWG 中应至少包含一个图元。");
            Assert.That(stats.LayerCountMap, Is.Not.Null, "图层统计不能为空。");
            Assert.That(stats.LayerCountMap.Count, Is.GreaterThan(0), "DWG 中应至少包含一个图层。");
        }
    }
}
