using process_pipeline.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        public CadMessageDialog(string title, string message, DialogConfig config)
        {
            InitializeComponent();
            txtTitle.Text = title;
            txtMessage.Text = message;
        
            // 应用颜色和图标
            var themeColor = (Color)ColorConverter.ConvertFromString(config.ColorHex);
            HeaderBar.Background = new SolidColorBrush(themeColor);
            iconPath.Fill = new SolidColorBrush(themeColor);
            iconPath.Data = System.Windows.Media.Geometry.Parse(config.IconData);
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
        public string IconData { get; set; } // SVG Path 数据

        public static DialogConfig Get(MessageBoxType type)
        {
            switch (type)
            {
                case MessageBoxType.Error:
                    return new DialogConfig { 
                        DefaultTitle = "错误", 
                        ColorHex = "#F44336", 
                        IconData = "M12,2C6.47,2,2,6.47,2,12s4.47,10,10,10,10-4.47,10-10S17.53,2,12,2zm5,13.59L15.59,17,12,13.41,8.41,17,7,15.59,10.59,12,7,8.41,8.41,7,12,10.59,15.59,7,17,8.41,13.41,12,17,15.59z" 
                    };
                case MessageBoxType.Warning:
                    return new DialogConfig { 
                        DefaultTitle = "警告", 
                        ColorHex = "#FF9800", 
                        IconData = "M1,21H23L12,2L1,21ZM13,18H11V16H13V18ZM13,14H11V10H13V14Z" 
                    };
                case MessageBoxType.Info:
                default:
                    return new DialogConfig { 
                        DefaultTitle = "提示", 
                        ColorHex = "#2196F3", 
                        IconData = "M12,2C6.48,2,2,6.48,2,12s4.48,10,10,10,10-4.48,10-10S17.52,2,12,2zm1,15h-2v-6h2v6zm0-8h-2V7h2v2z" 
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
