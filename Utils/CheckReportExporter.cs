using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Text;
using process_pipeline.Core;
using process_pipeline.Forms;

namespace process_pipeline.Utils
{
    /// <summary>
    /// 核查报表导出工作表数据模型（对应左侧导航树的单个【核查类型】根节点）。
    /// </summary>
    public sealed class CheckReportSheetData
    {
        /// <summary>
        /// 核查类型名称（对应左侧树根节点名称及 Excel Sheet 名称，如“流向箭头核查”、“图属匹配核查”）。
        /// </summary>
        public string RuleName { get; set; }

        /// <summary>
        /// 该核查类型下的全部记录行。
        /// </summary>
        public IReadOnlyList<PipeRowVm> Rows { get; set; } = Array.Empty<PipeRowVm>();
    }

    /// <summary>
    /// 管线核查报表导出工具类（Commit 6）：
    /// 1. 支持按左侧导航树【核查类型】根节点分 Sheet 导出标准 OpenXML <c>.xlsx</c> 工作簿（基于 WindowsBase 内置 <see cref="Package"/>，零第三方 DLL 依赖，纯正 UTF-8 编码）；
    /// 2. 支持导出合并单表 <c>.csv</c> 文件（采用带 BOM 的 UTF-8 编码 <c>EF BB BF</c>，确保 Windows Excel 与 WPS 双击打开中文 100% 零乱码）。
    /// </summary>
    public static class CheckReportExporter
    {
        private const string WorkbookContentType =
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
        private const string WorksheetContentType =
            "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml";
        private const string StylesContentType =
            "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml";

        private const string OfficeDocumentRelType =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
        private const string WorksheetRelType =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet";
        private const string StylesRelType =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles";

        private static readonly string[] SheetColumnHeaders =
        {
            "序号",
            "编号",
            "核查类型",
            "核查状态",
            "所属图层",
            "位置坐标",
            "问题描述 / 备注",
            "推荐解决方案"
        };

        private static readonly string[] CsvColumnHeaders =
        {
            "序号",
            "核查类型",
            "编号",
            "核查状态",
            "所属图层",
            "位置坐标",
            "问题描述 / 备注",
            "推荐解决方案"
        };

        /// <summary>
        /// 将核查记录按核查类型分 Sheet 导出为标准 OpenXML (.xlsx) 文件。
        /// </summary>
        public static void ExportToExcelXlsx(
            string filePath,
            IReadOnlyList<CheckReportSheetData> sheets,
            ProgressContext progress = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentNullException(nameof(filePath));
            if (sheets == null || sheets.Count == 0)
                throw new ArgumentException("至少需要一个核查类型工作表数据。", nameof(sheets));

            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var usedSheetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sheetEntries = new List<(string SheetName, string RelId, IReadOnlyList<PipeRowVm> Rows)>(sheets.Count);

            progress?.SetTotal(Math.Max(1, sheets.Count));

            using (Package package = Package.Open(filePath, FileMode.Create, FileAccess.ReadWrite))
            {
                Uri workbookUri = new Uri("/xl/workbook.xml", UriKind.Relative);
                PackagePart workbookPart = package.CreatePart(workbookUri, WorkbookContentType, CompressionOption.Normal);
                package.CreateRelationship(workbookUri, TargetMode.Internal, OfficeDocumentRelType, "rIdWorkbook");

                Uri stylesUri = new Uri("/xl/styles.xml", UriKind.Relative);
                PackagePart stylesPart = package.CreatePart(stylesUri, StylesContentType, CompressionOption.Normal);
                workbookPart.CreateRelationship(new Uri("styles.xml", UriKind.Relative), TargetMode.Internal, StylesRelType, "rIdStyles");
                WriteStylesXml(stylesPart, utf8NoBom);

                for (int i = 0; i < sheets.Count; i++)
                {
                    CheckReportSheetData sheetData = sheets[i];
                    string safeSheetName = SanitizeSheetName(sheetData?.RuleName, i + 1, usedSheetNames);
                    string relId = $"rIdSheet{i + 1}";
                    IReadOnlyList<PipeRowVm> rows = sheetData?.Rows ?? Array.Empty<PipeRowVm>();

                    Uri sheetUri = new Uri($"/xl/worksheets/sheet{i + 1}.xml", UriKind.Relative);
                    PackagePart sheetPart = package.CreatePart(sheetUri, WorksheetContentType, CompressionOption.Normal);
                    workbookPart.CreateRelationship(
                        new Uri($"worksheets/sheet{i + 1}.xml", UriKind.Relative),
                        TargetMode.Internal,
                        WorksheetRelType,
                        relId);

                    WriteWorksheetXml(sheetPart, sheetData?.RuleName ?? safeSheetName, rows, utf8NoBom);
                    sheetEntries.Add((safeSheetName, relId, rows));
                    progress?.Step();
                }

                WriteWorkbookXml(workbookPart, sheetEntries, utf8NoBom);
                package.Flush();
            }
        }

