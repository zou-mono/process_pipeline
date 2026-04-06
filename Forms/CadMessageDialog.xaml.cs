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
        // 定义深蓝色 (AutoCAD 风格或标准深蓝)
        Brush highlightBrush = new SolidColorBrush(Color.FromRgb(0, 51, 153)); // 经典的深蓝色

        public CadMessageDialog(string title, string message, DialogConfig config)
        {
            InitializeComponent();
            txtTitle.Text = title;
            txtMessage.Text = message;

            // --- 核心修改：不再直接 txtMessage.Text = message ---
            ParseMessageWithIcons(message);

            // 应用颜色和图标
            var themeColor = (Color)ColorConverter.ConvertFromString(config.ColorHex);
            HeaderBar.Background = new SolidColorBrush(themeColor);
            iconPath.Fill = new SolidColorBrush(themeColor);
            //iconPath.Data = System.Windows.Media.Geometry.Parse(config.IconData);

            // 关键：从资源字典中根据 Key 查找 Geometry 对象
            // 这样即使路径变了，只要资源 Key 没变，代码就永远有效
            this.iconPath.Data = this.FindResource(config.IconResourceKey) as System.Windows.Media.Geometry;
        }

        private void ParseMessageWithIcons(string rawMessage)
        {
            txtMessage.Inlines.Clear();
            if (string.IsNullOrEmpty(rawMessage)) return;

            // 正则：匹配 [Info], [Warning], [Error] 等标签
            // 你可以根据需要增加更多标签
            string[] parts = Regex.Split(rawMessage, @"(\[Title\]|\[Hint\]|\[Info\]|\[Warning\]|\[Error\])");

            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part)) continue;

                // 根据标签映射到资源字典里的 Geometry Key
                string geoKey = null;

                if (part == "[Title]")
                {
                    // 标记接下来的文字需要加粗变大
                    // 我们不需要在这里做操作，只需在处理下一段文字时应用样式
                    continue;
                }

                // 检查前一个元素是不是 [Title]
                int currentIndex = Array.IndexOf(parts, part);
                bool isTitle = currentIndex > 0 && parts[currentIndex - 1] == "[Title]";

                // C# 7.3 使用传统的 switch 语句
                switch (part)
                {
                    case "[Info]":
                        geoKey = "Icon_Info";
                        break;
                    case "[Warning]":
                        geoKey = "Icon_Warning";
                        break;
                    case "[Error]":
                        geoKey = "Icon_Error";
                        break;
                    case "[Hint]":
                        geoKey = "Icon_Hint";
                        break;
                    default:
                        geoKey = null;
                        break;
                }

                if (geoKey != null)
                {
                    // 创建一个小图标控件
                    var path = new System.Windows.Shapes.Path
                    {
                        Data = this.FindResource(geoKey) as System.Windows.Media.Geometry,
                        Fill = highlightBrush, // 或者根据业务定颜色
                        Width = 18,
                        Height = 18,
                        Stretch = Stretch.Uniform,
                        // 关键点：Margin 的第四个参数（Bottom）
                        // 当图标比文字大时，通常需要一个负的 Bottom Margin 来让图标“沉”下去一点，
                        // 从而和文字的中轴线对齐。
                        Margin = new Thickness(2, 0, 5, -4) // 微调对齐文字基线
                    };
                    txtMessage.Inlines.Add(new InlineUIContainer(path));
                }
                else if (part != "[Title]") // 排除标签本身，处理纯文本
                {
                    Run run = new Run(part);

                    if (isTitle)
                    {
                        // --- 局部样式设置 ---
                        //run.FontWeight = FontWeights.Bold;       // 加粗
                        run.FontSize = txtMessage.FontSize + 4;  // 比默认大 4 号
                        run.Foreground = highlightBrush;
                        //run.Foreground = Brushes.White;         // 标题可以更亮一点
                    }

                    txtMessage.Inlines.Add(run);
                }
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
        public string ColorHex { get; set; }
        //public string IconData { get; set; } // SVG Path 数据
        public string IconResourceKey { get; set; } // 改为存储 Key 名字

        public static DialogConfig Get(MessageBoxType type)
        {
            switch (type)
            {
                case MessageBoxType.Error:
                    return new DialogConfig { 
                        DefaultTitle = "错误", 
                        ColorHex = "#F44336",
                        IconResourceKey = "Icon_Error"
                    };
                case MessageBoxType.Warning:
                    return new DialogConfig { 
                        DefaultTitle = "警告", 
                        ColorHex = "#FF9800",
                        IconResourceKey = "Icon_Warning"
                    };
                case MessageBoxType.Info:
                default:
                    return new DialogConfig { 
                        DefaultTitle = "提示", 
                        ColorHex = "#2196F3",
                        IconResourceKey = "Icon_Info"
                    };
            };
        }
    }

    public enum MessageBoxType {
        Error,
        Warning,
        Info
    }
}
