using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using process_pipeline.Commands;
using process_pipeline.Core;
using process_pipeline.Core.PreCheck;
using process_pipeline.Geometry;
using process_pipeline.Models;
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
using AcadDb = Autodesk.AutoCAD.DatabaseServices;

namespace process_pipeline.Forms
{
    /// <summary>
    /// Interaction logic for ucMainCheckWindow.xaml
    /// </summary>
    // UserControl类，纯View类
    public partial class ucMainCheckWindow : UserControl, IPaletteControl<Dictionary<ObjectId, CheckIssueItem>>
    {
        // 【移除】：所有属性、命令和逻辑（移到 MainViewModel 中）
        // 例如：TreeNodes、Rows、StatusText、ToggleTreeCommand 等
        private readonly MainCheckWindowViewModel _vm = new MainCheckWindowViewModel();
        private readonly DataGridCopyOptions _copyOptions = new DataGridCopyOptions
        {
            IncludeHeader = true,
            VisibleColumnsOnly = true,
            KeepDisplayOrder = true,
            KeepSelectionOrder = true
        };

        public ucMainCheckWindow()
        {
            InitializeComponent();
            // 【移除】：mock 数据和命令初始化（现在在 MainViewModel 中）

            //CadThemes.ApplyCadTheme(this);
            _vm.AttachGrid(dgIssues);
            if (dgIssues?.ContextMenu != null)
            {
                dgIssues.ContextMenu.DataContext = _vm;
            }
            this.DataContext = _vm;
        }

        // 【保留】：接口实现，用于外部更新数据
        public IReadOnlyDictionary<ObjectId, CheckIssueItem> CurrentIssues => _vm.CurrentIssues;

        /// <summary>
        /// IPaletteControl 标准接口实现：接收统一核查问题字典进行全量更新。
        /// </summary>
        public void UpdateData(Dictionary<ObjectId, CheckIssueItem> data)
        {
            if (this.DataContext is MainCheckWindowViewModel viewModel)
            {
                viewModel.UpdateFromIssues(data, isIncremental: false);
            }
        }

        /// <summary>
        /// 接收局部增量核查结果进行单行就地更新（不清空全表）。
        /// </summary>
        public void UpdateIncrementalData(Dictionary<ObjectId, CheckIssueItem> data)
        {
            if (this.DataContext is MainCheckWindowViewModel viewModel)
            {
                viewModel.UpdateFromIssues(data, isIncremental: true);
            }
        }

        /// <summary>
        /// 接收按唯一 IssueId 组织的核查问题列表进行全量或增量更新（Stage 2 Commit 2.3）。
        /// </summary>
        public void UpdateIssueItems(IEnumerable<CheckIssueItem> items, bool isIncremental = false)
        {
            if (this.DataContext is MainCheckWindowViewModel viewModel)
            {
                viewModel.UpdateFromIssueItems(items, isIncremental);
            }
        }

