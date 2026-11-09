using process_pipeline.Themes;
using process_pipeline.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace process_pipeline.Forms
{
    /// <summary>
    /// AutoCAD 2019 工业风格消息提示对话框（同时适配 COLORTHEME=0 深色主题与 COLORTHEME=1 浅色主题）。
    /// </summary>
    public partial class CadMessageDialog : Window
    {
        private enum TokenType
        {
            Text,
            Marker,      // [Info], [Title], [Hint] 等
            BoldToggle,  // **
            BigOpen,     // [Big]
            BigClose,    // [/Big]
            LineBreak    // \n
        }

        private class TextState
        {
            public bool IsBold { get; set; }
            public bool IsBig { get; set; }
            public bool IsTitleNext { get; set; }
        }

        private sealed class MessageToken
        {
            public TokenType Type { get; set; }
            public string Value { get; set; }
        }

        private static readonly Dictionary<string, string> MarkerConfigs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "[Title]",   null },
            { "[Info]",    "Icon_Info" },
            { "[Warning]", "Icon_Warning" },
            { "[Error]",   "Icon_Error" },
            { "[Hint]",    "Icon_Hint" }
        };

        private static readonly Dictionary<string, string> MarkerBrushKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Icon_Info",    "Brush.DialogInfo" },
            { "Icon_Warning", "Brush.DialogWarning" },
            { "Icon_Error",   "Brush.DialogError" },
            { "Icon_Hint",    "Brush.DialogHint" }
        };

        private static readonly Regex MasterRegex = new Regex(
            $"({string.Join("|", MarkerConfigs.Keys.Select(Regex.Escape))}|\\*\\*|\\[Big\\]|\\[/Big\\]|\\r\\n|\\n)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly DialogConfig _config;
        private readonly string _rawMessage;

        /// <summary>
        /// 当前对话框已应用的 CAD 主题名称（"Dark" 或 "Light"）。
        /// </summary>
        public string ActiveThemeName { get; private set; }

        public CadMessageDialog(string title, string message, DialogConfig config)
        {
            InitializeComponent();

            _config = config ?? DialogConfig.Get(MessageBoxType.Info);
            _rawMessage = message ?? string.Empty;

            txtTitle.Text = !string.IsNullOrWhiteSpace(title) ? title : _config.DefaultTitle;

            // 1. 立即同步当前 AutoCAD 主题（COLORTHEME: 0=Dark, 1=Light）
            ApplyTheme();

            // 2. 渲染结构化富文本（主标题 + 内嵌说明卡片）
            RenderRichText(_rawMessage);
        }

        /// <summary>
        /// 应用指定的 AutoCAD 主题（"Dark" 或 "Light"；为空则自动读取 COLORTHEME 系统变量）。
        /// </summary>
        public void ApplyTheme(string explicitThemeName = null)
        {
            string targetTheme = string.IsNullOrWhiteSpace(explicitThemeName)
                ? CadThemes.CadThemeName()
                : (string.Equals(explicitThemeName, "Light", StringComparison.OrdinalIgnoreCase) || explicitThemeName == "1"
                    ? "Light"
                    : "Dark");

            ActiveThemeName = targetTheme;
            CadThemes.ApplyCadTheme(this, targetTheme);

            string statusBrushKey = !string.IsNullOrWhiteSpace(_config?.StatusBrushResourceKey)
                ? _config.StatusBrushResourceKey
                : "Brush.DialogInfo";

            HeaderAccentBar.SetResourceReference(Border.BackgroundProperty, statusBrushKey);
            iconPath.SetResourceReference(Shape.FillProperty, statusBrushKey);

            if (!string.IsNullOrWhiteSpace(_config?.IconResourceKey))
            {
                iconPath.Data = (TryFindResource(_config.IconResourceKey) as System.Windows.Media.Geometry)
                                ?? CadThemes.GetResource<System.Windows.Media.Geometry>(_config.IconResourceKey);
            }
        }

        private void RenderRichText(string rawMessage)
        {
            txtMessage.Inlines.Clear();
            txtHeadline.Text = string.Empty;
            txtHeadline.Visibility = Visibility.Collapsed;
            ContentCardBorder.Visibility = Visibility.Visible;

            if (string.IsNullOrWhiteSpace(rawMessage))
                return;

            var tokens = Tokenize(rawMessage);
            int startIndex = 0;

            // 若消息以 [Title] 开头，则提取第一行作为独立醒目主标题展示在卡片上方
            if (tokens.Count >= 2 &&
                tokens[0].Type == TokenType.Marker &&
                tokens[0].Value.Equals("[Title]", StringComparison.OrdinalIgnoreCase) &&
                tokens[1].Type == TokenType.Text)
            {
                txtHeadline.Text = tokens[1].Value.Trim();
                txtHeadline.Visibility = Visibility.Visible;

                string headlineBrushKey = !string.IsNullOrWhiteSpace(_config?.StatusBrushResourceKey)
                    ? _config.StatusBrushResourceKey
                    : "Brush.DialogWarning";
                txtHeadline.SetResourceReference(TextBlock.ForegroundProperty, headlineBrushKey);

                startIndex = 2;
                while (startIndex < tokens.Count && tokens[startIndex].Type == TokenType.LineBreak)
                {
                    startIndex++;
                }
            }

            if (startIndex >= tokens.Count)
            {
                ContentCardBorder.Visibility = Visibility.Collapsed;
                return;
            }

            var state = new TextState();
            bool isFirstTokenInCard = true;

            for (int i = startIndex; i < tokens.Count; i++)
            {
                var token = tokens[i];
                switch (token.Type)
                {
                    case TokenType.Marker:
                        if (token.Value.Equals("[Title]", StringComparison.OrdinalIgnoreCase))
                        {
                            state.IsTitleNext = true;
                        }
                        else if (MarkerConfigs.TryGetValue(token.Value, out string iconKey) && !string.IsNullOrEmpty(iconKey))
                        {
                            var icon = CreateInlineIcon(iconKey);
                            if (icon != null)
                            {
                                txtMessage.Inlines.Add(new InlineUIContainer(icon));
                            }
                            isFirstTokenInCard = false;
                        }
                        break;

                    case TokenType.BoldToggle:
                        state.IsBold = !state.IsBold;
                        break;

                    case TokenType.BigOpen:
                        state.IsBig = true;
                        break;

                    case TokenType.BigClose:
                        state.IsBig = false;
                        break;

                    case TokenType.LineBreak:
                        if (!isFirstTokenInCard)
                        {
                            txtMessage.Inlines.Add(new LineBreak());
                        }
                        break;

                    case TokenType.Text:
                        var run = new Run(token.Value);
                        if (state.IsTitleNext)
                        {
                            ApplyTitleStyle(run);
                            state.IsTitleNext = false;
                        }
                        else
                        {
                            if (state.IsBold) run.FontWeight = FontWeights.Bold;
                            if (state.IsBig) run.FontSize = txtMessage.FontSize + 3;
                        }

                        txtMessage.Inlines.Add(run);
                        isFirstTokenInCard = false;
                        break;
                }
            }
        }

        private List<MessageToken> Tokenize(string input)
        {
            var result = new List<MessageToken>();
            if (string.IsNullOrEmpty(input))
                return result;

            string[] parts = MasterRegex.Split(input);

            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part))
                    continue;

                string upperPart = part.ToUpperInvariant();

                if (upperPart == "**")
                    result.Add(new MessageToken { Type = TokenType.BoldToggle });
                else if (upperPart == "[BIG]")
                    result.Add(new MessageToken { Type = TokenType.BigOpen });
                else if (upperPart == "[/BIG]")
                    result.Add(new MessageToken { Type = TokenType.BigClose });
                else if (part == "\n" || part == "\r\n")
                    result.Add(new MessageToken { Type = TokenType.LineBreak });
                else if (MarkerConfigs.ContainsKey(upperPart))
                    result.Add(new MessageToken { Type = TokenType.Marker, Value = upperPart });
                else
                    result.Add(new MessageToken { Type = TokenType.Text, Value = part });
            }
            return result;
        }

        private void ApplyTitleStyle(Run run)
        {
            run.FontSize = txtMessage.FontSize + 2;
            run.FontWeight = FontWeights.Bold;
            run.SetResourceReference(TextElement.ForegroundProperty, "Brush.DialogWarning");
        }

        private FrameworkElement CreateInlineIcon(string iconResourceKey)
        {
            try
            {
                var geo = (TryFindResource(iconResourceKey) as System.Windows.Media.Geometry)
                          ?? CadThemes.GetResource<System.Windows.Media.Geometry>(iconResourceKey);
                if (geo == null)
                    return null;

                string brushKey = MarkerBrushKeys.TryGetValue(iconResourceKey, out string mappedBrushKey)
                    ? mappedBrushKey
                    : "Brush.DialogHint";

                var path = new Path
                {
                    Data = geo,
                    Width = 15,
                    Height = 15,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(0, 0, 5, -2.5)
                };
                path.SetResourceReference(Shape.FillProperty, brushKey);

                return path;
            }
            catch
            {
                return null;
            }
        }

        private void Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                try
                {
                    DragMove();
                }
                catch
                {
                    // 忽略非左键拖动异常
                }
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                this.DialogResult = true;
            }
            catch
            {
                // 非模态展示时忽略 DialogResult 异常
            }
            this.Close();
        }
    }

    public static class CadDialogService
    {
        /// <summary>
        /// 获取 AutoCAD 主窗口的句柄并绑定为当前 WPF Window 的 Owner。
        /// </summary>
        public static void SetOwner(this Window window)
        {
            if (window == null)
                return;

            try
            {
                IntPtr acadHandle = AcadApp.MainWindow.Handle;
                if (acadHandle != IntPtr.Zero)
                {
                    WindowInteropHelper helper = new WindowInteropHelper(window);
                    helper.Owner = acadHandle;
                }
            }
            catch
            {
                // 无头环境或句柄未就绪时安全忽略
            }
        }

        /// <summary>
        /// 显示 AutoCAD 2019 风格（自适应 Dark / Light 主题）的消息对话框。
        /// </summary>
        public static bool? ShowMessage(
            string message,
            string title = null,
            MessageBoxType type = MessageBoxType.Info)
        {
            var config = DialogConfig.Get(type);
            string finalTitle = title ?? config.DefaultTitle;

            var dialog = new CadMessageDialog(finalTitle, message, config);
            dialog.SetOwner();
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;

            return dialog.ShowDialog();
        }
    }

    public class DialogConfig
    {
        public string DefaultTitle { get; set; }
        public Brush DialogBrush { get; set; }
        public string StatusBrushResourceKey { get; set; }
        public string IconResourceKey { get; set; }

        public static DialogConfig Get(MessageBoxType type)
        {
            CadThemes.RefreshTheme();
            switch (type)
            {
                case MessageBoxType.Error:
                    return new DialogConfig
                    {
                        DefaultTitle = "错误",
                        StatusBrushResourceKey = "Brush.DialogError",
                        DialogBrush = CadThemes.GetResource<Brush>("Brush.DialogError"),
                        IconResourceKey = "Icon_Error"
                    };
                case MessageBoxType.Warning:
                    return new DialogConfig
                    {
                        DefaultTitle = "警告",
                        StatusBrushResourceKey = "Brush.DialogWarning",
                        DialogBrush = CadThemes.GetResource<Brush>("Brush.DialogWarning"),
                        IconResourceKey = "Icon_Warning"
                    };
                case MessageBoxType.Info:
                default:
                    return new DialogConfig
                    {
                        DefaultTitle = "提示",
                        StatusBrushResourceKey = "Brush.DialogInfo",
                        DialogBrush = CadThemes.GetResource<Brush>("Brush.DialogInfo"),
                        IconResourceKey = "Icon_Info"
                    };
            }
        }
    }
}
