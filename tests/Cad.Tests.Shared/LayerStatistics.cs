using System.Collections.Generic;

namespace Cad.Tests.Shared
{
    public class LayerStatistics
    {
        public int TotalCount { get; set; }

        public Dictionary<string, int> LayerCountMap { get; set; } = new Dictionary<string, int>();
    }
}
