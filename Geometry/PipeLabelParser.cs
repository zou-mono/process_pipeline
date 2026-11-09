using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace process_pipeline.Geometry
{
    /// <summary>
    /// 规划管线图面文字标注解析结果模型（Stage 5 Commit 5.1）。
    /// 
    /// 典型输入格式示例：
    /// - 2-管线规格-现状："d400"、"d800-i1.0"、"d500-i1.5"
    /// - 2-管线名称-2025："d600-i5.6-L1080"、"DN600-L2216"
    /// - 3-标注-管线标注-规划扩建："d700-i2.0-L900"
    /// </summary>
    public class PipeLabelParseResult
    {
        /// <summary>
        /// CAD 实体原始文本内容（DBText.TextString 或 MText.Contents）。
        /// </summary>
        public string RawText { get; set; } = string.Empty;

        /// <summary>
        /// 剥离 MText 格式控制符并标准化分隔符后的纯文本。
        /// </summary>
        public string CleanedText { get; set; } = string.Empty;

        /// <summary>
        /// 是否成功解析出有效的管径/断面宽度（WIDTH &gt; 0）。
        /// </summary>
        public bool IsSuccess { get; set; }

        /// <summary>
        /// 识别到的管径前缀标识（如 "d"、"DN"、"D"、"BxH"）。
        /// </summary>
        public string Prefix { get; set; } = string.Empty;

        /// <summary>
        /// 解析出的管径或渠箱宽度 WIDTH（毫米，如 400、600、700、800）。
        /// </summary>
        public double? Diameter { get; set; }

        /// <summary>
        /// 解析出的渠箱高度 HEITH（毫米；圆管默认为 0，渠箱如 1200x1000 时为 1000）。
        /// </summary>
        public double? Height { get; set; }

        /// <summary>
        /// 解析出的管线坡度 PD（如 "i1.0" -&gt; 1.0, "i5.6" -&gt; 5.6；未包含时为 null）。
        /// </summary>
        public double? Slope { get; set; }

        /// <summary>
        /// 解析出的标注管长 LEN（米，如 "L1080" -&gt; 1080, "L2216" -&gt; 2216；未包含时为 null）。
        /// </summary>
        public double? Length { get; set; }

        /// <summary>
        /// 并排管线根数（默认 1；如 "2DN1000" 时为 2，属于需人工核查的不规范多管标注）。
        /// </summary>
        public int PipeCount { get; set; } = 1;

        /// <summary>
        /// 变径/范围标注的第二管径（毫米，如 "d600-700" 时为 700；正常单管径标注为 null）。
        /// </summary>
        public double? SecondaryDiameter { get; set; }

        /// <summary>
        /// 是否属于需人工重点核查的不规范文字标注格式（如并排多管 "2DN1000"、变径范围 "d600-700" 等）。
        /// </summary>
        public bool HasIrregularFormat { get; set; }

        /// <summary>
        /// 不规范文字标注的具体原因说明（供待核查列表展示，正常标注为空字符串）。
        /// </summary>
        public string IrregularReason { get; set; } = string.Empty;

        /// <summary>
        /// 将解析出的管径（WIDTH）、高度（HEITH）、坡度（PD）、管长（LEN）及不规范标识转换为标准管线属性字典。
        /// </summary>
        public Dictionary<string, object> ToAttributeDictionary()
        {
            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (!IsSuccess || !Diameter.HasValue)
                return dict;

            dict["WIDTH"] = Diameter.Value;
            dict["HEITH"] = Height ?? 0.0;

            if (Slope.HasValue)
            {
                dict["PD"] = Slope.Value;
            }

            if (Length.HasValue)
            {
                dict["LEN"] = Length.Value;
            }

            if (PipeCount > 1)
            {
                dict["PIPE_COUNT"] = PipeCount;
            }

            if (SecondaryDiameter.HasValue)
            {
                dict["WIDTH_MAX"] = SecondaryDiameter.Value;
            }

            if (HasIrregularFormat && !string.IsNullOrWhiteSpace(IrregularReason))
            {
                dict["IRREGULAR_LABEL"] = IrregularReason;
            }

            if (!string.IsNullOrWhiteSpace(CleanedText))
            {
                dict["RAW_LABEL"] = CleanedText;
            }

            return dict;
        }
    }

    /// <summary>
    /// 规划管线文字标注正则解析器（Stage 5 Commit 5.1 &amp; 5.2）。
    /// 
    /// 支持解析：
    /// 1. 圆管标注：d400、D500、DN600、φ800、%%c400 等；
    /// 2. 渠箱标注：1200x1000、2000×1500、B×H=1500×1200 等；
    /// 3. 坡度标注：-i1.0、-i5.6、i=2.0、I1.5 等；
    /// 4. 管长标注：-L1080、-L2216、L=900、-l350 等；
    /// 5. 自动清理 MText 富文本控制序列（如 \A1;、\P、{\f...;...}）与全角连接符；
    /// 6. 自动识别人工作业不规范标注（如并排多管 "2DN1000"、渐变管径 "d600-700"）并输出 HasIrregularFormat 供待核查列表展示。
    /// </summary>
    public static class PipeLabelParser
    {
        // MText 控制序列正则：匹配如 \A1; \fSimSun|...; \H1.2x; \W0.8; \C1; \pxqc; 等
        private static readonly Regex MTextFormatCodeRegex = new Regex(
            @"\\(?:[A-Za-z][^;]*;|[P~LloOkK])|[{}]",
            RegexOptions.Compiled);

        // 圆管直径正则：匹配 DN600, 2DN1000, d400, D800, Φ500 等（限定合理管径数值范围 50 ~ 10000mm，兼容并排双管前缀如 2DN1000）
        private static readonly Regex CircularDiameterRegex = new Regex(
            @"(?<![A-Za-z0-9])(?:(?<count>\d+)\s*[-xX×\*]?\s*)?(?<prefix>DN|dn|Dn|dN|d|D|Φ|φ)\s*[-=:：]?\s*(?<diam>\d{2,5}(?:\.\d+)?)(?![0-9])",
            RegexOptions.Compiled);

        // 变径/范围管径正则：匹配 d600-700, d600~d700, DN600/800 等（第二个数值前无 i/L 等坡度或管长前缀）
        private static readonly Regex VariableDiameterRangeRegex = new Regex(
            @"(?<![A-Za-z0-9])(?:(?:\d+)\s*[-xX×\*]?\s*)?(?:DN|dn|Dn|dN|d|D|Φ|φ)\s*[-=:：]?\s*(?<diam1>\d{2,5}(?:\.\d+)?)\s*[-~/]\s*(?:DN|dn|Dn|dN|d|D|Φ|φ)?\s*(?<diam2>\d{2,5}(?:\.\d+)?)(?![0-9A-Za-z])",
            RegexOptions.Compiled);

        // 矩形渠箱断面正则：匹配 1200x1000, 2000×1500 等
        private static readonly Regex BoxSectionRegex = new Regex(
            @"(?<![A-Za-z0-9])(?<w>\d{3,5}(?:\.\d+)?)\s*[xX×\*]\s*(?<h>\d{3,5}(?:\.\d+)?)(?![0-9])",
            RegexOptions.Compiled);

        // 坡度 i 正则：匹配 -i1.0, -i5.6, i=2.0 等
        private static readonly Regex SlopeRegex = new Regex(
            @"(?:^|[-—–_/\s,;，；])(?:i|I|坡度)\s*[-=:：]?\s*(?<slope>\d+(?:\.\d+)?)",
            RegexOptions.Compiled);

        // 管长 L 正则：匹配 -L1080, -L2216, L=900 等
        private static readonly Regex LengthRegex = new Regex(
            @"(?:^|[-—–_/\s,;，；])(?:L|l|管长|长度)\s*[-=:：]?\s*(?<len>\d+(?:\.\d+)?)",
            RegexOptions.Compiled);

        /// <summary>
        /// 清理 CAD DBText / MText 中的富文本控制符、CAD 特殊直径符号（%%c）及全角分隔符。
        /// </summary>
        public static string CleanCadText(string rawText)
        {
            if (string.IsNullOrWhiteSpace(rawText))
                return string.Empty;

            string text = rawText.Trim();

            // 替换 AutoCAD 直径转义符 %%c / %%C
            text = Regex.Replace(text, @"%%[cC]", "d");

            // 将 MText 换行符 \P 替换为空格，并剔除其余 MText 控制码与花括号
            text = text.Replace("\\P", " ").Replace("\\p", " ");
            text = MTextFormatCodeRegex.Replace(text, string.Empty);

            // 归一化全角横杠、冒号、等号
            text = text
                .Replace('－', '-')
                .Replace('—', '-')
                .Replace('–', '-')
                .Replace('～', '-')
                .Replace('：', ':')
                .Replace('＝', '=');

            // 压缩连续空白字符
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text;
        }

        /// <summary>
        /// 解析单条管线文字标注字符串，返回结构化的管径（WIDTH）、坡度（PD）与管长（LEN）结果。
        /// </summary>
        public static PipeLabelParseResult Parse(string rawText)
        {
            var result = new PipeLabelParseResult
            {
                RawText = rawText ?? string.Empty,
                CleanedText = CleanCadText(rawText)
            };

            if (string.IsNullOrWhiteSpace(result.CleanedText))
                return result;

            string text = result.CleanedText;

            // 1. 优先匹配圆管管径（d400 / DN600 / 2DN1000 / d600-700 等）
            Match circularMatch = CircularDiameterRegex.Match(text);
            if (circularMatch.Success &&
                double.TryParse(circularMatch.Groups["diam"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double diam) &&
                diam >= 50.0 && diam <= 10000.0)
            {
                result.IsSuccess = true;
                result.Prefix = circularMatch.Groups["prefix"].Value;
                result.Diameter = diam;
                result.Height = 0.0;

                var irregularReasons = new List<string>();

                // 检测并排多管前缀（如 "2DN1000"）
                if (circularMatch.Groups["count"].Success &&
                    int.TryParse(circularMatch.Groups["count"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pipeCount) &&
                    pipeCount > 1)
                {
                    result.PipeCount = pipeCount;
                    result.HasIrregularFormat = true;
                    irregularReasons.Add($"并排多管标注({pipeCount}根{result.Prefix}{diam:0})");
                }

                // 检测变径/范围管径标注（如 "d600-700"）
                Match rangeMatch = VariableDiameterRangeRegex.Match(text);
                if (rangeMatch.Success &&
                    double.TryParse(rangeMatch.Groups["diam2"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double diam2) &&
                    diam2 >= 50.0 && diam2 <= 10000.0 &&
                    Math.Abs(diam2 - diam) > 1e-6)
                {
                    result.SecondaryDiameter = diam2;
                    result.HasIrregularFormat = true;
                    irregularReasons.Add($"变径范围标注({result.Prefix}{diam:0}-{diam2:0})");
                }

                if (irregularReasons.Count > 0)
                {
                    result.IrregularReason = string.Join("；", irregularReasons);
                }
            }
            else
            {
                // 2. 备选匹配矩形渠箱断面（W x H）
                Match boxMatch = BoxSectionRegex.Match(text);
                if (boxMatch.Success &&
                    double.TryParse(boxMatch.Groups["w"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double w) &&
                    double.TryParse(boxMatch.Groups["h"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double h) &&
                    w >= 100.0 && w <= 20000.0 && h >= 100.0 && h <= 20000.0)
                {
                    result.IsSuccess = true;
                    result.Prefix = "BxH";
                    result.Diameter = w;
                    result.Height = h;
                }
            }

            if (!result.IsSuccess)
                return result;

            // 3. 提取可选的坡度参数（-i1.0 / -i5.6）
            Match slopeMatch = SlopeRegex.Match(text);
            if (slopeMatch.Success &&
                double.TryParse(slopeMatch.Groups["slope"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double slopeVal) &&
                slopeVal >= 0.0)
            {
                result.Slope = slopeVal;
            }

            // 4. 提取可选的标注管长参数（-L1080 / -L2216）
            Match lenMatch = LengthRegex.Match(text);
            if (lenMatch.Success &&
                double.TryParse(lenMatch.Groups["len"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lenVal) &&
                lenVal > 0.0)
            {
                result.Length = lenVal;
            }

            return result;
        }

        /// <summary>
        /// 尝试解析管线文字标注，当且仅当成功提取出有效管径（WIDTH &gt; 0）时返回 true。
        /// </summary>
        public static bool TryParse(string rawText, out PipeLabelParseResult result)
        {
            result = Parse(rawText);
            return result != null && result.IsSuccess;
        }

        /// <summary>
        /// 从 CAD 文字实体（DBText 或 MText）构建用于 PipeSpatialMatcher 的空间附属要素对象。
        /// 若文字内容不属于合法的管线规格标注（如普通地名、路名说明），则返回 null。
        /// </summary>
        public static SpatialAttachmentItem CreateAttachmentFromTextEntity(
            Entity ent,
            ObjectId objId,
            out PipeLabelParseResult parseResult)
        {
            parseResult = null;
            if (ent == null)
                return null;

            string rawText = null;
            double rotationRad = 0.0;
            Point3d fallbackPoint = Point3d.Origin;

            if (ent is DBText dbText)
            {
                rawText = dbText.TextString;
                rotationRad = dbText.Rotation;
                fallbackPoint = (dbText.HorizontalMode != TextHorizontalMode.TextLeft ||
                                 dbText.VerticalMode != TextVerticalMode.TextBase) &&
                                dbText.AlignmentPoint.DistanceTo(Point3d.Origin) > 1e-3
                    ? dbText.AlignmentPoint
                    : dbText.Position;
            }
            else if (ent is MText mText)
            {
                rawText = !string.IsNullOrWhiteSpace(mText.Contents) ? mText.Contents : mText.Text;
                rotationRad = mText.Rotation;
                fallbackPoint = mText.Location;
            }
            else
            {
                return null;
            }

            if (!TryParse(rawText, out parseResult))
                return null;

            // 优先采用文字实体实际包围盒（GeometricExtents）中心作为空间匹配基准点：
            // 原因：DBText.Position 位于整串长文字（如 "d600-i5.6-L1080"）的左下角起点，
            // 而文字包围盒中心点正好位于文字标注段的几何中点，与管线曲线的距离计算最为准确。
            Point3d repPos = fallbackPoint;
            try
            {
                Extents3d ext = ent.GeometricExtents;
                double cx = (ext.MinPoint.X + ext.MaxPoint.X) * 0.5;
                double cy = (ext.MinPoint.Y + ext.MaxPoint.Y) * 0.5;
                if (!double.IsNaN(cx) && !double.IsInfinity(cx) &&
                    !double.IsNaN(cy) && !double.IsInfinity(cy))
                {
                    repPos = new Point3d(cx, cy, 0.0);
                }
            }
            catch
            {
                repPos = new Point3d(fallbackPoint.X, fallbackPoint.Y, 0.0);
            }

            double rotDeg = (rotationRad * 180.0 / Math.PI) % 360.0;
            if (rotDeg < 0) rotDeg += 360.0;

            var attrs = parseResult.ToAttributeDictionary();

            return new SpatialAttachmentItem
            {
                AttachmentId = objId,
                Handle = !objId.IsNull && objId.IsValid ? objId.Handle.ToString() : (ent.Handle.Value != 0 ? ent.Handle.ToString() : string.Empty),
                LayerName = ent.Layer ?? string.Empty,
                BlockNameOrText = parseResult.CleanedText,
                Position = repPos,
                RotationDegrees = rotDeg,
                // 注意：规划图面上的 -L1080 常表示整条规划干线总长而非单段折线长度，
                // 因此不将 DeclaredLength 作为硬性长度惩罚项，而是由曲线距离 + 平行角主导配对，并将 LEN 存入 Attributes。
                DeclaredLength = null,
                Attributes = attrs,
                XDataLinkedPipeHandle = PipeSpatialMatcher.ReadLinkedHandleFromXData(ent)
            };
        }
    }
}