        /// <summary>
        /// 将核查记录导出为带 UTF-8 BOM (<c>EF BB BF</c>) 的 <c>.csv</c> 文件，确保 Excel / WPS 打开中文零乱码。
        /// </summary>
        public static void ExportToCsv(
            string filePath,
            IReadOnlyList<CheckReportSheetData> sheets,
            ProgressContext progress = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentNullException(nameof(filePath));
            if (sheets == null)
                throw new ArgumentNullException(nameof(sheets));

            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            progress?.SetTotal(Math.Max(1, sheets.Count));

            // 显式使用带 BOM 的 UTF-8 (EF BB BF)，解决 Windows Excel 双击打开 UTF-8 CSV 中文乱码问题
            var utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(fs, utf8WithBom))
            {
                writer.WriteLine(string.Join(",", CsvColumnHeaders.Select(EscapeCsvField)));

                int seq = 1;
                foreach (CheckReportSheetData sheet in sheets)
                {
                    if (sheet?.Rows == null)
                    {
                        progress?.Step();
                        continue;
                    }
                    string fallbackRuleName = sheet.RuleName ?? string.Empty;

                    foreach (PipeRowVm row in sheet.Rows)
                    {
                        if (row == null) continue;

                        string ruleName = !string.IsNullOrWhiteSpace(row.RuleName) ? row.RuleName : fallbackRuleName;
                        string id = row.Id ?? string.Empty;
                        string status = row.Status ?? string.Empty;
                        string layer = row.Type ?? string.Empty;
                        string location = row.LocationText ?? "-";
                        string remark = row.Remark ?? string.Empty;
                        string solution = row.SolutionText ?? string.Empty;

                        string[] fields =
                        {
                            seq.ToString(CultureInfo.InvariantCulture),
                            EscapeCsvField(ruleName),
                            EscapeCsvField(id),
                            EscapeCsvField(status),
                            EscapeCsvField(layer),
                            EscapeCsvField(location),
                            EscapeCsvField(remark),
                            EscapeCsvField(solution)
                        };

                        writer.WriteLine(string.Join(",", fields));
                        seq++;
                    }

                    progress?.Step();
                }
            }
        }

