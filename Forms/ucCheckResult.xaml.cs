using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Windows;
using process_pipeline.Commands;
using process_pipeline.Utils;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
    /// Interaction logic for UserControl1.xaml
    /// </summary>
    public partial class ucCheckResult : UserControl
    {
        // 核心数据字典
        private Dictionary<ObjectId, ProblemItem> _currentProblems = new Dictionary<ObjectId, ProblemItem>();
        
        // WPF 专用的可观察集合，绑定到界面后，增删数据会自动刷新 UI
        private ObservableCollection<ProblemItemViewModel> _observableList = new ObservableCollection<ProblemItemViewModel>();

        // 暴露给外部的只读字典
        public IReadOnlyDictionary<ObjectId, ProblemItem> CurrentProblems => _currentProblems;

        private Document doc;

        // 自定义事件
        public event EventHandler ProblemsChanged;

        public ucCheckResult(Dictionary<ObjectId, ProblemItem> problems)
        {
            InitializeComponent();

            doc = AcadApp.DocumentManager.MdiActiveDocument;
            _currentProblems = problems ?? new Dictionary<ObjectId, ProblemItem>();

            // 将 DataGrid 的数据源指向 ObservableCollection
            dgvProblems.ItemsSource = _observableList;

            // 订阅事件（和 WinForms 一样）
            ProblemsChanged += (s, e) => PopulateDataGridView();

            // 1. 初始化时应用 CAD 当前主题
            GraphicManager.ApplyCadTheme(this);

            // 初始化加载数据
            PopulateDataGridView();
        }

        // 【修改 3】: 数据填充逻辑优化
        // 为什么改：在 CAD 开发中，事件可能在非 UI 线程触发。WPF 对跨线程操作要求极严，
        // 必须使用 Dispatcher.Invoke 来确保在 UI 线程更新 ObservableCollection。
        private void PopulateDataGridView()
        {
            // 必须在 UI 线程操作 ObservableCollection
            Dispatcher.Invoke(() =>
            {
                _observableList.Clear();
                
                int index = 1; // 用于生成序号 NO

                // 先筛选：只保留未修复的原始项
                var filteredItems = _currentProblems
                    .Where(p => p.Value.IsFixed == false && !p.Value.PipeId.IsErased && !p.Value.PipeId.IsNull)
                    .ToList();
                    

                foreach (var kvp in filteredItems)
                {
                    ProblemItem originalItem = kvp.Value;
                    
                    _observableList.Add(new ProblemItemViewModel(index++, kvp.Value));
                }
            }); 
        }

        public void UpdateProblems(Dictionary<ObjectId, ProblemItem> newProblems)
        {
            _currentProblems = newProblems ?? new Dictionary<ObjectId, ProblemItem>();
            OnProblemsChanged(EventArgs.Empty);
        }

        protected virtual void OnProblemsChanged(EventArgs e)
        {
            ProblemsChanged?.Invoke(this, e);
        }

        private void dgvProblems_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {

        }

        // 将原先 SelectionChanged 里的 CAD 跳转逻辑提炼成独立方法
        private void ExecuteCadSelection()
        {
            // 确保有选中的行（WPF DataGrid 用 SelectedItems）
            if (dgvProblems.SelectedItems == null || dgvProblems.SelectedItems.Count == 0) 
                return;

            // 获取所有选中的 ProblemItem（WPF 直接强转绑定项，无需 DataBoundItem）
            var selectedItems = dgvProblems.SelectedItems
                .Cast<ProblemItemViewModel>()
                .Where(vm => vm != null && vm.OriginalItem != null)
                .Select(vm => vm.OriginalItem)
                .ToList();

            if (selectedItems.Count == 0) return;

            var objectIds = selectedItems.Select(p => p.PipeId).ToArray();
            SelectByHandleService sbh = new SelectByHandleService(doc.Database, doc.Editor);

            if (objectIds.Count() > 500)  // 太多了就不计算整体Extent
            {
                sbh.SelectByHandles(objectIds, false);   // 你的跳转选中函数
            }
            else
            {
                sbh.SelectByHandles(objectIds);   // 你的跳转选中函数
            }

            GraphicManager.ClearAuxiliaryGraphics();
            if (selectedItems.Count == 1)
            {
                if (selectedItems[0].Type == ProblemType.OneToMany)
                {
                    List<MatchItem> PossibleMatches = selectedItems[0].PossibleMatches;
                    GraphicManager.DrawAuxiliaryLines(PossibleMatches);
                }
            }
        }

        public static void RefreshDataGrid(Document doc, List<ObjectId> idsToProcess)
        {
            // 你的具体刷新代码...
            GraphicManager.ClearAuxiliaryGraphics();
            if (palCheckResult.Instance.IsVisible) { 
                var service = new FlowArrowService(doc.Database, doc.Editor, useEditor: false);
                service.Run(Properties.Settings.Default.taskFlowArrow, true, idsToProcess);

                foreach (ObjectId oid in idsToProcess) {
                    if (palCheckResult.Instance.CurrentProblems.ContainsKey(oid)) 
                    { 
                        ProblemItem _problem = palCheckResult.Instance.CurrentProblems[oid];
                        if (_problem.Type == ProblemType.OneToMany && !_problem.IsFixed) {
                            GraphicManager.DrawAuxiliaryLines(_problem.PossibleMatches);
                        }
                    }
                }
            }
        }

        private void btnRefresh_Click(object sender, RoutedEventArgs e)
        {
            var service = new FlowArrowService(doc.Database, doc.Editor, useEditor: false);
            service.Run(Properties.Settings.Default.taskFlowArrow, true);
        }

        private void btnReversePolyline_Click(object sender, RoutedEventArgs e)
        {
            ReversePolylineCommands rpc = new ReversePolylineCommands();
            rpc.Execute();

            PaletteRefreshManager.TriggerPaletteRefreshIfNeeded();
        }

        private void DataGridRow_Click(object sender, MouseButtonEventArgs e)
        {
            //// sender 直接就是被点击的那一行对象
            //var row = sender as DataGridRow;

            //if (row != null)
            //{
            //    // row.Item 就是你绑定到这一行的实体对象（比如 ProblemModel）
            //    //var data = row.Item as ProblemItemViewModel; 
            //        // 执行你的 CAD 任务
            //    ExecuteCadSelection();
            //}
        }

        private void dgvProblems_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 只有当用户真的选择了东西（且不是在初始化加载时）才触发
            if (!dgvProblems.IsLoaded || dgvProblems.SelectedItems.Count == 0) return;

            // 3. 统一执行 CAD 任务
            ExecuteCadSelection(); 
        }

        private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            //dgvProblems.UpdateLayout();
        }
    }

    public class palCheckResult : IDisposable
    {
        private static PaletteSet _paletteSet = null;
        
        // 【改造点1】：替换为 WPF 控件引用
        private ucCheckResult _currentControl;  

        private Dictionary<ObjectId, ProblemItem> _currentProblems = new Dictionary<ObjectId, ProblemItem>();
        public IReadOnlyDictionary<ObjectId, ProblemItem> CurrentProblems => _currentProblems;

        private PaletteRefreshManager _refreshManager; 

        private readonly Guid _paletteGuid = new Guid("7e8d4f9a-5b7c-4890-8a7b-123456789abc");

        private static readonly Lazy<palCheckResult> _instance = new Lazy<palCheckResult>(() => new palCheckResult());
        public static palCheckResult Instance => _instance.Value;

        private palCheckResult()
        {  
            _currentProblems = new Dictionary<ObjectId, ProblemItem>();
            AcadApp.DocumentManager.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
            
            // 假设你的 PaletteRefreshManager 依然有效
            _refreshManager = new PaletteRefreshManager(); 
        }

        private void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs e)
        {
            Dispose();
        }

        public void Show(Dictionary<ObjectId, ProblemItem> initialProblems)
        {
            var currentDoc = AcadApp.DocumentManager.MdiActiveDocument;
            if (currentDoc == null) return;

            Update(initialProblems);

            if (_paletteSet == null || _paletteSet.IsDisposed)
            {
                _paletteSet = new PaletteSet("管线箭头检查", "PipeCheckPalette", _paletteGuid)
                {
                    DockEnabled = DockSides.Left | DockSides.Right | DockSides.Top | DockSides.Bottom,
                    MinimumSize = new System.Drawing.Size(600, 400),
                    Size = new System.Drawing.Size(600, 700),
                    Visible = true
                };

                // 【改造点2】：WPF 控件没有 Dispose() 方法，直接置空让 GC 回收即可
                _currentControl = null;

                // 创建新的 WPF 控件
                _currentControl = new ucCheckResult(initialProblems);
                
                // 【改造点3】：使用 AddVisual 桥接 WPF 控件！！！
                _paletteSet.AddVisual("检查结果", _currentControl);

                _refreshManager.StartListening(currentDoc);
            }
            else 
            { 
                if (_currentControl != null)
                {
                    _currentControl.UpdateProblems(initialProblems);
                }
            }

            _paletteSet.Visible = true;
            _paletteSet.Activate(0);

            // 【改造点4】：删除了所有关于 AcadApp.Idle 和 Size 微调的 Hack 代码！
            // WPF 的布局系统（Measure/Arrange）非常可靠，放入 PaletteSet 后会自动完美撑开，不再需要强制刷新。

            _paletteSet.Focus();
        }

        public bool IsVisible
        {
            get => _paletteSet != null ? _paletteSet.Visible : false;
        }

        public void Hide()
        {
            if (_paletteSet != null && !_paletteSet.IsDisposed)
                _paletteSet.Visible = false;
        }

        public void MarkProblemFixed(HashSet<ObjectId> _pipeIds, bool _isFixed)
        {
            if (_pipeIds == null || _pipeIds.Count == 0) return;

            var validIds = _pipeIds.Where(id => !id.IsNull).ToHashSet();
            if (validIds.Count == 0) return;

            int fixedCount = 0;
            foreach (var p in _currentProblems)
            {
                if (!p.Value.IsFixed && validIds.Contains(p.Value.PipeId))
                {
                    p.Value.IsFixed = _isFixed;
                    fixedCount++;
                }
            }

            if (fixedCount > 0)
            {
                _currentControl?.UpdateProblems(_currentProblems);
            }
        }

        public void RefreshProblems()
        {
            Document Doc = AcadApp.DocumentManager.MdiActiveDocument;
            
            // 假设你的 FlowArrowService 依然有效
            var service = new FlowArrowService(Doc.Database, Doc.Editor);
            Dictionary<ObjectId, ProblemItem> newProblems = service.RunChecker();

            if (newProblems != null)
            {
                Update(newProblems);
            }
        }

        public void Update(Dictionary<ObjectId, ProblemItem> _newProblems)
        {
            _currentProblems = new Dictionary<ObjectId, ProblemItem>();

            if (_newProblems != null)
            {
                foreach (var _item in _newProblems)
                {
                    _currentProblems[_item.Key] = _item.Value; 
                } 
            }

            if (_currentControl != null)
            {
                // 【改造点5】：WPF 的跨线程调用使用 Dispatcher
                // 如果当前不在 UI 线程，则使用 Dispatcher.Invoke 调度
                if (!_currentControl.Dispatcher.CheckAccess())
                {
                    _currentControl.Dispatcher.Invoke(() => 
                    {
                        _currentControl.UpdateProblems(_currentProblems);
                    });
                }
                else
                {
                    _currentControl.UpdateProblems(_currentProblems);
                }
            }
        }

        public void Dispose()
        {
            try
            {
                // 【改造点6】：WPF 控件不需要也不能调用 Dispose()
                _currentControl = null;

                if (_paletteSet != null && !_paletteSet.IsDisposed)
                {
                    _paletteSet.Visible = false;
                    _paletteSet.Close();
                    _paletteSet.Dispose();
                }
                _paletteSet = null;

                _currentProblems = null;

                var currentDoc = AcadApp.DocumentManager.MdiActiveDocument;
                if (currentDoc != null && _refreshManager != null)
                {
                    _refreshManager.StopListening(currentDoc);
                }
            }
            catch (System.Exception ex)
            {
                AcadApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
                    $"\n关闭 PaletteSet 失败：{ex.Message}");
            }
        }
    }
}
