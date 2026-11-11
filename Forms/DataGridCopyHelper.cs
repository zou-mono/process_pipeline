using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
        public static bool HasSelection(DataGrid dg) =>
            dg != null &&
            ((dg.SelectedItems != null && dg.SelectedItems.Count > 0) ||
             (dg.SelectedCells != null && dg.SelectedCells.Count > 0));

        /// <summary>
        /// 构建统一“复制矩阵”：兼容整行选择（FullRow / SelectedItems）与单元格选择（SelectedCells），
        /// 并通过 WPF 原生 ClipboardContentBinding / OnCopyingCellClipboardContent 提取包含模板列在内的文本。
        /// </summary>
        public static CopyMatrix BuildMatrix(DataGrid dg, DataGridCopyOptions opt = null)
        {
            opt = opt ?? new DataGridCopyOptions();
            var matrix = new CopyMatrix();
            if (!HasSelection(dg)) return matrix;

            bool useFullRowItems = dg.SelectionUnit == DataGridSelectionUnit.FullRow &&
                                   dg.SelectedItems != null &&
                                   dg.SelectedItems.Count > 0;

            IEnumerable<DataGridColumn> cols = useFullRowItems
                ? dg.Columns
                : dg.SelectedCells.Select(c => c.Column).Where(c => c != null).Distinct();

            if (opt.VisibleColumnsOnly)
                cols = cols.Where(c => c.Visibility == Visibility.Visible);

            if (opt.KeepDisplayOrder)
                cols = cols.OrderBy(c => c.DisplayIndex >= 0 ? c.DisplayIndex : int.MaxValue);

            matrix.Columns.AddRange(cols);
            if (matrix.Columns.Count == 0) return matrix;

            if (opt.IncludeHeader)
            {
                foreach (var c in matrix.Columns)
                    matrix.Headers.Add(c.Header?.ToString() ?? string.Empty);
            }

            if (useFullRowItems)
            {
                foreach (object item in dg.SelectedItems)
                {
                    if (item == null) continue;
                    var rowText = new string[matrix.Columns.Count];
                    for (int i = 0; i < matrix.Columns.Count; i++)
                        rowText[i] = GetCellText(item, matrix.Columns[i]);

                    matrix.RowItems.Add(item);
                    matrix.Rows.Add(rowText);
                }
                return matrix;
            }

            var colIndex = new Dictionary<DataGridColumn, int>(matrix.Columns.Count);
            for (int i = 0; i < matrix.Columns.Count; i++)
                colIndex[matrix.Columns[i]] = i;

            var rowBuffer = new Dictionary<object, string[]>(ReferenceEqualityComparer.Instance);
            foreach (var cell in dg.SelectedCells)
            {
                if (cell.Item == null || cell.Column == null) continue;
                if (!colIndex.TryGetValue(cell.Column, out int ci)) continue;

                if (!rowBuffer.TryGetValue(cell.Item, out string[] arr))
                {
                    arr = new string[matrix.Columns.Count];
                    for (int i = 0; i < arr.Length; i++) arr[i] = string.Empty;
                    rowBuffer[cell.Item] = arr;
                    matrix.RowItems.Add(cell.Item);
                    matrix.Rows.Add(arr);
                }
                arr[ci] = GetCellText(cell.Item, cell.Column);
            }

            return matrix;
        }

        public static void CopyToClipboard(DataGrid dg, CopyFormat format, DataGridCopyOptions opt = null)
        {
            var m = BuildMatrix(dg, opt);
            if (m.IsEmpty) return;

            bool includeHeader = opt?.IncludeHeader ?? true;
            var data = new WinForms.DataObject();

            switch (format)
            {
                case CopyFormat.Smart:
                    string tsv = ToTsv(m, includeHeader);
                    data.SetData(WinForms.DataFormats.Text, true, tsv);
                    data.SetData(WinForms.DataFormats.UnicodeText, true, tsv);
                    data.SetData(WinForms.DataFormats.CommaSeparatedValue, true, ToCsv(m, includeHeader));
                    break;
                case CopyFormat.Csv:
                    data.SetData(WinForms.DataFormats.UnicodeText, true, ToCsv(m, includeHeader));
                    break;
                case CopyFormat.PlainText:
                    data.SetData(WinForms.DataFormats.UnicodeText, true, ToPlainText(m, includeHeader));
                    break;
                case CopyFormat.Tsv:
                default:
                    data.SetData(WinForms.DataFormats.UnicodeText, true, ToTsv(m, includeHeader));
                    break;
            }

            // 直接使用 WinForms.Clipboard 内置的底层重试机制（5次，每次间隔30ms），避免二次打开剪贴板抢占锁
            WinForms.Clipboard.SetDataObject(data, copy: true, retryTimes: 5, retryDelay: 30);
        }

        public static string ToTsv(CopyMatrix m, bool includeHeader) =>
            FormatDelimited(m, includeHeader, '\t', new[] { '\t', '"', '\r', '\n' });

        public static string ToCsv(CopyMatrix m, bool includeHeader) =>
            FormatDelimited(m, includeHeader, ',', new[] { ',', '"', '\r', '\n' });

        public static string ToPlainText(CopyMatrix m, bool includeHeader)
        {
            var sb = new StringBuilder(Math.Max(256, m.Rows.Count * 64));
            for (int r = 0; r < m.Rows.Count; r++)
            {
                var row = m.Rows[r];
                for (int c = 0; c < row.Length; c++)
                {
                    string key = includeHeader && c < m.Headers.Count ? m.Headers[c] : $"Col{c + 1}";
                    sb.Append(key).Append(": ").Append(row[c] ?? string.Empty).AppendLine();
                }
                if (r < m.Rows.Count - 1) sb.AppendLine("----");
            }
            return sb.ToString().TrimEnd('\r', '\n');
        }

        private static string FormatDelimited(CopyMatrix m, bool includeHeader, char delimiter, char[] quoteChars)
        {
            var sb = new StringBuilder(Math.Max(256, m.Rows.Count * 32));
            if (includeHeader && m.Headers.Count > 0)
                AppendLine(sb, m.Headers, delimiter, quoteChars);

            foreach (var row in m.Rows)
                AppendLine(sb, row, delimiter, quoteChars);

            return sb.ToString().TrimEnd('\r', '\n');
        }

        private static void AppendLine(StringBuilder sb, IList<string> fields, char delimiter, char[] quoteChars)
        {
            for (int i = 0; i < fields.Count; i++)
            {
                if (i > 0) sb.Append(delimiter);
                string s = fields[i] ?? string.Empty;
                sb.Append(s.IndexOfAny(quoteChars) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s);
            }
            sb.AppendLine();
        }

        private static string GetCellText(object item, DataGridColumn column)
        {
            if (item == null || column == null) return string.Empty;

            object val = null;
            try
            {
                val = column.OnCopyingCellClipboardContent(item);
            }
            catch
            {
                // 无 UI 容器上下文时走下方 ClipboardContentBinding 路径回退
            }

            if (val == null && column.ClipboardContentBinding is Binding b && !string.IsNullOrWhiteSpace(b.Path?.Path))
            {
                val = GetPropertyValue(item, b.Path.Path);
                if (val != null && !string.IsNullOrWhiteSpace(b.StringFormat))
                {
                    try { return string.Format(CultureInfo.CurrentCulture, b.StringFormat, val) ?? string.Empty; }
                    catch { }
                }
            }

            return val != null ? (Convert.ToString(val, CultureInfo.CurrentCulture) ?? string.Empty) : string.Empty;
        }

        private static object GetPropertyValue(object obj, string path)
        {
            object cur = obj;
            foreach (string segment in path.Split('.'))
            {
                if (cur == null) return null;
                PropertyInfo prop = cur.GetType().GetProperty(segment, BindingFlags.Public | BindingFlags.Instance);
                if (prop == null) return null;
                cur = prop.GetValue(cur, null);
            }
            return cur;
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
