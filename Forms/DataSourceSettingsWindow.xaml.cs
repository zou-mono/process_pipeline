using System;
using System.Windows;
using process_pipeline.Themes;

namespace process_pipeline.Forms
{
    /// <summary>
    /// 多数据源核查配置窗口（Stage 6 Commit 6.3）。
    /// 纯 MVVM 视图，所有探测、校验与 Config.json 持久化逻辑均由 <see cref="DataSourceSettingsViewModel"/> 驱动。
    /// </summary>
    public partial class DataSourceSettingsWindow : Window
    {
        public DataSourceSettingsViewModel ViewModel { get; }

        public DataSourceSettingsWindow(DataSourceSettingsViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            DataContext = ViewModel;

            ViewModel.RequestClose += OnRequestClose;
        }

        private void OnRequestClose(bool confirmed)
        {
            try
            {
                DialogResult = confirmed;
            }
            catch
            {
                // 若窗口未通过 ShowDialog 模态打开，设置 DialogResult 可能抛 InvalidOperationException，安全忽略
            }
            Close();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            CadThemes.ApplyCadTheme(this);
        }

        protected override void OnClosed(EventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.RequestClose -= OnRequestClose;
            }
            base.OnClosed(e);
        }
    }
}
