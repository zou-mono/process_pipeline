using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using process_pipeline.Commands;
using process_pipeline.Core;
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

        public ucMainCheckWindow()
        {
            InitializeComponent();
            // 【移除】：mock 数据和命令初始化（现在在 MainViewModel 中）

            //CadThemes.ApplyCadTheme(this);
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

        private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is TreeNodeVm node)
            {
                _vm.SelectedTreeNode = node;
            }
        }

        private void DataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            _vm.ZoomToSelectedRow(isZoomTo: true);
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
        private Dictionary<ObjectId, CheckIssueItem> _currentIssues = new Dictionary<ObjectId, CheckIssueItem>();
        private PipeRowVm _selectedRow;
        private bool _isSyncingSelection;
        private readonly HybridUndoRedoManager _fallbackUndoRedoManager = new HybridUndoRedoManager();

        /// <summary>
        /// 获取当前活动文档会话的混合撤销/重做管理器（若在非活动文档或示例模式下则回退至本地管理器）。
        /// </summary>
        public HybridUndoRedoManager ActiveUndoRedoManager =>
            DocumentSessionManager.GetActiveSession()?.UndoRedoManager ?? _fallbackUndoRedoManager;

        public IReadOnlyDictionary<ObjectId, CheckIssueItem> CurrentIssues => _currentIssues;

        //private double _leftPanelToggleX;  // GridSplitter距离左侧的偏移值

        public ObservableCollection<TreeNodeVm> TreeNodes { get; } = new ObservableCollection<TreeNodeVm>();
        public ObservableCollection<PipeRowVm> Rows { get; } = new ObservableCollection<PipeRowVm>();

        public ICommand ToggleTreeCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand ZoomToCommand { get; }
        public ICommand ExportCommand { get; }
        public ICommand MarkModifiedCommand { get; }
        public ICommand MarkToRecheckCommand { get; }
        public ICommand MarkIgnoredCommand { get; }
        public ICommand ReverseSelectedPipeCommand { get; }
        public ICommand UndoCommand { get; }
        public ICommand RedoCommand { get; }
        public ICommand CadSelectionChangedCommand { get; }

        public MainCheckWindowViewModel()
        {
            ToggleTreeCommand = new RelayCommand(_ => ToggleTree());
            RefreshCommand = new RelayCommand(_ => RefreshByRule());
            ZoomToCommand = new RelayCommand(_ => ZoomToSelectedRow(isZoomTo: true));
            ExportCommand = new RelayCommand(_ => StatusText = "已导出");
            MarkModifiedCommand = new RelayCommand(_ => ChangeSelectedRowStatus(IssueReviewStatus.Modified), _ => SelectedRow != null);
            MarkToRecheckCommand = new RelayCommand(_ => ChangeSelectedRowStatus(IssueReviewStatus.ToRecheck), _ => SelectedRow != null);
            MarkIgnoredCommand = new RelayCommand(_ => ChangeSelectedRowStatus(IssueReviewStatus.Ignored), _ => SelectedRow != null);
            ReverseSelectedPipeCommand = new RelayCommand(_ => ReverseSelectedPipe(), _ => CanReverseSelectedPipe());
            UndoCommand = new RelayCommand(_ => ExecuteUndo(), _ => ActiveUndoRedoManager.CanUndo);
            RedoCommand = new RelayCommand(_ => ExecuteRedo(), _ => ActiveUndoRedoManager.CanRedo);
            CadSelectionChangedCommand = new RelayCommand(_ => OnImpliedSelectionChanged());

            // 挂载默认集合视图过滤器（WPF DataGrid 绑定 Rows 时会自动使用此 DefaultView）
            _rowsView = CollectionViewSource.GetDefaultView(Rows);
            if (_rowsView != null)
            {
                _rowsView.Filter = FilterRow;
            }

            // 初始化 mock 数据（在实际应用中，可以从服务加载）
            InitializeMockData();
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
                Remark = "已翻转管线流向（已修复归档）"
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
        /// 当前表格选中的问题行（Commit 2.3）。
        /// 切换选中行时自动在 CAD 图面高亮对应管线，若是一对多箭头冲突则绘制瞬态辅助红线。
        /// </summary>
        public PipeRowVm SelectedRow
        {
            get => _selectedRow;
            set
            {
                if (Set(ref _selectedRow, value))
                {
                    OnPropertyChanged(nameof(ReversePipeMenuVisibility));
                    NotifyUndoRedoStateChanged();
                    if (!_isSyncingSelection && value != null)
                    {
                        ZoomToSelectedRow(isZoomTo: false);
                    }
                }
            }
        }

        /// <summary>
        /// 上下文感知显隐（Commit 4.3）：
        /// 仅当当前选中行属于流向冲突问题（DirectionConflict 或 OneToMany）时，
        /// 右键菜单才显示“一键修复：翻转管线流向”，非此类问题则彻底隐藏（Collapsed）。
        /// </summary>
        public System.Windows.Visibility ReversePipeMenuVisibility =>
            CanReverseSelectedPipe() ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

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
                    return $"记录数：{totalCount}（已修复归档：{resolvedCount}）";

                int visibleCount = _rowsView.Cast<object>().Count();
                return $"显示：{visibleCount} / 总计：{totalCount}（已修复归档：{resolvedCount}）";
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
                        // 点击二级【核查状态】节点（如“待核查”或“已修复归档”）时：按核查类型 + 具体状态精准匹配
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
                // 未选中任何树节点时，默认隐藏已修复归档项，仅展示待处理/活动项
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
        /// 触发内存视图过滤刷新并同步底部状态栏记录数。
        /// </summary>
        public void RefreshFilter()
        {
            _rowsView?.Refresh();
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
        /// 使用统一核查问题模型（CheckIssueItem）更新列表，支持全量刷新、单行就地增量更新与已修复归档（Commit 5.2）。
        /// 
        /// 设计说明：
        /// - 已修复（IssueReviewStatus.Resolved）的记录不再从 Rows 中物理删除，而是保留在集合中，
        ///   自动归档到对应核查类型的“已修复归档”节点下；仅当 CAD 图元被彻底 Erase 删除时才移除行。
        /// </summary>
        public void UpdateFromIssues(Dictionary<ObjectId, CheckIssueItem> issues, bool isIncremental = false)
        {
            if (issues == null) return;

            if (!isIncremental)
            {
                _currentIssues = new Dictionary<ObjectId, CheckIssueItem>(issues);
                Rows.Clear();
                _objectIdToRowMap.Clear();

                foreach (var kvp in issues)
                {
                    CheckIssueItem issue = kvp.Value;
                    if (issue == null || (!issue.TargetObjectId.IsNull && issue.TargetObjectId.IsErased))
                        continue;

                    var rowVm = new PipeRowVm(issue);
                    Rows.Add(rowVm);
                    if (!issue.TargetObjectId.IsNull)
                    {
                        _objectIdToRowMap[issue.TargetObjectId] = rowVm;
                    }
                }

                RebuildNavigationTreeNodes();
            }
            else
            {
                bool hasNewRuleOrLayer = false;
                foreach (var kvp in issues)
                {
                    ObjectId targetId = kvp.Key;
                    CheckIssueItem issue = kvp.Value;
                    if (issue == null) continue;

                    _currentIssues[targetId] = issue;
                    bool isErased = !targetId.IsNull && targetId.IsErased;

                    if (_objectIdToRowMap.TryGetValue(targetId, out PipeRowVm existingRow))
                    {
                        if (isErased)
                        {
                            Rows.Remove(existingRow);
                            _objectIdToRowMap.Remove(targetId);
                        }
                        else
                        {
                            // 单行就地刷新（若变为 Resolved，则自动归档至“已修复归档”节点）
                            existingRow.ApplyFromIssue(issue);
                        }
                    }
                    else if (!isErased)
                    {
                        var newRow = new PipeRowVm(issue);
                        Rows.Add(newRow);
                        if (!targetId.IsNull)
                        {
                            _objectIdToRowMap[targetId] = newRow;
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
                activeSession.CurrentIssues = new Dictionary<ObjectId, CheckIssueItem>(_currentIssues);
            }

            RefreshFilter();
            StatusText = $"已更新 {Rows.Count} 条核查记录";
        }

        /// <summary>
        /// 构建“构想 1”二级导航树（Commit 5.2）：
        /// - 一级主节点：各【核查类型】（如“流向箭头核查”、“管段匹配核查”）；
        /// - 二级子节点：5 个带彩色矢量圆点的【核查状态】（待核查、待复核、已修改、已忽略、已修复归档）；
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
                    ("已修改", IssueReviewStatus.Modified),
                    ("已忽略", IssueReviewStatus.Ignored),
                    ("已修复归档", IssueReviewStatus.Resolved)
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

            // 2. 构建底部【按管线图层辅助筛选】节点
            var layerRoot = new TreeNodeVm("按管线图层辅助筛选")
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
        /// 人工判读状态修改（Commit 4.3）：
        /// 通过 HybridUndoRedoManager 封装为可逆操作 ChangeIssueStatusOperation，
        /// 支持单行就地刷新以及 Ctrl+Z / Ctrl+Y 撤销与重做。
        /// </summary>
        public void ChangeSelectedRowStatus(IssueReviewStatus newStatus)
        {
            if (SelectedRow?.SourceIssue == null) return;

            CheckIssueItem beforeSnapshot = SelectedRow.SourceIssue.CloneSnapshot();
            CheckIssueItem afterSnapshot = SelectedRow.SourceIssue.CloneSnapshot();
            afterSnapshot.Status = newStatus;

            PipeRowVm targetRow = SelectedRow;
            var op = new ChangeIssueStatusOperation(
                beforeSnapshot,
                afterSnapshot,
                snapshot => ApplyIssueSnapshotToRow(targetRow, snapshot));

            ActiveUndoRedoManager.ExecuteOperation(op);
            NotifyUndoRedoStateChanged();
            StatusText = $"已将管线 [{targetRow.Id}] 标记为：{targetRow.Status}";
        }

        /// <summary>
        /// 判断当前选中行是否适用“翻转管线流向”修复操作。
        /// </summary>
        public bool CanReverseSelectedPipe()
        {
            var issue = SelectedRow?.SourceIssue;
            if (issue == null) return false;

            return issue.Type == ProblemType.DirectionConflict ||
                   issue.Type == ProblemType.OneToMany;
        }

        /// <summary>
        /// 一键翻转当前选中管线的流向（Commit 4.3 & Commit 5.2）：
        /// 封装为可逆复合操作 ReverseCurveOperation，翻转后自动将其归档至“已修复归档”节点下。
        /// </summary>
        public void ReverseSelectedPipe()
        {
            if (!CanReverseSelectedPipe() || SelectedRow?.SourceIssue == null)
                return;

            PipeRowVm targetRow = SelectedRow;
            ObjectId targetId = targetRow.TargetObjectId;
            if (targetId.IsNull || !targetId.IsValid || targetId.IsErased)
            {
                StatusText = $"管线 [{targetRow.Id}] 未关联有效 CAD 图元，无法翻转";
                return;
            }

            CheckIssueItem beforeSnapshot = targetRow.SourceIssue.CloneSnapshot();
            var op = new ReverseCurveOperation(
                targetId,
                beforeSnapshot,
                (curveId, snapshot, isUndo) =>
                {
                    if (snapshot != null)
                    {
                        ApplyIssueSnapshotToRow(targetRow, snapshot);
                    }
                    PaletteRefreshManager.TriggerPaletteRefreshIfNeeded();
                });

            ActiveUndoRedoManager.ExecuteOperation(op);
            NotifyUndoRedoStateChanged();
            StatusText = $"已翻转管线 [{targetRow.Id}] 流向，并归档至【已修复归档】";
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

        private void ApplyIssueSnapshotToRow(PipeRowVm row, CheckIssueItem snapshot)
        {
            if (row == null || snapshot == null) return;

            row.ApplyFromIssue(snapshot);
            if (!snapshot.TargetObjectId.IsNull)
            {
                _currentIssues[snapshot.TargetObjectId] = snapshot;
                var activeSession = DocumentSessionManager.GetActiveSession();
                if (activeSession != null)
                {
                    activeSession.CurrentIssues[snapshot.TargetObjectId] = snapshot;
                }
            }

            UpdateTreeCounts();
            RefreshFilter();
        }

        private void NotifyUndoRedoStateChanged()
        {
            OnPropertyChanged(nameof(UndoMenuHeader));
            OnPropertyChanged(nameof(RedoMenuHeader));
            CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>
        /// 选中或缩放定位到当前选中行对应的 CAD 实体，并按需绘制一对多瞬态辅助线。
        /// </summary>
        public void ZoomToSelectedRow(bool isZoomTo)
        {
            if (_isSyncingSelection || SelectedRow == null) return;

            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            ObjectId targetId = SelectedRow.TargetObjectId;
            if (targetId.IsNull || !targetId.IsValid || targetId.IsErased)
            {
                StatusText = $"管线 [{SelectedRow.Id}] 为示例数据或未关联有效图元";
                return;
            }

            _isSyncingSelection = true;
            try
            {
                var selector = new SelectByHandleService(doc.Database, doc.Editor);
                selector.SelectByHandles(new[] { targetId }, isZoomTo);

                GraphicManager.ClearAuxiliaryGraphics();
                var issue = SelectedRow.SourceIssue;
                if (issue != null &&
                    issue.Type == ProblemType.OneToMany &&
                    issue.PossibleMatches != null &&
                    issue.PossibleMatches.Count > 0)
                {
                    GraphicManager.DrawAuxiliaryLines(issue.PossibleMatches);
                }

                StatusText = isZoomTo ? $"已定位到管线 [{SelectedRow.Id}]" : $"已选中管线 [{SelectedRow.Id}]";
            }
            finally
            {
                _isSyncingSelection = false;
            }
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
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            _isSyncingSelection = true;
            try
            {
                var psr = doc.Editor.SelectImplied();
                if (psr.Status == PromptStatus.OK && psr.Value != null)
                {
                    foreach (ObjectId id in psr.Value.GetObjectIds())
                    {
                        if (_objectIdToRowMap.TryGetValue(id, out PipeRowVm matchedRow))
                        {
                            SelectedRow = matchedRow;
                            break;
                        }
                    }
                }
            }
            catch
            {
                // 忽略选择集瞬态读取异常
            }
            finally
            {
                _isSyncingSelection = false;
            }
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
        public static readonly Brush ResolvedBrush = CreateFrozenBrush(0x69, 0xF0, 0xAE);  // 翠绿：已修复归档

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
            UpdateData(session?.CurrentIssues ?? new Dictionary<ObjectId, CheckIssueItem>());
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

        public override void RefreshData()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var session = DocumentSessionManager.GetOrCreateSession(doc.Database);
            ICheckRule rule = session?.ActiveRule ?? new FlowArrowService(doc.Database, doc.Editor);
            var newProblems = rule.CheckAll();
            if (newProblems != null)
            {
                var merged = session != null
                    ? session.MergeRuleIssues(rule.RuleId, newProblems, isIncremental: false)
                    : newProblems;
                UpdateData(merged);
            }
        }
    }
}
