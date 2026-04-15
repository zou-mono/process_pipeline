using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Windows;
using process_pipeline.Commands;
using process_pipeline.Themes;
using process_pipeline.Utils;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

        // 在类定义中增加一个私有字典
        private Dictionary<ObjectId, ProblemItemViewModel> _idToViewModelMap = new Dictionary<ObjectId, ProblemItemViewModel>();

        private Document doc;
        private bool _isSyncing = false;  // 防止CAD图纸和DataGrid选中状态的“循环触发”冲突
        // 自定义事件
        public event EventHandler ProblemsChanged;

        private readonly DataGridCopyOptions _copyOptions = new DataGridCopyOptions
        {
            IncludeHeader = true,
            VisibleColumnsOnly = true,
            KeepDisplayOrder = true,
            KeepSelectionOrder = true
        };

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
            //GraphicManager.ApplyCadTheme(this);

            // 初始化加载数据
            PopulateDataGridView();

            // 2. 订阅系统变量改变事件
            AcadApp.SystemVariableChanged += AcadApp_SystemVariableChanged;

            doc.ImpliedSelectionChanged += Editor_ImpliedSelectionChanged;
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

                    var vm = new ProblemItemViewModel(index++, kvp.Value);
                    _observableList.Add(vm);

                    // 【新增】：存入索引字典
                    if (!kvp.Value.PipeId.IsNull)
                        _idToViewModelMap[kvp.Value.PipeId] = vm;
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
            // 使用封装的工具函数直接获取 RowHeader
            // 一次遍历，同时拿到 Header 和 Row
            var (rowHeader, row) = VisualTree.GetRowContext(e.OriginalSource);

            // 只有当点击的是行头 (header != null) 且 属于有效行 (row != null) 时触发
            if (rowHeader != null && row?.Item is ProblemItemViewModel vm)
            {
                ExecuteCadSelection(true); // 执行 CAD 定位逻辑
        
                e.Handled = true; 
                return;
            }

            // 如果 header == null 但 row != null，说明点在了单元格内部，可以写其他逻辑
        }

        // 将原先 SelectionChanged 里的 CAD 跳转逻辑提炼成独立方法
        private void ExecuteCadSelection(bool IsZoomTo = false)
        {
            if (_isSyncing) return;
            _isSyncing = true;

            try
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
                sbh.SelectByHandles(objectIds, IsZoomTo);  // 只选择，不跳转

                //if (objectIds.Count() > 500)  // 太多了就不计算整体Extent
                //{
                //    sbh.SelectByHandles(objectIds, false);
                //}
                //else
                //{
                //    sbh.SelectByHandles(objectIds);
                //}

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
            finally
            {
                _isSyncing = false;
            }
        }

        public static void RefreshDataGrid(Document doc, List<ObjectId> idsToProcess)
        {
            // 你的具体刷新代码...
            GraphicManager.ClearAuxiliaryGraphics();
            if (palCheckResult.Instance.IsVisible)
            {
                var service = new FlowArrowService(doc.Database, doc.Editor, useEditor: false);
                service.Run(Properties.Settings.Default.taskFlowArrow, true, idsToProcess);

                foreach (ObjectId oid in idsToProcess)
                {
                    if (palCheckResult.Instance.CurrentProblems.ContainsKey(oid))
                    {
                        ProblemItem _problem = palCheckResult.Instance.CurrentProblems[oid];
                        if (_problem.Type == ProblemType.OneToMany && !_problem.IsFixed)
                        {
                            GraphicManager.DrawAuxiliaryLines(_problem.PossibleMatches);
                        }
                    }
                }
            }
        }

        //private void btnRefresh_Click(object sender, RoutedEventArgs e)
        //{
        //    var service = new FlowArrowService(doc.Database, doc.Editor, useEditor: false);
        //    service.Run(Properties.Settings.Default.taskFlowArrow, true);
        //}

        //private void btnReversePolyline_Click(object sender, RoutedEventArgs e)
        //{
        //    ReversePolylineCommands rpc = new ReversePolylineCommands();
        //    rpc.Execute();

        //    PaletteRefreshManager.TriggerPaletteRefreshIfNeeded();
        //}

        private void DataGridRow_Click(object sender, MouseButtonEventArgs e)
        {

        }

        private void dgvProblems_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 3. 统一执行 CAD 任务
            ExecuteCadSelection(false);
        }

        private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            //dgvProblems.UpdateLayout();
        }

        private void CtxZoomToExtent_Click(object sender, RoutedEventArgs e)
        {
            // 3. 统一执行 CAD 任务
            ExecuteCadSelection(true);
        }

        private void CtxRefresh_Click(object sender, RoutedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                var service = new FlowArrowService(doc.Database, doc.Editor, useEditor: false);
                service.Run(Properties.Settings.Default.taskFlowArrow, true);
            });
        }

        private void CtxCopy_Click(object sender, RoutedEventArgs e)
        {
            // 只要 SelectedItems 有东西，说明用户选中了至少一行（通过行头点击）, 此时我们带上表头
            _copyOptions.IncludeHeader = (dgvProblems.SelectedItems != null && dgvProblems.SelectedItems.Count > 0);

            // 1. 【核心】立即手动关闭右键菜单
            // 很多时候是菜单这个窗口本身占用了剪贴板相关的 Win32 消息
            if (sender is System.Windows.Controls.MenuItem mi && mi.Parent is ContextMenu cm)
            {
                cm.IsOpen = false;
            }

            dgvProblems.Dispatcher.BeginInvoke(new Action(() =>
            {
                TryCopy(CopyFormat.Smart);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void CtxSelect_Click(object sender, RoutedEventArgs e)
        {
            // 从 Tag 中取出刚才存下的行对象
            if (dgvProblems.Tag is DataGridRow row)
            {
                // 执行反转逻辑
                // row.IsSelected 会自动同步 SelectedItems，从而配合你的复制逻辑
                row.IsSelected = !row.IsSelected;

                // 执行完后可以清空 Tag（可选）
                dgvProblems.Tag = null;

                ExecuteCadSelection(false);

                // 强制焦点回到 DataGrid，防止 AutoCAD 干扰
                dgvProblems.Focus();
            }
        }

        private void CtxClearSelected_Click(object sender, RoutedEventArgs e)
        { 
            dgvProblems.SelectedItems.Clear();
        }

        // 拦截 Ctrl+C 快捷键
        private void DataGrid_ExecuteCopy(object sender, ExecutedRoutedEventArgs e)
        {
            TryCopy(CopyFormat.Smart);
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            CadThemes.ApplyCadTheme(this);
        }

        private void AcadApp_SystemVariableChanged(object sender, Autodesk.AutoCAD.ApplicationServices.SystemVariableChangedEventArgs e)
        {
            // 检查改变的是否是主题变量
            if (e.Name.Equals("COLORTHEME", StringComparison.OrdinalIgnoreCase))
            {
                // 必须在 UI 线程执行
                this.Dispatcher.Invoke(() =>
                {
                    CadThemes.ApplyCadTheme(this);
                });
            }
        }

        // 实现点击行头选中整行
        private void dgvProblems_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 获取点击位置的元素
            DependencyObject dep = (DependencyObject)e.OriginalSource;

            DependencyObject temp = dep;
            DataGridRowHeader rowHeader = null;
            DataGridCell cell = null;

            // 检查点击的是不是行头 (RowHeader)
            while (temp != null && temp != dgvProblems)
            {
                if (temp is DataGridRowHeader rh) { rowHeader = rh; break; }
                if (temp is DataGridCell c) { cell = c; break; }
                temp = VisualTreeHelper.GetParent(temp);
            }

            // 3. 分情况处理逻辑
            if (rowHeader != null)
            {
                // --- 情况 A：点击的是【行头】 ---
                // 我们不拦截事件 (不设 e.Handled = true)，
                // 让 WPF 原生的 Extended 模式处理：支持拖拽多选、Shift/Ctrl 多选。
                return;
            }
            else if (cell != null)
            {
                // --- 情况 B：点击的是【单元格】 ---
                // 如果没有按住 Ctrl 或 Shift，我们强制执行“单选单元格”逻辑
                if (Keyboard.Modifiers == ModifierKeys.None)
                {
                    // 清除所有已选中的（包括之前选中的行或单元格）
                    dgvProblems.SelectedCells.Clear();

                    // 获取当前单元格所属的行
                    DataGridRow row = ItemsControl.ContainerFromElement(dgvProblems, cell) as DataGridRow;
                    if (row != null)
                    {
                        // 只选中当前这一个单元格
                        DataGridCellInfo cellInfo = new DataGridCellInfo(cell);
                        dgvProblems.SelectedCells.Add(cellInfo);

                        // 关键：确保 Row.IsSelected 为 false，防止行头亮起
                        // 在 CellOrRowHeader 模式下，只选单元格不会让 Row.IsSelected 变 true
                        row.IsSelected = false;
                    }

                    // 聚焦到 DataGrid 确保键盘操作有效
                    dgvProblems.Focus();

                    // 注意：这里由于我们手动处理了选中，如果不希望 WPF 默认的“点击单元格累加”逻辑介入，
                    // 我们可以不设 e.Handled，但要确保 SelectedCells.Clear() 已经执行。
                }
            }
        }

        private void dgvProblems_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            // 获取当前行索引并加1 
            e.Row.Header = (e.Row.GetIndex() + 1).ToString();
        }

        private void dgvProblems_Loaded(object sender, RoutedEventArgs e)
        {

        }

        private void dgvProblems_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 获取点击位置的元素
            DependencyObject dep = (DependencyObject)e.OriginalSource;

            // 向上查找点击的是哪一行
            while ((dep != null) && !(dep is DataGridRow))
            {
                dep = VisualTreeHelper.GetParent(dep);
            }

            UpdateMenuState();

            if (dep is DataGridRow row)
            {
                dgvProblems.Tag = row;

                // 核心逻辑：如果点击的这一行本身已经是选中状态，不做任何处理（让右键菜单正常弹出）
                // 如果点击的是未选中的行，拦截点击事件，不让 DataGrid 改变选中项
                if (!row.IsSelected)
                {
                    e.Handled = true; // 拦截事件，DataGrid 就不会收到点击信号，也就不会改变选中状态

                    // 如果你依然希望在未选中行上弹出右键菜单，可以手动触发：
                    // row.ContextMenu?.IsOpen = true;
                    var contextMenu = row.ContextMenu ?? dgvProblems.ContextMenu;
                    if (contextMenu != null)
                    {
                        contextMenu.PlacementTarget = row; // 设置弹出位置参考
                        contextMenu.IsOpen = true;
                    }
                }
            }
        }

        private void UpdateMenuState()
        {
            bool has = DataGridCopyHelper.HasSelection(dgvProblems);

            if (ctxCopy != null) ctxCopy.IsEnabled = has;

            has = (dgvProblems.SelectedItems != null && dgvProblems.SelectedItems.Count > 0);

            if (ctxZoomToExtent != null) ctxZoomToExtent.IsEnabled = has;
            if (ctxClearSelected != null) ctxClearSelected.IsEnabled = has;
        }

        private void TryCopy(CopyFormat fmt)
        {
            try
            {
                if (!DataGridCopyHelper.HasSelection(dgvProblems))
                    return;

                DataGridCopyHelper.CopyToClipboard(dgvProblems, fmt, _copyOptions);
            }
            catch (Exception ex)
            {
                //AcadApp.DocumentManager.MdiActiveDocument.Editor.WriteMessage($"\n复制失败: {ex.Message}");
                //MessageBox.Show($"复制失败：{ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void UserControl_MouseEnter(object sender, MouseEventArgs e)
        {
            this.Focus();
            dgvProblems.Focus();
        }

        // CAD图纸和DataGrid联动
        private void Editor_ImpliedSelectionChanged(object sender, EventArgs e)
        {
            if (_isSyncing) return;

            // 异步处理，避免阻塞 CAD 操作线程
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _isSyncing = true;
                try
                {
                    var ed = AcadApp.DocumentManager.MdiActiveDocument.Editor;
                    var psr = ed.SelectImplied();

                    // 1. 获取当前 CAD 选中的所有 ID (HashSet 查找速度 O(1))
                    var selectedIds = new HashSet<ObjectId>();
                    if (psr.Status == PromptStatus.OK)
                    {
                        foreach (var id in psr.Value.GetObjectIds()) selectedIds.Add(id);
                    }

                    // 2. 只有当选择集真的变了才操作 UI
                    // 批量更新 DataGrid 选中状态
                    dgvProblems.SelectedItems.Clear();

                    foreach (var id in selectedIds)
                    {
                        // 【核心】：从我们维护的字典里直接取现有的 ViewModel 实例
                        if (_idToViewModelMap.TryGetValue(id, out var vm))
                        {
                            dgvProblems.SelectedItems.Add(vm);
                        }
                    }

                    //// 可选：如果只选了一个，自动滚动到它
                    //if (dgvProblems.SelectedItems.Count > 0)
                    //{
                    //    dgvProblems.ScrollIntoView(dgvProblems.SelectedItems[0]);
                    //}
                }
                catch (Exception ex)
                {
                    // 静默处理或记录日志
                }
                finally
                {
                    _isSyncing = false;
                }
            }), System.Windows.Threading.DispatcherPriority.Background); // 使用 Background 优先级，不抢占渲染
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