        private static void WriteWorkbookXml(
            PackagePart workbookPart,
            IReadOnlyList<(string SheetName, string RelId, IReadOnlyList<PipeRowVm> Rows)> sheets,
            Encoding encoding)
        {
            using (Stream stream = workbookPart.GetStream(FileMode.Create, FileAccess.Write))
            using (var writer = new StreamWriter(stream, encoding))
            {
                writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
                writer.Write("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" ");
                writer.Write("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
                writer.Write("<sheets>");

                for (int i = 0; i < sheets.Count; i++)
                {
                    var entry = sheets[i];
                    writer.Write("<sheet name=\"");
                    writer.Write(EscapeXmlText(entry.SheetName));
                    writer.Write("\" sheetId=\"");
                    writer.Write((i + 1).ToString(CultureInfo.InvariantCulture));
                    writer.Write("\" r:id=\"");
                    writer.Write(EscapeXmlText(entry.RelId));
                    writer.Write("\"/>");
                }

                writer.Write("</sheets>");
                writer.Write("</workbook>");
            }
        }

        private static void WriteStylesXml(PackagePart stylesPart, Encoding encoding)
        {
            using (Stream stream = stylesPart.GetStream(FileMode.Create, FileAccess.Write))
            using (var writer = new StreamWriter(stream, encoding))
            {
                writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
                writer.Write("<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

                // 字体：0=正文微软雅黑10pt，1=表头微软雅黑10pt加粗
                writer.Write("<fonts count=\"2\">");
                writer.Write("<font><sz val=\"10\"/><color rgb=\"FF1F2328\"/><name val=\"Microsoft YaHei\"/></font>");
                writer.Write("<font><b/><sz val=\"10\"/><color rgb=\"FF1F2328\"/><name val=\"Microsoft YaHei\"/></font>");
                writer.Write("</fonts>");

                // 填充：0=none，1=gray125（Excel 规范保留前两项），2=表头浅灰蓝底色
                writer.Write("<fills count=\"3\">");
                writer.Write("<fill><patternFill patternType=\"none\"/></fill>");
                writer.Write("<fill><patternFill patternType=\"gray125\"/></fill>");
                writer.Write("<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFEAEFF5\"/><bgColor indexed=\"64\"/></patternFill></fill>");
                writer.Write("</fills>");

                // 边框：0=无边框，1=细网格线
                writer.Write("<borders count=\"2\">");
                writer.Write("<border><left/><right/><top/><bottom/><diagonal/></border>");
                writer.Write("<border>");
                writer.Write("<left style=\"thin\"><color rgb=\"FFD0D7DE\"/></left>");
                writer.Write("<right style=\"thin\"><color rgb=\"FFD0D7DE\"/></right>");
                writer.Write("<top style=\"thin\"><color rgb=\"FFD0D7DE\"/></top>");
                writer.Write("<bottom style=\"thin\"><color rgb=\"FFD0D7DE\"/></bottom>");
                writer.Write("<diagonal/>");
                writer.Write("</border>");
                writer.Write("</borders>");

                writer.Write("<cellStyleXfs count=\"1\">");
                writer.Write("<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/>");
                writer.Write("</cellStyleXfs>");

                // 单元格样式：0=默认，1=表头居中加粗带底色，2=正文居中，3=正文左对齐
                writer.Write("<cellXfs count=\"4\">");
                writer.Write("<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>");
                writer.Write("<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\">");
                writer.Write("<alignment horizontal=\"center\" vertical=\"center\"/>");
                writer.Write("</xf>");
                writer.Write("<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\">");
                writer.Write("<alignment horizontal=\"center\" vertical=\"center\"/>");
                writer.Write("</xf>");
                writer.Write("<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\">");
                writer.Write("<alignment horizontal=\"left\" vertical=\"center\" wrapText=\"1\"/>");
                writer.Write("</xf>");
                writer.Write("</cellXfs>");

                writer.Write("</styleSheet>");
            }
        }

        private static void WriteWorksheetXml(
            PackagePart sheetPart,
            string fallbackRuleName,
            IReadOnlyList<PipeRowVm> rows,
            Encoding encoding)
        {
            double[] colWidths = { 8.0, 12.0, 18.0, 12.0, 22.0, 22.0, 48.0, 44.0 };
            for (int c = 0; c < SheetColumnHeaders.Length; c++)
            {
                colWidths[c] = Math.Max(colWidths[c], EstimateDisplayWidth(SheetColumnHeaders[c]) + 4.0);
            }

            if (rows != null)
            {
                foreach (PipeRowVm row in rows)
                {
                    if (row == null) continue;
                    string ruleName = !string.IsNullOrWhiteSpace(row.RuleName) ? row.RuleName : fallbackRuleName;
                    colWidths[1] = Math.Min(24.0, Math.Max(colWidths[1], EstimateDisplayWidth(row.Id) + 3.0));
                    colWidths[2] = Math.Min(28.0, Math.Max(colWidths[2], EstimateDisplayWidth(ruleName) + 3.0));
                    colWidths[3] = Math.Min(16.0, Math.Max(colWidths[3], EstimateDisplayWidth(row.Status) + 3.0));
                    colWidths[4] = Math.Min(36.0, Math.Max(colWidths[4], EstimateDisplayWidth(row.Type) + 3.0));
                    colWidths[5] = Math.Min(32.0, Math.Max(colWidths[5], EstimateDisplayWidth(row.LocationText) + 3.0));
                    colWidths[6] = Math.Min(75.0, Math.Max(colWidths[6], EstimateDisplayWidth(row.Remark) + 4.0));
                    colWidths[7] = Math.Min(65.0, Math.Max(colWidths[7], EstimateDisplayWidth(row.SolutionText) + 4.0));
                }
            }

            using (Stream stream = sheetPart.GetStream(FileMode.Create, FileAccess.Write))
            using (var writer = new StreamWriter(stream, encoding))
            {
                writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
                writer.Write("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

                // 冻结首行表头
                writer.Write("<sheetViews><sheetView workbookViewId=\"0\">");
                writer.Write("<pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/>");
                writer.Write("</sheetView></sheetViews>");

                writer.Write("<sheetFormatPr defaultRowHeight=\"20\"/>");

                // 自适应列宽定义
                writer.Write("<cols>");
                for (int c = 0; c < colWidths.Length; c++)
                {
                    int colIndex = c + 1;
                    writer.Write("<col min=\"");
                    writer.Write(colIndex.ToString(CultureInfo.InvariantCulture));
                    writer.Write("\" max=\"");
                    writer.Write(colIndex.ToString(CultureInfo.InvariantCulture));
                    writer.Write("\" width=\"");
                    writer.Write(colWidths[c].ToString("F1", CultureInfo.InvariantCulture));
                    writer.Write("\" customWidth=\"1\"/>");
                }
                writer.Write("</cols>");

                writer.Write("<sheetData>");

                // 第 1 行：表头
                writer.Write("<row r=\"1\" ht=\"24\" customHeight=\"1\">");
                for (int c = 0; c < SheetColumnHeaders.Length; c++)
                {
                    WriteInlineStringCell(writer, GetColumnLetter(c) + "1", SheetColumnHeaders[c], styleIndex: 1);
                }
                writer.Write("</row>");

                // 数据行
                if (rows != null)
                {
                    int rowNumber = 2;
                    int seq = 1;
                    foreach (PipeRowVm row in rows)
                    {
                        if (row == null) continue;

                        string rowNumStr = rowNumber.ToString(CultureInfo.InvariantCulture);
                        string ruleName = !string.IsNullOrWhiteSpace(row.RuleName) ? row.RuleName : fallbackRuleName;

                        writer.Write("<row r=\"");
                        writer.Write(rowNumStr);
                        writer.Write("\" ht=\"20\">");

                        WriteNumberCell(writer, "A" + rowNumStr, seq, styleIndex: 2);
                        WriteInlineStringCell(writer, "B" + rowNumStr, row.Id ?? string.Empty, styleIndex: 2);
                        WriteInlineStringCell(writer, "C" + rowNumStr, ruleName ?? string.Empty, styleIndex: 2);
                        WriteInlineStringCell(writer, "D" + rowNumStr, row.Status ?? string.Empty, styleIndex: 2);
                        WriteInlineStringCell(writer, "E" + rowNumStr, row.Type ?? string.Empty, styleIndex: 3);
                        WriteInlineStringCell(writer, "F" + rowNumStr, row.LocationText ?? "-", styleIndex: 2);
                        WriteInlineStringCell(writer, "G" + rowNumStr, row.Remark ?? string.Empty, styleIndex: 3);
                        WriteInlineStringCell(writer, "H" + rowNumStr, row.SolutionText ?? string.Empty, styleIndex: 3);

                        writer.Write("</row>");
                        rowNumber++;
                        seq++;
                    }
                }

                writer.Write("</sheetData>");
                writer.Write("</worksheet>");
            }
        }

        private static void WriteNumberCell(TextWriter writer, string cellRef, int numberValue, int styleIndex)
        {
            writer.Write("<c r=\"");
            writer.Write(cellRef);
            writer.Write("\" s=\"");
            writer.Write(styleIndex.ToString(CultureInfo.InvariantCulture));
            writer.Write("\"><v>");
            writer.Write(numberValue.ToString(CultureInfo.InvariantCulture));
            writer.Write("</v></c>");
        }

        private static void WriteInlineStringCell(TextWriter writer, string cellRef, string text, int styleIndex)
        {
            writer.Write("<c r=\"");
            writer.Write(cellRef);
            writer.Write("\" s=\"");
            writer.Write(styleIndex.ToString(CultureInfo.InvariantCulture));
            writer.Write("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">");
            writer.Write(EscapeXmlText(text));
            writer.Write("</t></is></c>");
        }

        private static string GetColumnLetter(int zeroBasedColIndex)
        {
            char letter = (char)('A' + zeroBasedColIndex);
            return letter.ToString();
        }

        /// <summary>
        /// 清理并规范化 Excel Sheet 名称（遵循 Excel 31 字符上限与特殊字符禁令，并确保同工作簿内不重名）。
        /// </summary>
        public static string SanitizeSheetName(string rawName, int fallbackIndex, HashSet<string> usedNames)
        {
            string cleaned = (rawName ?? string.Empty).Trim();
            char[] invalidChars = { '\\', '/', '?', '*', ':', '[', ']' };
            foreach (char c in invalidChars)
            {
                cleaned = cleaned.Replace(c.ToString(), "_");
            }
            cleaned = cleaned.Trim('\'');

            if (string.IsNullOrWhiteSpace(cleaned))
            {
                cleaned = $"核查项{fallbackIndex}";
            }

            if (cleaned.Length > 31)
            {
                cleaned = cleaned.Substring(0, 31);
            }

            string candidate = cleaned;
            int suffix = 2;
            while (usedNames != null && !usedNames.Add(candidate))
            {
                string suffixText = $"_{suffix}";
                int maxPrefixLen = Math.Max(1, 31 - suffixText.Length);
                string prefix = cleaned.Length > maxPrefixLen ? cleaned.Substring(0, maxPrefixLen) : cleaned;
                candidate = prefix + suffixText;
                suffix++;
            }

            return candidate;
        }

        private static string EscapeXmlText(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            var sb = new StringBuilder(input.Length + 8);
            foreach (char ch in input)
            {
                switch (ch)
                {
                    case '&':
                        sb.Append("&amp;");
                        break;
                    case '<':
                        sb.Append("&lt;");
                        break;
                    case '>':
                        sb.Append("&gt;");
                        break;
                    case '"':
                        sb.Append("&quot;");
                        break;
                    case '\'':
                        sb.Append("&apos;");
                        break;
                    default:
                        if (ch == '\t' || ch == '\n' || ch == '\r' || ch >= 0x20)
                        {
                            sb.Append(ch);
                        }
                        break;
                }
            }
            return sb.ToString();
        }

        private static string EscapeCsvField(string field)
        {
            if (string.IsNullOrEmpty(field))
                return string.Empty;

            string normalized = field;
            // 防止以 = / + / - / @ 开头的长公式文本被 Excel 误解析为公式报 #NAME?（单独的 "-" 保持原样）
            if (normalized.Length > 1 &&
                (normalized[0] == '=' || normalized[0] == '+' || normalized[0] == '@'))
            {
                normalized = "'" + normalized;
            }

            bool mustQuote = normalized.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ||
                             normalized.StartsWith(" ", StringComparison.Ordinal) ||
                             normalized.EndsWith(" ", StringComparison.Ordinal);

            if (!mustQuote)
                return normalized;

            return "\"" + normalized.Replace("\"", "\"\"") + "\"";
        }

        private static double EstimateDisplayWidth(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0.0;

            double width = 0.0;
            foreach (char ch in text)
            {
                if (ch == '\r' || ch == '\n')
                    continue;
                width += ch > 127 ? 2.0 : 1.1;
            }
            return width;
        }
    }
}
