using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using WinForms = System.Windows.Forms;

namespace process_pipeline.Forms
{
    public enum CopyFormat
    {
        Smart,      // 多格式写入剪贴板：Text/UnicodeText=TSV, CSV=CommaSeparatedValue
        Csv,
        PlainText,
        Tsv
    }

    public sealed class DataGridCopyOptions
    {
        public bool IncludeHeader { get; set; } = true;
        public bool VisibleColumnsOnly { get; set; } = true;
        public bool KeepDisplayOrder { get; set; } = true;
        public bool KeepSelectionOrder { get; set; } = true;
    }

    public sealed class CopyMatrix
    {
        public List<DataGridColumn> Columns { get; } = new List<DataGridColumn>();
        public List<string> Headers { get; } = new List<string>();
        public List<object> RowItems { get; } = new List<object>();
        public List<string[]> Rows { get; } = new List<string[]>();

        public bool IsEmpty => Rows.Count == 0 || Columns.Count == 0;
    }

    public static class DataGridCopyHelper
    {
        // 缓存属性访问器，减少反射开销（高频复制有价值）
        private static readonly Dictionary<(Type type, string path), PropertyInfo[]> _propPathCache
            = new Dictionary<(Type, string), PropertyInfo[]>();

        private static readonly object _cacheLock = new object();

        public static bool HasSelection(DataGrid dg)
        {
            if (dg == null) return false;
            return dg.SelectedCells != null && dg.SelectedCells.Count > 0;
        }

        /// <summary>
        /// 构建统一“复制矩阵”：列集合 + 行集合 + 单元格文本。
        /// 只基于 SelectedCells，兼容 Cell/RowHeader 混合选择。
        /// </summary>
        public static CopyMatrix BuildMatrix(DataGrid dg, DataGridCopyOptions opt = null)
        {
            //opt ??= new DataGridCopyOptions();
            opt = opt ?? new DataGridCopyOptions();
            var matrix = new CopyMatrix();
            if (dg == null) return matrix;

            var selectedCells = dg.SelectedCells;
            if (selectedCells == null || selectedCells.Count == 0) return matrix;

            // 1) 收集列（去重）
            // 说明：DataGrid.SelectedCells 在行头选择时会包含该行所有列的 cell info（符合期望）
            IEnumerable<DataGridColumn> cols = selectedCells
                .Select(c => c.Column)
                .Where(c => c != null);

            if (opt.VisibleColumnsOnly)
                cols = cols.Where(c => c.Visibility == Visibility.Visible);

            if (opt.KeepDisplayOrder)
                cols = cols.Distinct().OrderBy(c => c.DisplayIndex);
            else
                cols = cols.Distinct();

            matrix.Columns.AddRange(cols);
            if (matrix.Columns.Count == 0) return matrix;

            // headers
            if (opt.IncludeHeader)
            {
                foreach (var c in matrix.Columns)
                    matrix.Headers.Add(c.Header?.ToString() ?? string.Empty);
            }

            // 2) 行分组（保留选择顺序）
            // 注意：SelectedCells 没有强保证“用户点击顺序”，但通常按行出现。这里做稳妥处理。
            var rowOrder = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
            int idx = 0;
            foreach (var sc in selectedCells)
            {
                if (sc.Item == null) continue;
                if (!rowOrder.ContainsKey(sc.Item))
                    rowOrder[sc.Item] = idx++;
            }

            var groups = selectedCells
                .Where(c => c.Item != null && c.Column != null)
                .Where(c => !opt.VisibleColumnsOnly || c.Column.Visibility == Visibility.Visible)
                .GroupBy(c => c.Item);

            if (opt.KeepSelectionOrder)
                groups = groups.OrderBy(g => rowOrder[g.Key]);

            // 3) 逐行构造文本（按列顺序填充；未选到的列留空）
            // 为避免 O(n^2)，先构建列索引映射
            var colIndex = new Dictionary<DataGridColumn, int>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < matrix.Columns.Count; i++)
                colIndex[matrix.Columns[i]] = i;

            foreach (var g in groups)
            {
                var arr = new string[matrix.Columns.Count];
                // 默认空串，不需要初始化（string[] 默认 null，最后兜底）
                foreach (var cell in g)
                {
                    if (!colIndex.TryGetValue(cell.Column, out int ci)) continue;
                    arr[ci] = GetCellText(g.Key, cell.Column);
                }

                for (int i = 0; i < arr.Length; i++)
                    if (arr[i] == null) arr[i] = string.Empty;

                matrix.RowItems.Add(g.Key);
                matrix.Rows.Add(arr);
            }

