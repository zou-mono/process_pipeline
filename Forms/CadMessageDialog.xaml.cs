using process_pipeline.Themes;
using process_pipeline.Utils;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Shapes;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace process_pipeline.Forms
{
    /// <summary>
    /// Interaction logic for UserControl1.xaml
    /// </summary>
    public partial class CadMessageDialog : Window
    {
        // 定义更细粒度的 Token 类型，用于顺序扫描
        private enum TokenType
        {
            Text,
            Marker,      // [Info], [Title] 等
            BoldToggle,  // **
            BigOpen,     // [Big]
            BigClose,    // [/Big]
            LineBreak    // \n
        }

        // 样式状态追踪
        private class TextState
        {
            public bool IsBold { get; set; }
            public bool IsBig { get; set; }
            public bool IsTitleNext { get; set; }
        }

        private sealed class MessageToken
        {
            public TokenType Type { get; set; }
            public string Value { get; set; } // Marker: [Info] / [Warning]... ; Text: 文本内容
        }

        // 标签到图标资源Key映射（可扩展）
        private static readonly Dictionary<string, string> MarkerConfigs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "[Title]",   null }, 
            { "[Info]", "Icon_Info" },
            { "[Warning]", "Icon_Warning" },
            { "[Error]", "Icon_Error" },
            { "[Hint]", "Icon_Hint" }
        };

        // 用于识别所有标签（含 Title）
        // 核心修复：将所有标签平铺在一个 () 捕获组内，去掉内部多余的 ()
        private static readonly Regex MasterRegex = new Regex(
            $"({string.Join("|", MarkerConfigs.Keys.Select(Regex.Escape))}|\\*\\*|\\[Big\\]|\\[/Big\\]|\\r\\n|\\n)", 
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public CadMessageDialog(string title, string message, DialogConfig config)
        {
            InitializeComponent();
            txtTitle.Text = title;
            txtMessage.Text = message;

            // 渲染富文本
            RenderRichText(message);

            // 应用颜色和图标
            Brush themeColor = config.DialogBrush;  // (Color)ColorConverter.ConvertFromString(config.ColorHex);
            HeaderBar.Background = themeColor ;
            iconPath.Fill = themeColor;
            //iconPath.Data = System.Windows.Media.Geometry.Parse(config.IconData);

            // 关键：从资源字典中根据 Key 查找 Geometry 对象
            // 这样即使路径变了，只要资源 Key 没变，代码就永远有效
            this.iconPath.Data = CadThemes.GetResource<System.Windows.Media.Geometry>(config.IconResourceKey);
        }

         private void RenderRichText(string rawMessage)
        {
            txtMessage.Inlines.Clear();
            if (string.IsNullOrWhiteSpace(rawMessage)) return;

            // 1. 拆分 Token
            var tokens = Tokenize(rawMessage);

            // 2. 顺序渲染
            var state = new TextState();

            foreach (var token in tokens)
            {
                switch (token.Type)
                {
                    case TokenType.Marker:
                        if (token.Value.Equals("[Title]", StringComparison.OrdinalIgnoreCase))
                            state.IsTitleNext = true;
                        else if (MarkerConfigs.TryGetValue(token.Value, out string iconKey) && !string.IsNullOrEmpty(iconKey))
                        {
                            var icon = CreateInlineIcon(iconKey);
                            if (icon != null) txtMessage.Inlines.Add(new InlineUIContainer(icon));
                        }
                        break;

                    case TokenType.BoldToggle:
                        state.IsBold = !state.IsBold; // 切换加粗状态
                        break;

                    case TokenType.BigOpen:
                        state.IsBig = true;
                        break;

                    case TokenType.BigClose:
                        state.IsBig = false;
                        break;

                    case TokenType.LineBreak:
                        // 优雅的换行间距：换行 + 零宽字符占位 + 换行
                        txtMessage.Inlines.Add(new LineBreak());
                        txtMessage.Inlines.Add(new Run("\u200B") { FontSize = 4 });
                        txtMessage.Inlines.Add(new LineBreak());
                        break;

                    case TokenType.Text:
                        var run = new Run(token.Value);
                        
                        // 样式叠加逻辑
                        if (state.IsTitleNext)
                        {
                            ApplyTitleStyle(run);
                            state.IsTitleNext = false; // 消费标题状态
                        }
                        else
                        {
                            // 自由叠加加粗和放大
                            if (state.IsBold) run.FontWeight = FontWeights.Bold;
                            if (state.IsBig) run.FontSize = txtMessage.FontSize + 4;
                        }
                        
                        txtMessage.Inlines.Add(run);
                        break;
                }
            }
        }

        private List<MessageToken> Tokenize(string input)
        {
            var result = new List<MessageToken>();
            if (string.IsNullOrEmpty(input)) return result;

            // Split 会把匹配到的标签也放进数组
            string[] parts = MasterRegex.Split(input);

            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part)) continue;

                // 规范化判断
                string upperPart = part.ToUpper();

                if (upperPart == "**") 
                    result.Add(new MessageToken { Type = TokenType.BoldToggle });
                else if (upperPart == "[BIG]") 
                    result.Add(new MessageToken { Type = TokenType.BigOpen });
                else if (upperPart == "[/BIG]") 
                    result.Add(new MessageToken { Type = TokenType.BigClose });
                else if (part == "\n" || part == "\r\n") 
                    result.Add(new MessageToken { Type = TokenType.LineBreak });
                else if (MarkerConfigs.ContainsKey(upperPart)) // 确保 MarkerConfigs 的 Key 也是大写或处理好大小写
                    result.Add(new MessageToken { Type = TokenType.Marker, Value = upperPart });
                else 
                    result.Add(new MessageToken { Type = TokenType.Text, Value = part });
            }
            return result;
        }

        // 提取样式设置，让代码更整洁
        private void ApplyTitleStyle(Run run)
        {
            run.FontSize = txtMessage.FontSize + 4;
            run.FontWeight = FontWeights.Bold;
            run.Foreground = CadThemes.GetResource<Brush>("Brush.DialogWarning");
        }

        private FrameworkElement CreateInlineIcon(string iconResourceKey)
        {
            try
            {
                var geo = this.FindResource(iconResourceKey) as System.Windows.Media.Geometry;
                if (geo == null)
                    return null;

                var path = new System.Windows.Shapes.Path
                {
                    Data = geo,
                    Fill = CadThemes.GetResource<Brush>("Brush.CadBlue"),
                    Width = 18,
                    Height = 18,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(2, 0, 5, -4) // 保留你原来的基线微调
                };

                return path;
            }
            catch
            {
                return null;
            }
        }

        private void Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true;
            this.Close();
        }
    }

    public static class CadDialogService
    {
        /// <summary>
        /// 获取 AutoCAD 主窗口的句柄并转换为 WPF 的 Window 对象（作为 Owner）
        /// </summary>
        public static void SetOwner(this Window window)
        {
            IntPtr acadHandle = AcadApp.MainWindow.Handle;
            WindowInteropHelper helper = new WindowInteropHelper(window);
            helper.Owner = acadHandle;
        }

        /// <summary>
        /// 显示一个美化的消息框
        /// </summary>
        public static bool? ShowMessage(
            string message, 
            string title = null, 
            MessageBoxType type = MessageBoxType.Info)
        {
            // 如果调用者没传 title，根据枚举获取默认值
            var config = DialogConfig.Get(type);
            string finalTitle = title ?? config.DefaultTitle;

            var dialog = new CadMessageDialog(finalTitle, message, config);

            //Autodesk.AutoCAD.ApplicationServices.Application.ShowModalWindow(dialog);

            // 确保在 CAD 线程中运行并挂载句柄
            dialog.SetOwner();
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;

            return dialog.ShowDialog();
        }
    }

    // 内部辅助类，用于管理不同类型的默认配置
    public class DialogConfig
    {
        public string DefaultTitle { get; set; }
        public Brush DialogBrush { get; set; }
        //public string IconData { get; set; } // SVG Path 数据
        public string IconResourceKey { get; set; } // 改为存储 Key 名字

        public static DialogConfig Get(MessageBoxType type)
        {
            switch (type)
            {
                case MessageBoxType.Error:
                    return new DialogConfig { 
                        DefaultTitle = "错误", 
                        DialogBrush = CadThemes.GetResource<Brush>("Brush.DialogError"),
                        IconResourceKey = "Icon_Error"
                    };
                case MessageBoxType.Warning:
                    return new DialogConfig { 
                        DefaultTitle = "警告", 
                        DialogBrush = CadThemes.GetResource<Brush>("Brush.DialogWarning"),
                        IconResourceKey = "Icon_Warning"
                    };
                case MessageBoxType.Info:
                default:
                    return new DialogConfig { 
                        DefaultTitle = "提示", 
                        DialogBrush = CadThemes.GetResource<Brush>("Brush.DialogInfo"),
                        IconResourceKey = "Icon_Info"
                    };
            };
        }
    }
}
