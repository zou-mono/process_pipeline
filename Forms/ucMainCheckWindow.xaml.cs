using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using process_pipeline.Commands;
using process_pipeline.Core;
using process_pipeline.Themes;
using process_pipeline.Utils;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace process_pipeline.Forms
{
    /// <summary>
    /// Interaction logic for ucMainCheckWindow.xaml
    /// </summary>
    // UserControl类，纯View类
    public partial class ucMainCheckWindow : UserControl, IPaletteControl<Dictionary<ObjectId, ProblemItem>>
    {
        // 【移除】：所有属性、命令和逻辑（移到 MainViewModel 中）
        // 例如：TreeNodes、Rows、StatusText、ToggleTreeCommand 等
        private readonly MainCheckWindowViewModel _vm = new MainCheckWindowViewModel();

        public ucMainCheckWindow()
        {
            InitializeComponent();
            // 【移除】：mock 数据和命令初始化（现在在 MainViewModel 中）

            //CadThemes.ApplyCadTheme(this);
            this.DataContext = _vm;
        }

        // 【保留】：接口实现，用于外部更新数据
        public void UpdateData(Dictionary<ObjectId, ProblemItem> data)
        {
            // 由于 DataContext 是 MainViewModel，假设 MainViewModel 有 UpdateData 方法或属性
            // 你需要访问 ViewModel 来更新数据
            if (this.DataContext is MainCheckWindowViewModel viewModel)
            {
                // 示例：调用 ViewModel 的方法来更新 Rows（假设你添加了这个方法）
                viewModel.UpdateFromData(data);
            }
        }

        private void Doc_ImpliedSelectionChanged(object sender, EventArgs e)
        {
            _vm.OnImpliedSelectionChanged();
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            CadThemes.ApplyCadTheme(this);
        }
    }

    // viewModel类
    public class MainCheckWindowViewModel : INotifyPropertyChanged
    {
        private bool _isTreeCollapsed;
        private string _statusText = "就绪";
        private string _keyword = string.Empty;
        private GridLengthCache _cache = new GridLengthCache();

        //private double _leftPanelToggleX;  // GridSplitter距离左侧的偏移值

        public ObservableCollection<TreeNodeVm> TreeNodes { get; } = new ObservableCollection<TreeNodeVm>();
        public ObservableCollection<PipeRowVm> Rows { get; } = new ObservableCollection<PipeRowVm>();

        public ICommand ToggleTreeCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand ZoomToCommand { get; }
        public ICommand ExportCommand { get; }

        public MainCheckWindowViewModel()
        {
            ToggleTreeCommand = new RelayCommand(_ => ToggleTree());
            RefreshCommand = new RelayCommand(_ => StatusText = "已刷新");
            ZoomToCommand = new RelayCommand(_ => StatusText = "已定位");
            ExportCommand = new RelayCommand(_ => StatusText = "已导出");

            // 初始化 mock 数据（在实际应用中，可以从服务加载）
            InitializeMockData();
        }

        private void InitializeMockData()
        {
            TreeNodes.Add(new TreeNodeVm("全部图层")
            {
                Children = {
                    new TreeNodeVm("给水"),
                    new TreeNodeVm("排水"),
                    new TreeNodeVm("燃气")
                }
            });

            Rows.Add(new PipeRowVm("PL001", "给水", "待核查", 300, 1.20, "-"));
            Rows.Add(new PipeRowVm("PL002", "排水", "已修改", 400, 0.95, "管径已更新"));
            Rows.Add(new PipeRowVm("PL003", "燃气", "待复核", 250, 1.10, "高程疑似异常"));
        }

        public bool IsTreeCollapsed
        {
            get => _isTreeCollapsed;
            set
            {
                if (Set(ref _isTreeCollapsed, value))
                {
                    OnPropertyChanged(nameof(CollapseGlyph));
                }
            }
        }

        // 折叠时显示向右，展开时显示向左
        public string CollapseGlyph => IsTreeCollapsed ? "▶" : "◀";

        public string StatusText
        {
            get => _statusText;
            set => Set(ref _statusText, value);
        }

        public string Keyword
        {
            get => _keyword;
            set => Set(ref _keyword, value);
        }

        public string RowCountText => $"记录数：{Rows.Count}";

        private void ToggleTree()
        {
            //IsTreeCollapsed = !IsTreeCollapsed;
        }

        // 【新增】：从外部数据更新 ViewModel（用于 UpdateData 调用）
        // 假设 ProblemItem 有 Type、Status、Diameter、Height、Remark 等字段
        public void UpdateFromData(Dictionary<ObjectId, ProblemItem> data)
        {
            if (data == null) return;

            // 清空并重新填充 Rows（基于 ProblemItem 字段）
            Rows.Clear();
            int index = 1;
            foreach (var kvp in data.Where(p => !p.Value.IsFixed && !p.Value.PipeId.IsErased))
            {
                var item = kvp.Value;
                // 假设 ProblemItem 有这些属性；如果字段名不同，调整此处
                //Rows.Add(new PipeRowVm($"PL{index++:D3}", item.Type, item.Status, item.Diameter, item.Height, item.Remark));
            }

            // 更新状态文本（可选）
            StatusText = $"已更新 {Rows.Count} 条记录";

            // 触发属性通知，确保 WPF 绑定刷新
            OnPropertyChanged(nameof(Rows));
            OnPropertyChanged(nameof(RowCountText));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            if (name == nameof(Rows))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowCountText)));
            return true;
        }

        public void OnImpliedSelectionChanged()
        {

        }
    }

    public sealed class GridLengthCache
    {
        public double LastLeftWidth { get; set; } = 320;
    }

    public class TreeNodeVm
    {
        public string Name { get; set; }
        public ObservableCollection<TreeNodeVm> Children { get; set; } = new ObservableCollection<TreeNodeVm>();
        public TreeNodeVm(string name) => Name = name;
    }

    public class PipeRowVm
    {
        public string Id { get; }
        public string Type { get; }
        public string Status { get; }
        public double Diameter { get; }
        public double Height { get; }
        public string Remark { get; }

        public PipeRowVm(string id, string type, string status, double diameter, double height, string remark)
        {
            Id = id;
            Type = type;
            Status = status;
            Diameter = diameter;
            Height = height;
            Remark = remark;
        }
    }

    public class palCheckPipe : PaletteSetBase<ucMainCheckWindow, Dictionary<ObjectId, ProblemItem>>
    {
        // 子类单例（不变）
        private static readonly Lazy<palCheckPipe> _instance = new Lazy<palCheckPipe>(() => new palCheckPipe());
        public static palCheckPipe Instance => _instance.Value;

        //private Dictionary<ObjectId, ProblemItem> _currentProblems = new Dictionary<ObjectId, ProblemItem>();
        ////public IReadOnlyDictionary<ObjectId, ProblemItem> CurrentProblems => _currentProblems;
        
        // 和userControl的updateData保持一致
        //public IReadOnlyDictionary<ObjectId, ProblemItem> CurrentProblems => _currentControl?.CurrentProblems ?? new Dictionary<ObjectId, ProblemItem>();


        private palCheckPipe() : base() { }

        // 【新增】：实现抽象属性，提供唯一 GUID（生成新 GUID，避免与基类冲突）
        protected override Guid PaletteGuid => new Guid("9b3f8c21-7d12-4e5a-b98c-07f8e29d1a6c");

        // 实现其他抽象方法（不变）
        protected override string GetPaletteTitle() => "管线检查";
        protected override string GetPaletteName() => "PipeCheckPalette";
        protected override Dictionary<ObjectId, ProblemItem> GetInitialData() => new Dictionary<ObjectId, ProblemItem>();

        public override void RefreshData()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var service = new FlowArrowService(doc.Database, doc.Editor);
            var newProblems = service.RunChecker();
            if (newProblems != null)
            {
                UpdateData(newProblems);
            }
        }
    }
}