            return matrix;
        }

        public static void CopyToClipboard(DataGrid dg, CopyFormat format, DataGridCopyOptions opt = null)
        {
            var m = BuildMatrix(dg, opt);
            if (m.IsEmpty) return;

            //var data = new DataObject();
            WinForms.DataObject data = new WinForms.DataObject();

            switch (format)
            {
                case CopyFormat.Smart:
                {
                    string tsv = ToTsv(m, opt?.IncludeHeader ?? true);
                    string csv = ToCsv(m, opt?.IncludeHeader ?? true);

                    //data.SetData(DataFormats.Text, tsv);
                    //data.SetData(DataFormats.UnicodeText, tsv);
                    //data.SetData(DataFormats.CommaSeparatedValue, csv);
                    //Clipboard.SetDataObject(data, true);
                    data.SetData(WinForms.DataFormats.Text, true, tsv);
                    data.SetData(WinForms.DataFormats.UnicodeText, true, tsv);
                    data.SetData(WinForms.DataFormats.CommaSeparatedValue, true, csv);
                    break;
                }
                case CopyFormat.Csv:
                {
                    //Clipboard.SetText(ToCsv(m, opt?.IncludeHeader ?? true));
                    data.SetData(WinForms.DataFormats.UnicodeText, true, ToCsv(m, opt?.IncludeHeader ?? true));
                    break;
                }
                case CopyFormat.PlainText:
                {
                    //Clipboard.SetText(ToPlainText(m, opt?.IncludeHeader ?? true));
                    data.SetData(WinForms.DataFormats.UnicodeText, true, ToPlainText(m, opt?.IncludeHeader ?? true));
                    break;
                }
                case CopyFormat.Tsv:
                default:
                {
                    data.SetData(WinForms.DataFormats.UnicodeText, true, ToTsv(m, opt?.IncludeHeader ?? true));
                    //Clipboard.SetText(ToTsv(m, opt?.IncludeHeader ?? true));
                    break;
                }
            }

            // 参数说明：数据对象, 退出后保留, 重试次数(10次), 重试间隔(100毫秒)
            // 【核心改动】：静默重试逻辑
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    // 尝试写入
                    WinForms.Clipboard.SetDataObject(data, true, 5, 50);
                    
                    //WinForms.Clipboard.SetDataObject(data, true, 10, 100);
            
                    // 【关键验证】：检查是否真的写进去了
                    // 如果剪贴板现在确实包含文本，说明我们成功了，直接退出
                    if (WinForms.Clipboard.ContainsText()) 
                    {
                        return; 
                    }
                }
                catch
                {
                    // 第一次报错？没关系，先检查一下剪贴板
                    // 如果虽然报错了，但数据其实已经进去了，那就当没发生过
                    if (WinForms.Clipboard.ContainsText()) return;
            
                    // 否则，稍微等一下再试
                    System.Threading.Thread.Sleep(100);
                }
            }

            //// 2. 【关键修复】：带重试的剪贴板写入
            //for (int i = 0; i < 10; i++) // 尝试 10 次
            //{
            //    try
            //    {
            //        // 第二个参数 true 非常重要，表示即使程序关闭，数据也保留在剪贴板
            //        Clipboard.SetDataObject(data, true);
            //        return; // 成功写入，立即退出
            //    }
            //    catch (System.Runtime.InteropServices.COMException ex)
            //    {
            //        // 0x800401D0 就是 OpenClipboard 失败错误
            //        if ((uint)ex.ErrorCode == 0x800401D0)
            //        {
            //            // 稍微等一下（50毫秒），让抢占剪贴板的进程（如CAD监视器）释放
            //            System.Threading.Thread.Sleep(50);
            //            continue;
            //        }
            //        throw; // 其他 COM 错误则抛出
            //    }
            //    catch (Exception)
            //    {
            //        if (i == 9) throw; // 最后一次尝试也失败了，才抛出异常
            //        System.Threading.Thread.Sleep(50);
            //    }
            //}
        }

        public static string ToTsv(CopyMatrix m, bool includeHeader)
        {
            var sb = new StringBuilder(capacity: Math.Max(256, m.Rows.Count * 32));

            if (includeHeader && m.Headers.Count > 0)
            {
                AppendDelimitedLine(sb, m.Headers, '\t', EscapeTsv);
            }

            foreach (var row in m.Rows)
            {
                AppendDelimitedLine(sb, row, '\t', EscapeTsv);
            }

            TrimLastNewLine(sb);
            return sb.ToString();
        }

        public static string ToCsv(CopyMatrix m, bool includeHeader)
        {
            var sb = new StringBuilder(capacity: Math.Max(256, m.Rows.Count * 32));

            if (includeHeader && m.Headers.Count > 0)
            {
                AppendDelimitedLine(sb, m.Headers, ',', EscapeCsv);
            }

            foreach (var row in m.Rows)
            {
                AppendDelimitedLine(sb, row, ',', EscapeCsv);
            }

            TrimLastNewLine(sb);
            return sb.ToString();
        }

        public static string ToPlainText(CopyMatrix m, bool includeHeader)
        {
            var sb = new StringBuilder(capacity: Math.Max(256, m.Rows.Count * 64));

            for (int r = 0; r < m.Rows.Count; r++)
            {
                var row = m.Rows[r];
                for (int c = 0; c < row.Length; c++)
                {
                    string key = includeHeader && c < m.Headers.Count
                        ? m.Headers[c]
                        : $"Col{c + 1}";
                    sb.Append(key).Append(": ").Append(row[c] ?? string.Empty).AppendLine();
                }
                if (r < m.Rows.Count - 1) sb.AppendLine("----");
            }

            TrimLastNewLine(sb);
            return sb.ToString();
        }

        private static void AppendDelimitedLine(
            StringBuilder sb,
            IEnumerable<string> fields,
            char delimiter,
            Func<string, string> escaper)
        {
            bool first = true;
            foreach (var f in fields)
            {
                if (!first) sb.Append(delimiter);
                sb.Append(escaper(f ?? string.Empty));
                first = false;
            }
            sb.AppendLine();
        }

        private static string EscapeCsv(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            bool needQuote = s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
            if (!needQuote) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        private static string EscapeTsv(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            bool needQuote = s.IndexOfAny(new[] { '\t', '"', '\r', '\n' }) >= 0;
            if (!needQuote) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        private static void TrimLastNewLine(StringBuilder sb)
        {
            // 替换 ^1 为 sb.Length - 1，兼容 C# 7.3
            while (sb.Length > 0 && (sb[sb.Length - 1] == '\r' || sb[sb.Length - 1] == '\n'))
            {
                sb.Length--;
            }
        }

        /// <summary>
        /// 高性能取值：
        /// 1) 优先 DataGridBoundColumn.Binding.Path
        /// 2) 支持简单路径 a.b.c
        /// 3) 失败回退到 ToString()
        /// </summary>
        private static string GetCellText(object item, DataGridColumn column)
        {
            if (item == null || column == null) return string.Empty;

            if (column is DataGridBoundColumn bc && bc.Binding is Binding b)
            {
                var path = b.Path?.Path;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var val = GetValueByPath(item, path);
                    if (val == null) return string.Empty;

                    // StringFormat 支持（可选）
                    if (!string.IsNullOrWhiteSpace(b.StringFormat))
                    {
                        try
                        {
                            return string.Format(CultureInfo.CurrentCulture, b.StringFormat, val) ?? string.Empty;
                        }
                        catch
                        {
                            // 忽略格式异常，走默认
                        }
                    }

                    return Convert.ToString(val, CultureInfo.CurrentCulture) ?? string.Empty;
                }
            }

            return item.ToString() ?? string.Empty;
        }

        private static object GetValueByPath(object obj, string path)
        {
            if (obj == null || string.IsNullOrWhiteSpace(path)) return null;

            var t = obj.GetType();
            PropertyInfo[] props;

            lock (_cacheLock)
            {
                if (!_propPathCache.TryGetValue((t, path), out props))
                {
                    var names = path.Split('.');
                    var list = new List<PropertyInfo>(names.Length);
                    Type cur = t;
                    foreach (var n in names)
                    {
                        var p = cur.GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
                        if (p == null)
                        {
                            props = null;
                            _propPathCache[(t, path)] = null;
                            return null;
                        }
                        list.Add(p);
                        cur = p.PropertyType;
                    }
                    props = list.ToArray();
                    _propPathCache[(t, path)] = props;
                }
            }

            if (props == null) return null;

            object curObj = obj;
            for (int i = 0; i < props.Length; i++)
            {
                if (curObj == null) return null;
                curObj = props[i].GetValue(curObj, null);
            }
            return curObj;
        }

        // 引用相等比较器：避免 item 自定义 Equals 导致分组混乱
        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