        private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is TreeNodeVm node)
            {
                _vm.SelectedTreeNode = node;
            }
        }

        private void DataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _vm.SyncSelectedRowsFromGrid();
        }

        private void DataGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (dgIssues == null) return;

            dgIssues.Tag = null;
            _vm.ContextTargetRow = null;

            DependencyObject dep = e.OriginalSource as DependencyObject;
            while (dep != null && !(dep is DataGridRow))
            {
                dep = VisualTreeHelper.GetParent(dep);
            }

            if (dep is DataGridRow row)
            {
                dgIssues.Tag = row;
                _vm.ContextTargetRow = row.Item as PipeRowVm;

                // 当用户已经多选了多行，或者右键点击在已选中的行上时，拦截默认右键行为以保持多选状态不丢失
                if (row.IsSelected || (dgIssues.SelectedItems != null && dgIssues.SelectedItems.Count > 1))
                {
                    e.Handled = true;
                    _vm.SyncSelectedRowsFromGrid(syncCadSelection: false);

                    var contextMenu = row.ContextMenu ?? dgIssues.ContextMenu;
                    if (contextMenu != null)
                    {
                        contextMenu.DataContext = _vm;
                        contextMenu.PlacementTarget = dgIssues;
                        contextMenu.IsOpen = true;
                    }
                }
                else
                {
                    // 单行未选中情况下右键：直接选中该行并同步右键菜单状态
                    dgIssues.SelectedItems.Clear();
                    row.IsSelected = true;
                    _vm.SelectedRow = row.Item as PipeRowVm;
                    _vm.SyncSelectedRowsFromGrid(syncCadSelection: true);
                    if (dgIssues.ContextMenu != null)
                    {
                        dgIssues.ContextMenu.DataContext = _vm;
                    }
                }
            }
            else
            {
                // 点击在表格空白处或表头时：保持菜单 DataContext 正确并刷新按钮可用性
                if (dgIssues.ContextMenu != null)
                {
                    dgIssues.ContextMenu.DataContext = _vm;
                }
                _vm.SyncSelectedRowsFromGrid(syncCadSelection: false);
            }
        }

        private void DataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // 仅当双击落在数据行上时才执行 CAD 定位，防止双击列宽分隔线、表头排序或滚动条时误触发
            var (_, row) = VisualTree.GetRowContext(e.OriginalSource);
            if (row != null && row.Item is PipeRowVm)
            {
                _vm.ZoomToSelectedRow(isZoomTo: true);
                e.Handled = true;
            }
        }

        private void DataGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            e.Row.Header = (e.Row.GetIndex() + 1).ToString();
        }

        private void DataGrid_ExecuteCopy(object sender, ExecutedRoutedEventArgs e)
        {
            TryCopySelection();
        }

        private void CtxCopy_Click(object sender, RoutedEventArgs e)
        {
            TryCopySelection();
        }

        private void CtxSelectRow_Click(object sender, RoutedEventArgs e)
        {
            if (dgIssues?.Tag is DataGridRow row)
            {
                row.IsSelected = !row.IsSelected;
                dgIssues.Tag = null;
                _vm.SyncSelectedRowsFromGrid();
                dgIssues.Focus();
            }
        }

        private void CtxClearSelected_Click(object sender, RoutedEventArgs e)
        {
            if (dgIssues == null) return;
            if (dgIssues.SelectionUnit != DataGridSelectionUnit.FullRow)
            {
                dgIssues.SelectedCells.Clear();
            }
            dgIssues.SelectedItems.Clear();
            _vm.ContextTargetRow = null;
            _vm.SelectedRow = null;
            _vm.SyncSelectedRowsFromGrid(syncCadSelection: false);
            _vm.ClearCadSelection();
        }

        private void TryCopySelection()
        {
            if (dgIssues == null) return;
            try
            {
                if (!DataGridCopyHelper.HasSelection(dgIssues)) return;
                _copyOptions.IncludeHeader = dgIssues.SelectedItems != null && dgIssues.SelectedItems.Count > 0;
                DataGridCopyHelper.CopyToClipboard(dgIssues, CopyFormat.Smart, _copyOptions);
                _vm.StatusText = "已复制选中内容到剪贴板";
            }
            catch
            {
                // 剪贴板占用时安全忽略
            }
        }

        // 【保留兼容】：支持旧版 ProblemItem 字典更新
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

        // ============================================================
        // Commit 2.2 & Commit 5.2：引入 ICollectionView 内存过滤与多维导航树过滤
        // 1. _rowsView：直接挂载在 Rows 默认视图上，当切换左侧树节点（核查类型 / 核查状态 / 管线图层）
        //    或输入搜索关键字时，只通过 _rowsView.Refresh() 过滤显示，绝不调用 Rows.Clear() 破坏原集合。
        // 2. _objectIdToRowMap：用于增量核查或人工修改时 O(1) 定位到具体行进行就地更新与归档。
        // ============================================================
        private string _selectedLayerFilter = "全部图层";
        private TreeNodeVm _selectedTreeNode;
        private readonly ICollectionView _rowsView;
        private readonly Dictionary<ObjectId, PipeRowVm> _objectIdToRowMap = new Dictionary<ObjectId, PipeRowVm>();
        private readonly Dictionary<string, PipeRowVm> _issueIdToRowMap = new Dictionary<string, PipeRowVm>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ObjectId, List<PipeRowVm>> _objectIdToRowsMap = new Dictionary<ObjectId, List<PipeRowVm>>();
        private Dictionary<ObjectId, CheckIssueItem> _currentIssues = new Dictionary<ObjectId, CheckIssueItem>();
        private DataGrid _grid;
        private PipeRowVm _selectedRow;
        private readonly List<PipeRowVm> _selectedRows = new List<PipeRowVm>();
        private bool _isSyncingSelection;
        private readonly HybridUndoRedoManager _fallbackUndoRedoManager = new HybridUndoRedoManager();

        /// <summary>
        /// 右键点击时鼠标落点所在的行（用于未多选时定位右键目标行）。
        /// </summary>
        public PipeRowVm ContextTargetRow { get; set; }

        public void AttachGrid(DataGrid grid)
        {
            _grid = grid;
        }

        /// <summary>
        /// 获取当前活动文档会话的混合撤销/重做管理器（若在非活动文档或示例模式下则回退至本地管理器）。
        /// </summary>
        public HybridUndoRedoManager ActiveUndoRedoManager =>
            DocumentSessionManager.GetActiveSession()?.UndoRedoManager ?? _fallbackUndoRedoManager;

        public IReadOnlyDictionary<ObjectId, CheckIssueItem> CurrentIssues => _currentIssues;

        //private double _leftPanelToggleX;  // GridSplitter距离左侧的偏移值

        public ObservableCollection<TreeNodeVm> TreeNodes { get; } = new ObservableCollection<TreeNodeVm>();
        public ObservableCollection<PipeRowVm> Rows { get; } = new ObservableCollection<PipeRowVm>();

        /// <summary>
        /// 顶部工具栏 `[单项核查]` 可选的五项标准核查规则（Stage 6 Commit 6.3）。
        /// </summary>
        public ObservableCollection<CheckRuleOptionItem> AvailableSingleRules { get; } =
            new ObservableCollection<CheckRuleOptionItem>(CheckPipelineCoordinator.GetAvailableRuleOptions());

        private CheckRuleOptionItem _selectedSingleRule;
        public CheckRuleOptionItem SelectedSingleRule
        {
            get => _selectedSingleRule;
            set => Set(ref _selectedSingleRule, value);
        }

        private string _dataSourceSummaryText = "数据源: 自动嗅探就绪";
        public string DataSourceSummaryText
        {
            get => _dataSourceSummaryText;
            set => Set(ref _dataSourceSummaryText, value);
        }

        /// <summary>
        /// 可选的测试环境自定义数据源弹窗委托（为空时走标准 WPF DataSourceSettingsWindow）。
        /// </summary>
        public Func<DataSourceSettingsViewModel, bool> CustomDataSourceDialogLauncher { get; set; }

        public ICommand OpenDataSourceSettingsCommand { get; }
        public ICommand RunFullCheckCommand { get; }
        public ICommand RunSingleCheckCommand { get; }
        public ICommand ToggleTreeCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand ZoomToCommand { get; }
        public ICommand ExportCommand { get; }
        public ICommand MarkModifiedCommand { get; }
        public ICommand MarkToRecheckCommand { get; }
        public ICommand MarkIgnoredCommand { get; }
        public ICommand ReverseSelectedPipeCommand { get; }
        public ICommand BindSelectedGroupCommand { get; }
        public ICommand UndoCommand { get; }
        public ICommand RedoCommand { get; }
        public ICommand CadSelectionChangedCommand { get; }

        public MainCheckWindowViewModel()
        {
            _selectedSingleRule = AvailableSingleRules.FirstOrDefault();

            OpenDataSourceSettingsCommand = new RelayCommand(_ => ExecuteOpenDataSourceSettings());
            RunFullCheckCommand = new RelayCommand(_ => ExecuteRunFullCheck());
            RunSingleCheckCommand = new RelayCommand(_ => ExecuteRunSingleCheck(), _ => SelectedSingleRule != null);
            ToggleTreeCommand = new RelayCommand(_ => ToggleTree());
            RefreshCommand = new RelayCommand(_ => RefreshByRule());
            ZoomToCommand = new RelayCommand(_ => ZoomToSelectedRow(isZoomTo: true));
            ExportCommand = new RelayCommand(_ => StatusText = "已导出");
            MarkModifiedCommand = new RelayCommand(_ => ChangeSelectedRowStatus(IssueReviewStatus.Resolved), _ => GetActionTargetRows().Count > 0);
            MarkToRecheckCommand = new RelayCommand(_ => ChangeSelectedRowStatus(IssueReviewStatus.ToRecheck), _ => GetActionTargetRows().Count > 0);
            MarkIgnoredCommand = new RelayCommand(_ => ChangeSelectedRowStatus(IssueReviewStatus.Ignored), _ => GetActionTargetRows().Count > 0);
            ReverseSelectedPipeCommand = new RelayCommand(_ => ReverseSelectedPipe(), _ => CanReverseSelectedPipe());
            BindSelectedGroupCommand = new RelayCommand(_ => BindSelectedPipeGroups(), _ => CanBindSelectedGroup());
            UndoCommand = new RelayCommand(_ => ExecuteUndo(), _ => ActiveUndoRedoManager.CanUndo);
            RedoCommand = new RelayCommand(_ => ExecuteRedo(), _ => ActiveUndoRedoManager.CanRedo);
            CadSelectionChangedCommand = new RelayCommand(_ => OnImpliedSelectionChanged());

            // 挂载默认集合视图过滤器（WPF DataGrid 绑定 Rows 时会自动使用此 DefaultView）
            _rowsView = CollectionViewSource.GetDefaultView(Rows);
            if (_rowsView != null)
            {
                _rowsView.Filter = FilterRow;
            }

            // Stage 6 Commit 6.6：移除初始化时的 Mock 假数据（PL001~PL003），保持初始空状态干净（Rows.Count == 0）
            RebuildNavigationTreeNodes();
            RefreshDataSourceSummaryText();
            StatusText = "就绪（请加载管线图纸后执行核查）";
        }

        private void InitializeMockData()
        {
            Rows.Add(new PipeRowVm(new CheckIssueItem
            {
                IssueId = "FlowArrow:PL001:0",
                RuleId = "FlowArrow",
                RuleName = "流向箭头核查",
                TargetHandle = "PL001",
                LayerName = "3-污水管-现状",
                Type = ProblemType.DirectionConflict,
                Status = IssueReviewStatus.Pending,
                Diameter = 300,
                Height = 1.20,
                Description = "管线和箭头方向不一致",
                Remark = "管线和箭头方向不一致"
            }));
            Rows.Add(new PipeRowVm(new CheckIssueItem
            {
                IssueId = "FlowArrow:PL002:0",
                RuleId = "FlowArrow",
                RuleName = "流向箭头核查",
                TargetHandle = "PL002",
                LayerName = "3-污水管-2025新建",
                Type = ProblemType.NoAdjacentItems,
                Status = IssueReviewStatus.ToRecheck,
                Diameter = 400,
                Height = 0.95,
                Description = "管线附近无匹配箭头",
                Remark = "待设计院确认流向"
            }));
            Rows.Add(new PipeRowVm(new CheckIssueItem
            {
                IssueId = "FlowArrow:PL003:0",
                RuleId = "FlowArrow",
                RuleName = "流向箭头核查",
                TargetHandle = "PL003",
                LayerName = "3-污水管-现状",
                Type = ProblemType.DirectionConflict,
                Status = IssueReviewStatus.Resolved,
                Diameter = 250,
                Height = 1.10,
                Description = "管线和箭头方向不一致",
                Remark = "已翻转管线流向（已修复）"
            }));

            RebuildNavigationTreeNodes();
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
            set
            {
                if (Set(ref _keyword, value))
                {
                    RefreshFilter();
                }
            }
        }

        /// <summary>
        /// 当前左侧导航树选中的节点（Commit 5.2：支持按“核查类型”、“核查状态”或“管线图层”过滤）。
        /// </summary>
        public TreeNodeVm SelectedTreeNode
        {
            get => _selectedTreeNode;
            set
            {
                if (Set(ref _selectedTreeNode, value))
                {
                    RefreshFilter();
                }
            }
        }

        /// <summary>
        /// 兼容旧版图层过滤属性。
        /// </summary>
        public string SelectedLayerFilter
        {
            get => _selectedLayerFilter;
            set
            {
                if (Set(ref _selectedLayerFilter, value ?? "全部图层"))
                {
                    RefreshFilter();
                }
            }
        }

        /// <summary>
        /// 当前表格选中的主焦点行（Commit 2.3 & Commit 5.5）。
        /// </summary>
        public PipeRowVm SelectedRow
        {
            get => _selectedRow;
            set
            {
                if (Set(ref _selectedRow, value))
                {
                    OnPropertyChanged(nameof(ReversePipeMenuVisibility));
                    OnPropertyChanged(nameof(ReversePipeMenuHeader));
                    NotifyUndoRedoStateChanged();
                }
            }
        }

        /// <summary>
        /// 从 DataGrid.SelectedItems 同步多选行集合，并按需触发 CAD 预选集批量高亮（Commit 5.5）。
        /// </summary>
        public void SyncSelectedRowsFromGrid(bool syncCadSelection = true)
        {
            _selectedRows.Clear();
            if (_grid?.SelectedItems != null && _grid.SelectedItems.Count > 0)
            {
                foreach (var item in _grid.SelectedItems)
                {
                    if (item is PipeRowVm rowVm && FilterRow(rowVm) && !_selectedRows.Contains(rowVm))
                    {
                        _selectedRows.Add(rowVm);
                    }
                }
            }

            if (_selectedRows.Count == 0 &&
                _grid != null &&
                _grid.SelectionUnit != DataGridSelectionUnit.FullRow &&
                _grid.SelectedCells != null &&
                _grid.SelectedCells.Count > 0)
            {
                foreach (var cell in _grid.SelectedCells)
                {
                    if (cell.Item is PipeRowVm rowVm && FilterRow(rowVm) && !_selectedRows.Contains(rowVm))
                    {
                        _selectedRows.Add(rowVm);
                    }
                }
            }

            OnPropertyChanged(nameof(ReversePipeMenuVisibility));
            OnPropertyChanged(nameof(ReversePipeMenuHeader));
            NotifyUndoRedoStateChanged();

            if (syncCadSelection && !_isSyncingSelection && _selectedRows.Count > 0)
            {
                ZoomToSelectedRow(isZoomTo: false);
            }
        }

        /// <summary>
        /// 获取当前右键动作的目标行集合（优先使用表格多选集合，未多选时回退至右键落点行或当前焦点行，并过滤已隐藏行）。
        /// </summary>
        public IReadOnlyList<PipeRowVm> GetActionTargetRows()
        {
            if (_selectedRows.Count > 0)
            {
                var visibleSelected = _selectedRows.Where(r => FilterRow(r)).ToList();
                if (visibleSelected.Count > 0)
                    return visibleSelected;
            }

            if (ContextTargetRow != null && FilterRow(ContextTargetRow))
                return new[] { ContextTargetRow };

            if (SelectedRow != null && FilterRow(SelectedRow))
                return new[] { SelectedRow };

            return Array.Empty<PipeRowVm>();
        }

        /// <summary>
        /// 上下文感知显隐（Commit 4.3 & Commit 5.5）：
        /// 仅当当前选中行集合中存在流向冲突问题（DirectionConflict 或 OneToMany）时，
        /// 右键菜单才显示“一键修复：翻转管线流向”，非此类问题则彻底隐藏（Collapsed）。
        /// </summary>
        public System.Windows.Visibility ReversePipeMenuVisibility =>
            CanReverseSelectedPipe() ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        /// <summary>
        /// 右键菜单“一键翻转”项的动态标题（单选时显示常规标题，多选时显示批量翻转条数）。
        /// </summary>
        public string ReversePipeMenuHeader
        {
            get
            {
                int count = GetReversibleTargetRows().Count;
                return count > 1
                    ? $"一键修复：批量翻转管线流向 (共 {count} 条)"
                    : "一键修复：翻转管线流向";
            }
        }

        /// <summary>
        /// 上下文感知显隐（Stage 3 Commit 3.2）：
        /// 当当前选中行集合中存在属于图属 Group 核查（MatchGroup）且具备候选属性块的记录时，
        /// 右键菜单显示“一键修复：绑定图属 Group 与 XData”。
        /// </summary>
        public System.Windows.Visibility BindGroupMenuVisibility =>
            CanBindSelectedGroup() ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        /// <summary>
        /// 右键菜单“一键绑定 Group 与 XData”项的动态标题。
        /// </summary>
        public string BindGroupMenuHeader
        {
            get
            {
                int count = GetBindableGroupTargetRows().Count;
                return count > 1
                    ? $"一键修复：批量绑定图属 Group 与 XData (共 {count} 组)"
                    : "一键修复：绑定图属 Group 与 XData";
            }
        }

        /// <summary>
        /// 右键菜单“撤销”项的动态标题（显示下一步即将撤销的具体动作描述）。
        /// </summary>
        public string UndoMenuHeader =>
            ActiveUndoRedoManager.CanUndo
                ? $"撤销：{ActiveUndoRedoManager.NextUndoDescription}"
                : "撤销核查操作";

        /// <summary>
        /// 右键菜单“重做”项的动态标题（显示下一步即将重做的具体动作描述）。
        /// </summary>
        public string RedoMenuHeader =>
            ActiveUndoRedoManager.CanRedo
                ? $"重做：{ActiveUndoRedoManager.NextRedoDescription}"
                : "重做核查操作";

        public string RowCountText
        {
            get
            {
                int totalCount = Rows.Count;
                int resolvedCount = Rows.Count(r => r.SourceIssue?.Status == IssueReviewStatus.Resolved);
                if (_rowsView == null)
                    return $"记录数：{totalCount}（已修复：{resolvedCount}）";

                int visibleCount = _rowsView.Cast<object>().Count();
                return $"显示：{visibleCount} / 总计：{totalCount}（已修复：{resolvedCount}）";
            }
        }

        /// <summary>
        /// 集合视图单行过滤谓词（Commit 5.2）：
        /// 结合左侧“核查类型 -> 核查状态 / 底部管线图层”导航树选中节点与顶部搜索框关键字进行内存级过滤。
        /// </summary>
        private bool FilterRow(object obj)
        {
            PipeRowVm row = obj as PipeRowVm;
            if (row == null) return false;

            IssueReviewStatus rowStatus = row.SourceIssue?.Status ?? IssueReviewStatus.Pending;

            // 1. 左侧树节点过滤
            if (_selectedTreeNode != null)
            {
                switch (_selectedTreeNode.FilterKind)
                {
                    case TreeFilterKind.RuleCategory:
                        // 点击一级【核查类型】节点时：展示该核查类型下所有未归档的活动记录（Status != Resolved）
                        if (!string.Equals(row.RuleName, _selectedTreeNode.RuleNameFilter, StringComparison.OrdinalIgnoreCase))
                            return false;
                        if (rowStatus == IssueReviewStatus.Resolved)
                            return false;
                        break;

                    case TreeFilterKind.RuleStatus:
                        // 点击二级【核查状态】节点（如“待核查”或“已修复”）时：按核查类型 + 具体状态精准匹配
                        if (!string.Equals(row.RuleName, _selectedTreeNode.RuleNameFilter, StringComparison.OrdinalIgnoreCase))
                            return false;
                        if (_selectedTreeNode.StatusFilter.HasValue && rowStatus != _selectedTreeNode.StatusFilter.Value)
                            return false;
                        break;

                    case TreeFilterKind.LayerRoot:
                        // 点击底部“按管线图层辅助筛选”根节点时：展示所有未归档的活动记录
                        if (rowStatus == IssueReviewStatus.Resolved)
                            return false;
                        break;

                    case TreeFilterKind.LayerItem:
                        // 点击具体图层子节点时：匹配所属图层
                        if (!string.Equals(row.Type, _selectedTreeNode.LayerFilter, StringComparison.OrdinalIgnoreCase))
                            return false;
                        break;

                    case TreeFilterKind.AllSummary:
                    default:
                        break;
                }
            }
            else
            {
                // 未选中任何树节点时，默认隐藏已修复项，仅展示待处理/活动项
                if (rowStatus == IssueReviewStatus.Resolved)
                    return false;

                if (!string.IsNullOrWhiteSpace(_selectedLayerFilter) &&
                    !string.Equals(_selectedLayerFilter, "全部图层", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(row.Type, _selectedLayerFilter, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }

            // 2. 关键字模糊匹配（支持匹配编号、核查类型、图层、状态、位置坐标、备注）
            if (!string.IsNullOrWhiteSpace(_keyword))
            {
                string kw = _keyword.Trim();
                bool matchId = row.Id != null && row.Id.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0;
                bool matchRule = row.RuleName != null && row.RuleName.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0;
                bool matchType = row.Type != null && row.Type.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0;
                bool matchStatus = row.Status != null && row.Status.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0;
                bool matchLoc = row.LocationText != null && row.LocationText.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0;
                bool matchRemark = row.Remark != null && row.Remark.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0;

                if (!matchId && !matchRule && !matchType && !matchStatus && !matchLoc && !matchRemark)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// 触发内存视图过滤刷新，清理已被隐藏行的选中残留，并同步底部状态栏记录数与连续行号。
        /// </summary>
        public void RefreshFilter()
        {
            _rowsView?.Refresh();

            if (ContextTargetRow != null && !FilterRow(ContextTargetRow))
            {
                ContextTargetRow = null;
            }
            if (SelectedRow != null && !FilterRow(SelectedRow))
            {
                SelectedRow = null;
            }

            if (_grid != null)
            {
                // 清理已被过滤隐藏的选中行，并异步刷新虚拟化可见行的连续行号（1, 2, 3...）
                _grid.Dispatcher.BeginInvoke(new Action(() =>
                {
                    for (int i = 0; i < _grid.Items.Count; i++)
                    {
                        if (_grid.ItemContainerGenerator.ContainerFromIndex(i) is DataGridRow rowContainer)
                        {
                            rowContainer.Header = (i + 1).ToString();
                        }
                    }
                    SyncSelectedRowsFromGrid(syncCadSelection: false);
                }), System.Windows.Threading.DispatcherPriority.Background);
            }

            OnPropertyChanged(nameof(RowCountText));
        }

        private void ToggleTree()
        {
            //IsTreeCollapsed = !IsTreeCollapsed;
        }

        // 【保留兼容】：从旧版 ProblemItem 字典更新 ViewModel
        public void UpdateFromData(Dictionary<ObjectId, ProblemItem> data)
        {
            if (data == null) return;

            var converted = new Dictionary<ObjectId, CheckIssueItem>();
            foreach (var kvp in data.Where(p => p.Value != null && !p.Value.PipeId.IsErased))
            {
                converted[kvp.Key] = CheckIssueItem.FromProblemItem(kvp.Value);
            }
            UpdateFromIssues(converted, isIncremental: false);
        }

        /// <summary>
        /// 使用统一核查问题模型（CheckIssueItem）更新列表，支持全量刷新、单行就地增量更新与已修复（Commit 5.2 & Stage 2 Commit 2.3）。
        /// 
        /// 设计说明：
        /// - 已修复（IssueReviewStatus.Resolved）的记录不再从 Rows 中物理删除，而是保留在集合中，
        ///   自动归档到对应核查类型的“已修复”节点下；仅当 CAD 图元被彻底 Erase 删除时才移除行。
        /// - Stage 2 Commit 2.3：底层改由 <see cref="UpdateFromIssueItems"/> 按唯一 IssueId 索引驱动，
        ///   支持同一根管线同时展示多条不同规则/不同折线段的问题，以及 TargetObjectId == ObjectId.Null 的缺失管段问题。
        /// </summary>
        public void UpdateFromIssues(Dictionary<ObjectId, CheckIssueItem> issues, bool isIncremental = false)
        {
            if (issues == null) return;

            var activeSession = DocumentSessionManager.GetActiveSession();
            if (!isIncremental && activeSession != null && activeSession.IssuesById.Count > issues.Count)
            {
                // 当会话中已通过 MergeRuleIssueItems 累积了多规则或同管线多子问题时，展示全部会话问题
                UpdateFromIssueItems(activeSession.GetAllIssueItems(), isIncremental: false);
                return;
            }

            int seq = 0;
            var itemList = new List<CheckIssueItem>(issues.Count);
            foreach (var kvp in issues)
            {
                if (kvp.Value == null) continue;
                if (kvp.Value.TargetObjectId.IsNull && !kvp.Key.IsNull)
                {
                    kvp.Value.TargetObjectId = kvp.Key;
                }
                DocumentCheckSession.EnsureIssueId(kvp.Value, kvp.Value.RuleId, seq++);
                itemList.Add(kvp.Value);
            }

            UpdateFromIssueItems(itemList, isIncremental);
        }

        /// <summary>
        /// 按唯一 IssueId 列表更新核查表格（Stage 2 Commit 2.3，支持单管线多规则/多折线段并存及 ObjectId.Null 缺失管段问题）。
        /// </summary>
        public void UpdateFromIssueItems(IEnumerable<CheckIssueItem> issueItems, bool isIncremental = false)
        {
            if (issueItems == null) return;

            var incomingList = issueItems.Where(x => x != null).ToList();

            if (!isIncremental)
            {
                _currentIssues.Clear();
                Rows.Clear();
                _objectIdToRowMap.Clear();
                _issueIdToRowMap.Clear();
                _objectIdToRowsMap.Clear();

                int seq = 0;
                foreach (CheckIssueItem issue in incomingList)
                {
                    if (!issue.TargetObjectId.IsNull && issue.TargetObjectId.IsErased)
                        continue;

                    string issueId = DocumentCheckSession.EnsureIssueId(issue, issue.RuleId, seq++);
                    if (_issueIdToRowMap.TryGetValue(issueId, out PipeRowVm duplicateRow))
                    {
                        duplicateRow.ApplyFromIssue(issue);
                        continue;
                    }

                    var rowVm = new PipeRowVm(issue);
                    Rows.Add(rowVm);
                    _issueIdToRowMap[issueId] = rowVm;

                    if (!issue.TargetObjectId.IsNull)
                    {
                        _currentIssues[issue.TargetObjectId] = issue;
                        _objectIdToRowMap[issue.TargetObjectId] = rowVm;
                        if (!_objectIdToRowsMap.TryGetValue(issue.TargetObjectId, out List<PipeRowVm> rowList))
                        {
                            rowList = new List<PipeRowVm>();
                            _objectIdToRowsMap[issue.TargetObjectId] = rowList;
                        }
                        rowList.Add(rowVm);
                    }
                }

                RebuildNavigationTreeNodes();
            }
            else
            {
                bool hasNewRuleOrLayer = false;
                int seq = 0;
                foreach (CheckIssueItem issue in incomingList)
                {
                    ObjectId targetId = issue.TargetObjectId;
                    string issueId = DocumentCheckSession.EnsureIssueId(issue, issue.RuleId, seq++);
                    bool isErased = !targetId.IsNull && targetId.IsErased;

                    if (!targetId.IsNull)
                    {
                        _currentIssues[targetId] = issue;
                    }

                    if (_issueIdToRowMap.TryGetValue(issueId, out PipeRowVm existingByIssueId))
                    {
                        if (isErased)
                        {
                            RemoveRowInternal(existingByIssueId, issueId, targetId);
                        }
                        else
                        {
                            existingByIssueId.ApplyFromIssue(issue);
                        }
                    }
                    else if (!targetId.IsNull &&
                             _objectIdToRowMap.TryGetValue(targetId, out PipeRowVm existingByOid) &&
                             string.Equals(existingByOid.SourceIssue?.RuleId, issue.RuleId, StringComparison.OrdinalIgnoreCase))
                    {
                        if (isErased)
                        {
                            RemoveRowInternal(existingByOid, existingByOid.SourceIssue?.IssueId, targetId);
                        }
                        else
                        {
                            existingByOid.ApplyFromIssue(issue);
                            _issueIdToRowMap[issueId] = existingByOid;
                        }
                    }
                    else if (!isErased)
                    {
                        var newRow = new PipeRowVm(issue);
                        Rows.Add(newRow);
                        _issueIdToRowMap[issueId] = newRow;

                        if (!targetId.IsNull)
                        {
                            _objectIdToRowMap[targetId] = newRow;
                            if (!_objectIdToRowsMap.TryGetValue(targetId, out List<PipeRowVm> rowList))
                            {
                                rowList = new List<PipeRowVm>();
                                _objectIdToRowsMap[targetId] = rowList;
                            }
                            rowList.Add(newRow);
                        }
                        hasNewRuleOrLayer = true;
                    }
                }

                if (hasNewRuleOrLayer || TreeNodes.Count == 0)
                {
                    RebuildNavigationTreeNodes();
                }
                else
                {
                    UpdateTreeCounts();
                }
            }

            var activeSession = DocumentSessionManager.GetActiveSession();
            if (activeSession != null)
            {
                foreach (var kvp in _currentIssues)
                {
                    activeSession.CurrentIssues[kvp.Key] = kvp.Value;
                }
                foreach (var kvp in _issueIdToRowMap)
                {
                    if (kvp.Value?.SourceIssue != null)
                    {
                        activeSession.IssuesById[kvp.Key] = kvp.Value.SourceIssue;
                    }
                }
            }

            RefreshFilter();
            RefreshDataSourceSummaryText();
            StatusText = $"已更新 {Rows.Count} 条核查记录";
        }

        private void RemoveRowInternal(PipeRowVm row, string issueId, ObjectId targetId)
        {
            if (row == null) return;
            Rows.Remove(row);
            if (!string.IsNullOrWhiteSpace(issueId))
            {
                _issueIdToRowMap.Remove(issueId);
            }
            if (!targetId.IsNull)
            {
                if (_objectIdToRowsMap.TryGetValue(targetId, out List<PipeRowVm> list) && list != null)
                {
                    list.Remove(row);
                    if (list.Count == 0)
                    {
                        _objectIdToRowsMap.Remove(targetId);
                        _objectIdToRowMap.Remove(targetId);
                    }
                    else
                    {
                        _objectIdToRowMap[targetId] = list[list.Count - 1];
                    }
                }
                else
                {
                    _objectIdToRowMap.Remove(targetId);
                }
            }
        }

        /// <summary>
        /// 构建“构想 1”二级导航树（Commit 5.2）：
        /// - 一级主节点：各【核查类型】（如“流向箭头核查”、“管段匹配核查”）；
        /// - 二级子节点：5 个带彩色矢量圆点的【核查状态】（待核查、待复核、已修改、已忽略、已修复）；
        /// - 底部节点：【按管线图层辅助筛选】及其各图层子节点。
        /// </summary>
        private void RebuildNavigationTreeNodes()
        {
            string prevRule = _selectedTreeNode?.RuleNameFilter;
            IssueReviewStatus? prevStatus = _selectedTreeNode?.StatusFilter;
            TreeFilterKind? prevKind = _selectedTreeNode?.FilterKind;
            string prevLayer = _selectedTreeNode?.LayerFilter;

            TreeNodes.Clear();

            var distinctRules = Rows
                .Select(r => r.RuleName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (distinctRules.Count == 0)
            {
                distinctRules.Add("流向箭头核查");
            }

            TreeNodeVm nodeToSelect = null;

            // 1. 构建每个【核查类型】一级节点及其 5 个【核查状态】二级子节点
            foreach (string ruleName in distinctRules)
            {
                var ruleNode = new TreeNodeVm(ruleName)
                {
                    FilterKind = TreeFilterKind.RuleCategory,
                    RuleNameFilter = ruleName,
                    IsExpanded = true
                };

                var statusDefinitions = new (string Label, IssueReviewStatus Status)[]
                {
                    ("待核查", IssueReviewStatus.Pending),
                    ("待复核", IssueReviewStatus.ToRecheck),
                    ("已忽略", IssueReviewStatus.Ignored),
                    ("已修复", IssueReviewStatus.Resolved)
                };

                foreach (var def in statusDefinitions)
                {
                    var statusNode = new TreeNodeVm(def.Label)
                    {
                        FilterKind = TreeFilterKind.RuleStatus,
                        RuleNameFilter = ruleName,
                        StatusFilter = def.Status,
                        StatusBrush = StatusColorPalette.GetBrush(def.Status)
                    };
                    ruleNode.Children.Add(statusNode);

                    if (prevKind == TreeFilterKind.RuleStatus &&
                        string.Equals(prevRule, ruleName, StringComparison.OrdinalIgnoreCase) &&
                        prevStatus == def.Status)
                    {
                        nodeToSelect = statusNode;
                    }
                }

                if (prevKind == TreeFilterKind.RuleCategory &&
                    string.Equals(prevRule, ruleName, StringComparison.OrdinalIgnoreCase))
                {
                    nodeToSelect = ruleNode;
                }

                // 默认选中第一个核查类型下的“待核查”节点（或该核查类型根节点）
                if (nodeToSelect == null)
                {
                    nodeToSelect = ruleNode;
                }

                TreeNodes.Add(ruleNode);
            }

            // 2. 构建底部【图层】节点
            var layerRoot = new TreeNodeVm("图层")
            {
                FilterKind = TreeFilterKind.LayerRoot,
                IsExpanded = false
            };

            var distinctLayers = Rows
                .Select(r => r.Type)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (string layer in distinctLayers)
            {
                var layerNode = new TreeNodeVm(layer)
                {
                    FilterKind = TreeFilterKind.LayerItem,
                    LayerFilter = layer
                };
                layerRoot.Children.Add(layerNode);

                if (prevKind == TreeFilterKind.LayerItem &&
                    string.Equals(prevLayer, layer, StringComparison.OrdinalIgnoreCase))
                {
                    layerRoot.IsExpanded = true;
                    nodeToSelect = layerNode;
                }
            }

            TreeNodes.Add(layerRoot);

            UpdateTreeCounts();

            if (nodeToSelect != null)
            {
                nodeToSelect.IsSelected = true;
                SelectedTreeNode = nodeToSelect;
            }
        }

        /// <summary>
        /// 实时重算左侧导航树所有节点的数量角标（不销毁树节点，保持展开与选中状态）。
        /// </summary>
        public void UpdateTreeCounts()
        {
            foreach (TreeNodeVm topNode in TreeNodes)
            {
                if (topNode.FilterKind == TreeFilterKind.RuleCategory)
                {
                    string ruleName = topNode.RuleNameFilter;
                    var ruleRows = Rows
                        .Where(r => string.Equals(r.RuleName, ruleName, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    int totalRuleCount = ruleRows.Count;
                    int activeRuleCount = ruleRows.Count(r => (r.SourceIssue?.Status ?? IssueReviewStatus.Pending) != IssueReviewStatus.Resolved);
                    topNode.CountText = $"({activeRuleCount}/{totalRuleCount})";

                    foreach (TreeNodeVm statusChild in topNode.Children)
                    {
                        if (statusChild.FilterKind == TreeFilterKind.RuleStatus && statusChild.StatusFilter.HasValue)
                        {
                            int cnt = ruleRows.Count(r => (r.SourceIssue?.Status ?? IssueReviewStatus.Pending) == statusChild.StatusFilter.Value);
                            statusChild.CountText = $"({cnt})";
                        }
                    }
                }
                else if (topNode.FilterKind == TreeFilterKind.LayerRoot)
                {
                    int totalLayerActive = Rows.Count(r => (r.SourceIssue?.Status ?? IssueReviewStatus.Pending) != IssueReviewStatus.Resolved);
                    topNode.CountText = $"({totalLayerActive})";

                    foreach (TreeNodeVm layerChild in topNode.Children)
                    {
                        if (layerChild.FilterKind == TreeFilterKind.LayerItem)
                        {
                            int cnt = Rows.Count(r => string.Equals(r.Type, layerChild.LayerFilter, StringComparison.OrdinalIgnoreCase));
                            layerChild.CountText = $"({cnt})";
                        }
                    }
                }
            }
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

        /// <summary>
        /// 人工判读状态修改（支持单条与多选批量标记，Commit 4.3 & Commit 5.5）：
        /// 通过 HybridUndoRedoManager 封装为可逆操作 ChangeIssueStatusOperation，
        /// 支持单行或多行就地刷新以及单步 Ctrl+Z / Ctrl+Y 撤销与重做。
        /// </summary>
        public void ChangeSelectedRowStatus(IssueReviewStatus newStatus)
        {
            var targetRows = GetActionTargetRows()
                .Where(r => r?.SourceIssue != null)
                .ToList();
            if (targetRows.Count == 0) return;

            var batchItems = new List<(CheckIssueItem Before, CheckIssueItem After, Action<CheckIssueItem> ApplyAction)>(targetRows.Count);
            foreach (PipeRowVm row in targetRows)
            {
                PipeRowVm capturedRow = row;
                CheckIssueItem beforeSnapshot = capturedRow.SourceIssue.CloneSnapshot();
                CheckIssueItem afterSnapshot = capturedRow.SourceIssue.CloneSnapshot();
                afterSnapshot.Status = newStatus;
                batchItems.Add((beforeSnapshot, afterSnapshot, snapshot => ApplyIssueSnapshotToRow(capturedRow, snapshot)));
            }

            var op = new ChangeIssueStatusOperation(batchItems);
            ActiveUndoRedoManager.ExecuteOperation(op);
            NotifyUndoRedoStateChanged();

            if (targetRows.Count == 1)
            {
                StatusText = $"已将管线 [{targetRows[0].Id}] 标记为：{targetRows[0].Status}";
            }
            else
            {
                StatusText = $"已批量将 {targetRows.Count} 条管线标记为：{targetRows[0].Status}";
            }
        }

        /// <summary>
        /// 筛选当前目标行集合中所有适用于“翻转管线流向”的记录（属于流向核查且为 DirectionConflict 或 OneToMany）。
        /// </summary>
        public IReadOnlyList<PipeRowVm> GetReversibleTargetRows()
        {
            return GetActionTargetRows()
                .Where(r => r?.SourceIssue != null &&
                            !string.Equals(r.SourceIssue.RuleId, MatchGroupService.DefaultRuleId, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(r.SourceIssue.RuleId, PlanDataCheckService.DefaultRuleId, StringComparison.OrdinalIgnoreCase) &&
                            (r.SourceIssue.Type == ProblemType.DirectionConflict ||
                             r.SourceIssue.Type == ProblemType.OneToMany))
                .ToList();
        }

        /// <summary>
        /// 判断当前选中行集合中是否存在适用“翻转管线流向”修复操作的记录。
        /// </summary>
        public bool CanReverseSelectedPipe()
        {
            return GetReversibleTargetRows().Count > 0;
        }

        /// <summary>
        /// 筛选当前目标行集合中所有适用于“绑定图属 Group 与 XData”的记录（Stage 3 Commit 3.2）。
        /// </summary>
        public IReadOnlyList<PipeRowVm> GetBindableGroupTargetRows()
        {
            return GetActionTargetRows()
                .Where(r => r?.SourceIssue != null &&
                            string.Equals(r.SourceIssue.RuleId, MatchGroupService.DefaultRuleId, StringComparison.OrdinalIgnoreCase) &&
                            !r.TargetObjectId.IsNull &&
                            r.SourceIssue.RelatedObjectIds != null &&
                            r.SourceIssue.RelatedObjectIds.Count > 0 &&
                            !r.SourceIssue.RelatedObjectIds[0].IsNull)
                .ToList();
        }

        /// <summary>
        /// 判断当前选中行集合中是否存在可绑定图属 Group 与 XData 的记录。
        /// </summary>
        public bool CanBindSelectedGroup()
        {
            return GetBindableGroupTargetRows().Count > 0;
        }

        /// <summary>
        /// 一键将当前选中管线与配对属性块绑定为 CAD Group 并写入 XData（支持单选与多选批量，Stage 3 Commit 3.2）。
        /// 封装为单事务可逆复合操作 BindPipeGroupOperation，支持按一次 Ctrl+Z 整批撤销。
        /// </summary>
        public void BindSelectedPipeGroups()
        {
            var bindableRows = GetBindableGroupTargetRows();
            if (bindableRows.Count == 0)
                return;

            var validRows = bindableRows
                .Where(r => !r.TargetObjectId.IsNull && r.TargetObjectId.IsValid && !r.TargetObjectId.IsErased &&
                            r.SourceIssue.RelatedObjectIds != null &&
                            r.SourceIssue.RelatedObjectIds.Count > 0 &&
                            r.SourceIssue.RelatedObjectIds[0].IsValid &&
                            !r.SourceIssue.RelatedObjectIds[0].IsErased)
                .ToList();

            if (validRows.Count == 0)
            {
                StatusText = "选中的记录未关联有效 CAD 管线或属性块图元，无法绑定 Group";
                return;
            }

            var rowByPipeId = new Dictionary<ObjectId, PipeRowVm>(validRows.Count);
            var batchItems = new List<(ObjectId PipeId, ObjectId AttachmentId, CheckIssueItem BeforeSnapshot)>(validRows.Count);

            foreach (PipeRowVm row in validRows)
            {
                ObjectId pipeId = row.TargetObjectId;
                ObjectId attId = row.SourceIssue.RelatedObjectIds[0];
                rowByPipeId[pipeId] = row;
                batchItems.Add((pipeId, attId, row.SourceIssue.CloneSnapshot()));
            }

            var op = new BindPipeGroupOperation(
                batchItems,
                (restoredList, isUndo) =>
                {
                    if (restoredList != null)
                    {
                        var affectedIds = new List<ObjectId>(restoredList.Count * 2);
                        foreach (var item in restoredList)
                        {
                            affectedIds.Add(item.PipeId);
                            if (item.Snapshot != null && rowByPipeId.TryGetValue(item.PipeId, out PipeRowVm targetRow))
                            {
                                if (targetRow.SourceIssue?.RelatedObjectIds != null)
                                {
                                    affectedIds.AddRange(targetRow.SourceIssue.RelatedObjectIds);
                                }
                                ApplyIssueSnapshotToRow(targetRow, item.Snapshot, refreshTreeAndFilter: false);
                            }
                        }

                        PaletteRefreshManager.SuppressIds(affectedIds);
                        GraphicManager.ClearAuxiliaryGraphics();
                        UpdateTreeCounts();
                        RefreshFilter();
                    }
                });

            ActiveUndoRedoManager.ExecuteOperation(op);
            NotifyUndoRedoStateChanged();

            if (validRows.Count == 1)
            {
                StatusText = $"已绑定管线 [{validRows[0].Id}] 的 Group 与 XData，并归档至【已修复】";
            }
            else
            {
                StatusText = $"已批量绑定 {validRows.Count} 组管线 Group 与 XData，并归档至【已修复】";
            }
        }

        /// <summary>
        /// 一键翻转当前选中管线（支持单选与多选批量，Commit 4.3 & Commit 5.5）：
        /// 封装为单事务可逆复合操作 ReverseCurveOperation，翻转后自动归档，按一次 Ctrl+Z 即可整批撤销。
        /// </summary>
        public void ReverseSelectedPipe()
        {
            var reversibleRows = GetReversibleTargetRows();
            if (reversibleRows.Count == 0)
                return;

            var validRows = reversibleRows
                .Where(r => !r.TargetObjectId.IsNull && r.TargetObjectId.IsValid && !r.TargetObjectId.IsErased)
                .ToList();

            if (validRows.Count == 0)
            {
                StatusText = "选中的管线未关联有效 CAD 图元，无法翻转";
                return;
            }

            var rowById = new Dictionary<ObjectId, PipeRowVm>(validRows.Count);
            var batchItems = new List<(ObjectId CurveId, CheckIssueItem BeforeSnapshot)>(validRows.Count);
            foreach (PipeRowVm row in validRows)
            {
                rowById[row.TargetObjectId] = row;
                batchItems.Add((row.TargetObjectId, row.SourceIssue.CloneSnapshot()));
            }

            var op = new ReverseCurveOperation(
                batchItems,
                (restoredList, isUndo) =>
                {
                    if (restoredList != null)
                    {
                        foreach (var item in restoredList)
                        {
                            if (item.Snapshot != null && rowById.TryGetValue(item.CurveId, out PipeRowVm targetRow))
                            {
                                ApplyIssueSnapshotToRow(targetRow, item.Snapshot, refreshTreeAndFilter: false);
                            }
                        }
                        // 抑制由本操作自身事务引发的后台 Idle 二次重算，防止覆盖 Undo/Redo 恢复的精确快照
                        PaletteRefreshManager.SuppressIds(restoredList.Select(x => x.CurveId));
                        UpdateTreeCounts();
                        RefreshFilter();
                    }
                });

            ActiveUndoRedoManager.ExecuteOperation(op);
            NotifyUndoRedoStateChanged();

            if (validRows.Count == 1)
            {
                StatusText = $"已翻转管线 [{validRows[0].Id}] 流向，并归档至【已修复】";
            }
            else
            {
                StatusText = $"已批量翻转 {validRows.Count} 条管线流向，并归档至【已修复】";
            }
        }

        /// <summary>
        /// 执行核查操作撤销（Ctrl+Z 或右键菜单触发）。
        /// </summary>
        public void ExecuteUndo()
        {
            if (!ActiveUndoRedoManager.CanUndo) return;

            IUserOperation undoneOp = ActiveUndoRedoManager.Undo();
            NotifyUndoRedoStateChanged();
            if (undoneOp != null)
            {
                StatusText = $"已撤销：{undoneOp.Description}";
            }
        }

        /// <summary>
        /// 执行核查操作重做（Ctrl+Y 或右键菜单触发）。
        /// </summary>
        public void ExecuteRedo()
        {
            if (!ActiveUndoRedoManager.CanRedo) return;

            IUserOperation redoneOp = ActiveUndoRedoManager.Redo();
            NotifyUndoRedoStateChanged();
            if (redoneOp != null)
            {
                StatusText = $"已重做：{redoneOp.Description}";
            }
        }

        private void ApplyIssueSnapshotToRow(PipeRowVm row, CheckIssueItem snapshot, bool refreshTreeAndFilter = true)
        {
            if (row == null || snapshot == null) return;

            row.ApplyFromIssue(snapshot);
            var activeSession = DocumentSessionManager.GetActiveSession();

            if (!string.IsNullOrWhiteSpace(snapshot.IssueId) && activeSession != null)
            {
                activeSession.IssuesById[snapshot.IssueId] = snapshot;
            }

            if (!snapshot.TargetObjectId.IsNull)
            {
                _currentIssues[snapshot.TargetObjectId] = snapshot;
                if (activeSession != null)
                {
                    activeSession.CurrentIssues[snapshot.TargetObjectId] = snapshot;
                }
            }

            if (refreshTreeAndFilter)
            {
                UpdateTreeCounts();
                RefreshFilter();
            }
        }

        private void NotifyUndoRedoStateChanged()
        {
            OnPropertyChanged(nameof(UndoMenuHeader));
            OnPropertyChanged(nameof(RedoMenuHeader));
            OnPropertyChanged(nameof(ReversePipeMenuVisibility));
            OnPropertyChanged(nameof(ReversePipeMenuHeader));
            OnPropertyChanged(nameof(BindGroupMenuVisibility));
            OnPropertyChanged(nameof(BindGroupMenuHeader));
            CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>
        /// 选中或缩放定位到当前选中行（单行或多行）对应的全部 CAD 实体，并按需绘制一对多瞬态辅助线（Commit 5.5 & Stage 2 Commit 2.3）。
        /// 当记录对应上版缺失管段（TargetObjectId == ObjectId.Null）但包含有效坐标时，支持直接按代表点坐标缩放 CAD 视口。
        /// </summary>
        public void ZoomToSelectedRow(bool isZoomTo)
        {
            if (_isSyncingSelection) return;

            var targetRows = GetActionTargetRows();
            if (targetRows.Count == 0) return;

            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var validIds = targetRows
                .Select(r => r.TargetObjectId)
                .Where(id => !id.IsNull && id.IsValid && !id.IsErased)
                .Distinct()
                .ToArray();

            if (validIds.Length == 0)
            {
                var firstWithLoc = targetRows.FirstOrDefault(r =>
                    r?.SourceIssue != null &&
                    (Math.Abs(r.SourceIssue.Location.X) > 1e-3 || Math.Abs(r.SourceIssue.Location.Y) > 1e-3));

                if (isZoomTo && firstWithLoc != null)
                {
                    try
                    {
                        var loc = firstWithLoc.SourceIssue.Location;
                        double halfSpan = 25.0;
                        var ext = new Extents3d(
                            new Autodesk.AutoCAD.Geometry.Point3d(loc.X - halfSpan, loc.Y - halfSpan, 0),
                            new Autodesk.AutoCAD.Geometry.Point3d(loc.X + halfSpan, loc.Y + halfSpan, 0));
                        doc.Editor.ZoomToExtents(ext);
                        StatusText = $"已按坐标定位到缺失/外部记录 [{firstWithLoc.Id}] ({firstWithLoc.LocationText})";
                        return;
                    }
                    catch
                    {
                        // 忽略视口缩放异常
                    }
                }

                StatusText = $"管线 [{targetRows[0].Id}] 为示例数据或未关联当前图纸图元";
                return;
            }

            _isSyncingSelection = true;
            try
            {
                var selector = new SelectByHandleService(doc.Database, doc.Editor);
                selector.SelectByHandles(validIds, isZoomTo);

                GraphicManager.ClearAuxiliaryGraphics();
                if (targetRows.Count == 1)
                {
                    var issue = targetRows[0].SourceIssue;
                    if (issue != null &&
                        issue.PossibleMatches != null &&
                        issue.PossibleMatches.Count > 0)
                    {
                        GraphicManager.DrawAuxiliaryLines(issue.PossibleMatches);
                    }
                    StatusText = isZoomTo ? $"已定位到管线 [{targetRows[0].Id}]" : $"已选中管线 [{targetRows[0].Id}]";
                }
                else
                {
                    StatusText = isZoomTo
                        ? $"已批量定位到 {validIds.Length} 条管线"
                        : $"已批量选中 {validIds.Length} 条管线";
                }
            }
            finally
            {
                _isSyncingSelection = false;
            }
        }

        /// <summary>
        /// 清空 CAD 图面当前的预选集与瞬态辅助线。
        /// </summary>
        public void ClearCadSelection()
        {
            if (_isSyncingSelection) return;
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            _isSyncingSelection = true;
            try
            {
                GraphicManager.ClearAuxiliaryGraphics();
                doc.Editor.SetImpliedSelection(Array.Empty<ObjectId>());
                StatusText = "已清空选择";
            }
            catch
            {
                // 忽略选择集清理异常
            }
            finally
            {
                _isSyncingSelection = false;
            }
        }

        /// <summary>
        /// 刷新底部状态栏与工具栏按钮提示中的数据源摘要文本（Stage 6 Commit 6.3）。
        /// </summary>
        public void RefreshDataSourceSummaryText(AcadDb.Database targetDb = null)
        {
            var db = targetDb ?? AcadApp.DocumentManager?.MdiActiveDocument?.Database;
            if (db == null)
            {
                DataSourceSummaryText = "数据源: 未关联活动文档";
                return;
            }

            var session = DocumentSessionManager.GetOrCreateSession(db);
            DataSourceSummaryText = CheckPipelineCoordinator.FormatDataSourceSummary(session?.DatasetContext);
        }

        /// <summary>
        /// 响应工具栏 `[数据源]` 按钮：打开多数据源核查配置窗口并持久化保存至 Config.json（Stage 6 Commit 6.3）。
        /// </summary>
        public void ExecuteOpenDataSourceSettings(AcadDb.Database targetDb = null)
        {
            var db = targetDb ?? AcadApp.DocumentManager?.MdiActiveDocument?.Database;
            if (db == null)
            {
                StatusText = "当前无活动 CAD 文档，无法配置数据源";
                return;
            }

            var session = DocumentSessionManager.GetOrCreateSession(db);
            bool confirmed = CheckPipelineCoordinator.OpenDataSourceSettings(
                db,
                session?.DatasetContext,
                CustomDataSourceDialogLauncher);

            RefreshDataSourceSummaryText(db);
            StatusText = confirmed
                ? $"已保存数据源配置（{DataSourceSummaryText}）"
                : "已取消数据源配置更改";
        }

        /// <summary>
        /// 响应工具栏 `[一键全量核查]` 按钮：先智能嗅探数据源（无法确定时弹窗），再顺序执行全部五项规则并汇聚展示（Stage 6 Commit 6.3）。
        /// </summary>
        public FullPipelineCheckSummary ExecuteRunFullCheck(AcadDb.Database targetDb = null, Editor targetEd = null, bool showPalette = false)
        {
            var doc = AcadApp.DocumentManager?.MdiActiveDocument;
            var db = targetDb ?? doc?.Database;
            var ed = targetEd ?? doc?.Editor;
            if (db == null)
            {
                StatusText = "当前无活动 CAD 文档，无法执行全量核查";
                return new FullPipelineCheckSummary();
            }

            FullPipelineCheckSummary summary = CheckPipelineCoordinator.ExecuteFullPipelineCheck(
                db,
                ed,
                allowInteractiveDialog: true,
                showPalette: showPalette,
                customDialogLauncher: CustomDataSourceDialogLauncher);

            if (summary.PreCheckBlocked)
            {
                UpdateFromIssueItems(Array.Empty<CheckIssueItem>(), isIncremental: false);
                RefreshDataSourceSummaryText(db);
                StatusText = "当前图纸未检测到合规的规划管线数据，已中止全量核查";
                return summary;
            }

            UpdateFromIssueItems(summary.AllMergedIssues, isIncremental: false);
            RefreshDataSourceSummaryText(db);
            StatusText = $"一键全量核查完成：共执行 {summary.ExecutedRuleIds.Count} 项规则，检出 {Rows.Count} 条记录（{summary.ElapsedMilliseconds}ms）";
            return summary;
        }

        /// <summary>
        /// 响应工具栏 `[单项核查]` 按钮：执行当前选中的单项规则（跨图规则先智能嗅探数据源，无法确定时弹窗，Stage 6 Commit 6.3）。
        /// </summary>
        public IReadOnlyList<CheckIssueItem> ExecuteRunSingleCheck(AcadDb.Database targetDb = null, Editor targetEd = null, bool showPalette = false)
        {
            var ruleOption = SelectedSingleRule ?? AvailableSingleRules.FirstOrDefault();
            if (ruleOption == null)
            {
                StatusText = "请先选择要执行的单项核查规则";
                return Array.Empty<CheckIssueItem>();
            }

            var doc = AcadApp.DocumentManager?.MdiActiveDocument;
            var db = targetDb ?? doc?.Database;
            var ed = targetEd ?? doc?.Editor;
            if (db == null)
            {
                StatusText = "当前无活动 CAD 文档，无法执行单项核查";
                return Array.Empty<CheckIssueItem>();
            }

            IReadOnlyList<CheckIssueItem> mergedIssues = CheckPipelineCoordinator.ExecuteSingleRule(
                db,
                ed,
                ruleOption.RuleId,
                out _,
                allowInteractiveDialog: true,
                showPalette: showPalette,
                customDialogLauncher: CustomDataSourceDialogLauncher);

            UpdateFromIssueItems(mergedIssues ?? Array.Empty<CheckIssueItem>(), isIncremental: false);
            RefreshDataSourceSummaryText(db);

            if ((mergedIssues == null || mergedIssues.Count == 0) &&
                process_pipeline.Core.PreCheck.PreCheckService.ValidateFullPipelinePreConditions(db, showDialogOnBlocking: false).HasBlockingIssues)
            {
                StatusText = $"当前图纸未检测到合规的规划管线数据，已中止 [{ruleOption.DisplayName}]";
            }
            else
            {
                StatusText = $"单项核查 [{ruleOption.DisplayName}] 完成：当前共 {Rows.Count} 条记录";
            }
            return mergedIssues;
        }

        /// <summary>
        /// 重新调用核查规则刷新数据。
        /// </summary>
        public void RefreshByRule()
        {
            palCheckPipe.Instance.RefreshData();
        }

        public void OnImpliedSelectionChanged()
        {
            if (_isSyncingSelection) return;
            if (_grid == null) return;
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            _grid.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isSyncingSelection) return;
                var activeDoc = AcadApp.DocumentManager.MdiActiveDocument;
                if (activeDoc == null) return;

                _isSyncingSelection = true;
                try
                {
                    var psr = activeDoc.Editor.SelectImplied();
                    var matchedRows = new List<PipeRowVm>();
                    if (psr.Status == PromptStatus.OK && psr.Value != null)
                    {
                        foreach (ObjectId id in psr.Value.GetObjectIds())
                        {
                            if (_objectIdToRowsMap.TryGetValue(id, out List<PipeRowVm> rowsForId) && rowsForId != null)
                            {
                                foreach (PipeRowVm row in rowsForId)
                                {
                                    if (FilterRow(row) && !matchedRows.Contains(row))
                                    {
                                        matchedRows.Add(row);
                                    }
                                }
                            }
                            else if (_objectIdToRowMap.TryGetValue(id, out PipeRowVm matchedRow) && FilterRow(matchedRow))
                            {
                                if (!matchedRows.Contains(matchedRow))
                                {
                                    matchedRows.Add(matchedRow);
                                }
                            }
                        }
                    }

                    _grid.SelectedItems.Clear();
                    foreach (PipeRowVm row in matchedRows)
                    {
                        _grid.SelectedItems.Add(row);
                    }

                    SelectedRow = matchedRows.FirstOrDefault();
                    SyncSelectedRowsFromGrid(syncCadSelection: false);
                }
                catch
                {
                    // 忽略选择集瞬态读取异常
                }
                finally
                {
                    _isSyncingSelection = false;
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void OnImpliedSelectionChangedPlaceholder()
        {

        }
    }

    public sealed class GridLengthCache
    {
        public double LastLeftWidth { get; set; } = 320;
    }

    /// <summary>
    /// 左侧导航树节点的过滤维度类型（Commit 5.2）。
    /// </summary>
    public enum TreeFilterKind
    {
        AllSummary = 0,
        RuleCategory = 1,
        RuleStatus = 2,
        LayerRoot = 3,
        LayerItem = 4
    }

    /// <summary>
    /// 核查状态对应的标准色彩画刷工具类（Commit 5.2）。
    /// 使用冻结（Frozen）的 SolidColorBrush，支持跨线程与高性能 WPF 矢量圆点渲染。
    /// </summary>
    public static class StatusColorPalette
    {
        public static readonly Brush PendingBrush = CreateFrozenBrush(0xFF, 0x52, 0x52);   // 醒目红：待核查
        public static readonly Brush ToRecheckBrush = CreateFrozenBrush(0xFF, 0xB3, 0x00); // 琥珀黄：待复核
        public static readonly Brush ModifiedBrush = CreateFrozenBrush(0x40, 0xC4, 0xFF);  // 亮天蓝：已修改
        public static readonly Brush IgnoredBrush = CreateFrozenBrush(0xCF, 0xD8, 0xDC);   // 柔和灰白：已忽略
        public static readonly Brush ResolvedBrush = CreateFrozenBrush(0x69, 0xF0, 0xAE);  // 翠绿：已修复

        private static Brush CreateFrozenBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        public static Brush GetBrush(IssueReviewStatus status)
        {
            switch (status)
            {
                case IssueReviewStatus.Pending:
                    return PendingBrush;
                case IssueReviewStatus.ToRecheck:
                    return ToRecheckBrush;
                case IssueReviewStatus.Modified:
                    return ModifiedBrush;
                case IssueReviewStatus.Ignored:
                    return IgnoredBrush;
                case IssueReviewStatus.Resolved:
                    return ResolvedBrush;
                default:
                    return PendingBrush;
            }
        }
    }

    /// <summary>
    /// 左侧“核查类型 -> 核查状态 / 管线图层”导航树节点视图模型（Commit 5.2）。
    /// </summary>
    public class TreeNodeVm : INotifyPropertyChanged
    {
        private string _name;
        private string _countText = string.Empty;
        private Brush _statusBrush;
        private bool _isExpanded = true;
        private bool _isSelected;

        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        /// <summary>
        /// 节点右侧括号内的实时数量文本，如 "(25)" 或 "(25/36)"。
        /// </summary>
        public string CountText
        {
            get => _countText;
            set
            {
                if (_countText != value)
                {
                    _countText = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        /// <summary>
        /// 组合后的完整显示文本（兼容单 TextBlock 绑定）。
        /// </summary>
        public string DisplayText =>
            string.IsNullOrWhiteSpace(_countText) ? _name : $"{_name} {_countText}";

        /// <summary>
        /// 状态圆点颜色画刷（仅二级状态节点非 null，一级节点与图层节点为 null）。
        /// </summary>
        public Brush StatusBrush
        {
            get => _statusBrush;
            set
            {
                if (_statusBrush != value)
                {
                    _statusBrush = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DotVisibility));
                }
            }
        }

        /// <summary>
        /// 控制节点前彩色圆点是否显示。
        /// </summary>
        public System.Windows.Visibility DotVisibility =>
            _statusBrush != null ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded != value)
                {
                    _isExpanded = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public TreeFilterKind FilterKind { get; set; } = TreeFilterKind.AllSummary;
        public string RuleNameFilter { get; set; }
        public IssueReviewStatus? StatusFilter { get; set; }
        public string LayerFilter { get; set; }

        public ObservableCollection<TreeNodeVm> Children { get; set; } = new ObservableCollection<TreeNodeVm>();

        public TreeNodeVm(string name) => _name = name;

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// 主核查窗口表格单行视图模型（Commit 2.1 & Commit 5.2）。
    /// 
    /// 架构设计说明：
    /// 1. 实现 INotifyPropertyChanged，支持单行就地刷新与 Undo/Redo 状态回滚。
    /// 2. 暴露 RuleName（核查类型）、LocationText（坐标文本）与 StatusBrush（彩色状态圆点画刷）。
    /// </summary>
    public class PipeRowVm : INotifyPropertyChanged
    {
        private string _id;
        private string _ruleName;
        private string _type;
        private string _status;
        private string _locationText;
        private double _diameter;
        private double _height;
        private string _remark;

        /// <summary>
        /// 关联的底层统一核查问题领域模型。
        /// </summary>
        public CheckIssueItem SourceIssue { get; private set; }

        /// <summary>
        /// 对应 CAD 图元的运行时 ObjectId（用于双击定位与图面联动高亮）。
        /// </summary>
        public ObjectId TargetObjectId => SourceIssue != null ? SourceIssue.TargetObjectId : ObjectId.Null;

        public string Id
        {
            get => _id;
            set => SetField(ref _id, value);
        }

        /// <summary>
        /// 核查类型中文名称（如 "流向箭头核查"、"管段匹配核查"）。
        /// </summary>
        public string RuleName
        {
            get => _ruleName;
            set
            {
                if (SetField(ref _ruleName, value) && SourceIssue != null)
                {
                    SourceIssue.RuleName = value;
                }
            }
        }

        /// <summary>
        /// 管线所属 CAD 图层名称。
        /// </summary>
        public string Type
        {
            get => _type;
            set
            {
                if (SetField(ref _type, value) && SourceIssue != null)
                {
                    SourceIssue.LayerName = value;
                }
            }
        }

        public string Status
        {
            get => _status;
            set
            {
                if (SetField(ref _status, value))
                {
                    OnPropertyChanged(nameof(StatusBrush));
                }
            }
        }

        /// <summary>
        /// 当前状态对应的彩色圆点画刷（红/黄/蓝/灰白/翠绿）。
        /// </summary>
        public Brush StatusBrush =>
            StatusColorPalette.GetBrush(SourceIssue?.Status ?? IssueReviewStatus.Pending);

        /// <summary>
        /// 格式化代表点坐标文本。
        /// </summary>
        public string LocationText
        {
            get => _locationText;
            set => SetField(ref _locationText, value);
        }

        public double Diameter
        {
            get => _diameter;
            set
            {
                if (SetField(ref _diameter, value) && SourceIssue != null)
                {
                    SourceIssue.Diameter = value;
                }
            }
        }
        public double Height
        {
            get => _height;
            set
            {
                if (SetField(ref _height, value) && SourceIssue != null)
                {
                    SourceIssue.Height = value;
                }
            }
        }
        public string Remark
        {
            get => _remark;
            set
            {
                if (SetField(ref _remark, value) && SourceIssue != null)
                {
                    SourceIssue.Remark = value;
                }
            }
        }

        public PipeRowVm(CheckIssueItem issue)
        {
            ApplyFromIssue(issue);
        }

        /// <summary>
        /// 从统一问题领域模型就地刷新当前行各列属性（不销毁行实例）。
        /// </summary>
        public void ApplyFromIssue(CheckIssueItem issue)
        {
            if (issue == null) return;

            SourceIssue = issue;
            Id = !string.IsNullOrWhiteSpace(issue.TargetHandle) ? issue.TargetHandle : issue.IssueId;
            RuleName = issue.GetRuleDisplayName();
            Type = !string.IsNullOrWhiteSpace(issue.LayerName) ? issue.LayerName : issue.GetRuleDisplayName();
            Status = issue.GetStatusDisplayText();
            LocationText = issue.LocationText;
            Diameter = issue.Diameter;
            Height = issue.Height;
            Remark = !string.IsNullOrWhiteSpace(issue.Remark) ? issue.Remark : issue.Description;
            OnPropertyChanged(nameof(StatusBrush));
        }

        /// <summary>
        /// 更新人工核查状态并同步刷新 Status 显示文本与彩色圆点画刷。
        /// </summary>
        public void UpdateReviewStatus(IssueReviewStatus newStatus, string newRemark = null)
        {
            if (SourceIssue != null)
            {
                SourceIssue.Status = newStatus;
                Status = SourceIssue.GetStatusDisplayText();
                OnPropertyChanged(nameof(StatusBrush));
            }
            if (newRemark != null)
            {
                Remark = newRemark;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        public PipeRowVm(string id, string type, string status, double diameter, double height, string remark)
        {
            Id = id;
            RuleName = "流向箭头核查";
            Type = type;
            Status = status;
            LocationText = "-";
            Diameter = diameter;
            Height = height;
            Remark = remark;
            SourceIssue = new CheckIssueItem
            {
                IssueId = id,
                RuleId = "FlowArrow",
                RuleName = "流向箭头核查",
                TargetHandle = id,
                LayerName = type,
                Diameter = diameter,
                Height = height,
                Remark = remark,
                Description = remark
            };
        }
    }

    public class palCheckPipe : PaletteSetBase<ucMainCheckWindow, Dictionary<ObjectId, CheckIssueItem>>
    {
        // 子类单例（不变）
        private static readonly Lazy<palCheckPipe> _instance = new Lazy<palCheckPipe>(() => new palCheckPipe());
        public static palCheckPipe Instance => _instance.Value;

        private palCheckPipe() : base() { }

        // 【新增】：实现抽象属性，提供唯一 GUID（生成新 GUID，避免与基类冲突）
        protected override Guid PaletteGuid => new Guid("9b3f8c21-7d12-4e5a-b98c-07f8e29d1a6c");

        // 实现其他抽象方法（不变）
        protected override string GetPaletteTitle() => "管线检查";
        protected override string GetPaletteName() => "PipeCheckPalette";
        public IReadOnlyDictionary<ObjectId, CheckIssueItem> CurrentIssues =>
            _currentControl?.CurrentIssues ?? new Dictionary<ObjectId, CheckIssueItem>();

        protected override Dictionary<ObjectId, CheckIssueItem> GetInitialData() => new Dictionary<ObjectId, CheckIssueItem>();

        /// <summary>
        /// 当用户在多个 DWG 文档标签页之间切换时，自动从目标文档的 DocumentCheckSession 恢复核查列表。
        /// </summary>
        protected override void OnActiveDocumentChanged(Autodesk.AutoCAD.ApplicationServices.Document newDoc)
        {
            if (newDoc?.Database == null) return;
            var session = DocumentSessionManager.GetOrCreateSession(newDoc.Database);
            if (session != null && session.IssuesById.Count > 0)
            {
                UpdateIssueItems(session.GetAllIssueItems(), isIncremental: false);
            }
            else if (session != null && session.CurrentIssues.Count > 0)
            {
                UpdateData(session.CurrentIssues);
            }
            else
            {
                UpdateIssueItems(Array.Empty<CheckIssueItem>(), isIncremental: false);
            }
        }

        /// <summary>
        /// 局部增量更新面板数据（单行就地刷新，不清空全表）。
        /// </summary>
        public void UpdateIncrementalData(Dictionary<ObjectId, CheckIssueItem> incrementalData)
        {
            if (_currentControl == null || incrementalData == null) return;

            if (!_currentControl.Dispatcher.CheckAccess())
            {
                _currentControl.Dispatcher.Invoke(() => _currentControl.UpdateIncrementalData(incrementalData));
            }
            else
            {
                _currentControl.UpdateIncrementalData(incrementalData);
            }
        }

        /// <summary>
        /// 按唯一 IssueId 列表更新面板数据（支持单管线多规则/多折线段并存及 ObjectId.Null 缺失问题，Stage 2 Commit 2.3）。
        /// </summary>
        public void UpdateIssueItems(IEnumerable<CheckIssueItem> items, bool isIncremental = false)
        {
            if (items == null) return;
            if (_currentControl == null)
            {
                Show(new Dictionary<ObjectId, CheckIssueItem>());
            }
            if (_currentControl == null) return;

            if (!_currentControl.Dispatcher.CheckAccess())
            {
                _currentControl.Dispatcher.Invoke(() => _currentControl.UpdateIssueItems(items, isIncremental));
            }
            else
            {
                _currentControl.UpdateIssueItems(items, isIncremental);
            }
        }

        /// <summary>
        /// 打开核查主面板并按唯一 IssueId 列表展示核查结果（Stage 2 Commit 2.3）。
        /// </summary>
        public void ShowIssueItems(IEnumerable<CheckIssueItem> items)
        {
            Show(new Dictionary<ObjectId, CheckIssueItem>());
            UpdateIssueItems(items, isIncremental: false);
        }

        public override void RefreshData()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var session = DocumentSessionManager.GetOrCreateSession(doc.Database);
            if (session != null && session.ActiveRules.Count > 1)
            {
                foreach (ICheckRule activeRule in session.ActiveRules.Values.ToList())
                {
                    if (activeRule == null) continue;
                    var ruleIssues = activeRule.CheckAll();
                    if (ruleIssues != null &&
                        string.Equals(activeRule.RuleId, CheckPipelineCoordinator.FlowArrowRuleId, StringComparison.OrdinalIgnoreCase))
                    {
                        session.MergeRuleIssues(activeRule.RuleId, ruleIssues, isIncremental: false);
                    }
                }

                UpdateIssueItems(session.GetAllIssueItems(), isIncremental: false);
                return;
            }

            ICheckRule rule = session?.ActiveRule ?? new FlowArrowService(doc.Database, doc.Editor);
            var newProblems = rule.CheckAll();
            if (newProblems != null)
            {
                if (session != null &&
                    string.Equals(rule.RuleId, CheckPipelineCoordinator.FlowArrowRuleId, StringComparison.OrdinalIgnoreCase))
                {
                    session.MergeRuleIssues(rule.RuleId, newProblems, isIncremental: false);
                }

                if (session != null && session.IssuesById.Count > 0)
                {
                    UpdateIssueItems(session.GetAllIssueItems(), isIncremental: false);
                }
                else
                {
                    UpdateData(newProblems);
                }
            }
        }
    }
}
