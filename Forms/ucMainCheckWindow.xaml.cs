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
                _vm.SelectedLayerFilter = node.Name;
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
        // Commit 2.2：引入 ICollectionView 内存过滤与 ObjectId->Row 索引映射
        // 1. _rowsView：直接挂载在 Rows 默认视图上，当切换图层树节点或输入搜索关键字时，
        //    只通过 _rowsView.Refresh() 过滤显示，绝不调用 Rows.Clear() 破坏原集合。
        // 2. _objectIdToRowMap：用于增量核查或人工修改时 O(1) 定位到具体行进行就地更新。
        // ============================================================
        private string _selectedLayerFilter = "全部图层";
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
            set
            {
                if (Set(ref _keyword, value))
                {
                    RefreshFilter();
                }
            }
        }

        /// <summary>
        /// 当前左侧图层树选中的图层过滤名称（默认 "全部图层" 表示不过滤）。
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
                if (_rowsView == null)
                    return $"记录数：{Rows.Count}";

                int visibleCount = _rowsView.Cast<object>().Count();
                return visibleCount == Rows.Count
                    ? $"记录数：{Rows.Count}"
                    : $"记录数：{visibleCount} / {Rows.Count}";
            }
        }

        /// <summary>
        /// 集合视图单行过滤谓词：同时判断左侧图层树选中项与顶部搜索框关键字。
        /// </summary>
        private bool FilterRow(object obj)
        {
            PipeRowVm row = obj as PipeRowVm;
            if (row == null) return false;

            // 1. 图层/分类过滤（"全部图层" 或空字符串时放行全部）
            if (!string.IsNullOrWhiteSpace(_selectedLayerFilter) &&
                !string.Equals(_selectedLayerFilter, "全部图层", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(row.Type, _selectedLayerFilter, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            // 2. 关键字模糊匹配（支持匹配编号、类型、状态、备注）
            if (!string.IsNullOrWhiteSpace(_keyword))
            {
                string kw = _keyword.Trim();
                bool matchId = row.Id != null && row.Id.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0;
                bool matchType = row.Type != null && row.Type.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0;
                bool matchStatus = row.Status != null && row.Status.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0;
                bool matchRemark = row.Remark != null && row.Remark.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0;

                if (!matchId && !matchType && !matchStatus && !matchRemark)
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

        // 【新增】：从外部数据更新 ViewModel（用于 UpdateData 调用）
        // 假设 ProblemItem 有 Type、Status、Diameter、Height、Remark 等字段
        public void UpdateFromData(Dictionary<ObjectId, ProblemItem> data)
        {
            if (data == null) return;

            // 清空并重新填充 Rows（基于 ProblemItem 字段）
            Rows.Clear();
            _objectIdToRowMap.Clear();
            //int index = 1;
            foreach (var kvp in data.Where(p => !p.Value.IsFixed && !p.Value.PipeId.IsErased))
            {
                var item = kvp.Value;
                // 假设 ProblemItem 有这些属性；如果字段名不同，调整此处
                CheckIssueItem issue = CheckIssueItem.FromProblemItem(item);
                var rowVm = new PipeRowVm(issue);
                Rows.Add(rowVm);
                if (!issue.TargetObjectId.IsNull)
                {
                    _objectIdToRowMap[issue.TargetObjectId] = rowVm;
                }
            }

            // 更新状态文本（可选）
            StatusText = $"已更新 {Rows.Count} 条记录";

            // 触发属性通知，确保 WPF 绑定刷新
            OnPropertyChanged(nameof(Rows));
            RefreshFilter();
        }

        /// <summary>
        /// 使用统一核查问题模型（CheckIssueItem）更新列表，支持全量刷新与单行就地增量更新。
        /// 
        /// 设计说明（Commit 2.2）：
        /// - 当 isIncremental == false（全量核查）时：重建列表并自动提取图层树节点；
        /// - 当 isIncremental == true（增量核查）时：不调用 Rows.Clear()，而是通过 _objectIdToRowMap
        ///   直接定位受影响的 PipeRowVm 调用 ApplyFromIssue() 就地更新属性，若问题已解决则移除该行。
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
                    if (issue == null || issue.Status == IssueReviewStatus.Resolved || issue.TargetObjectId.IsErased)
                        continue;

                    var rowVm = new PipeRowVm(issue);
                    Rows.Add(rowVm);
                    if (!issue.TargetObjectId.IsNull)
                    {
                        _objectIdToRowMap[issue.TargetObjectId] = rowVm;
                    }
                }

                RebuildLayerTreeNodes();
            }
            else
            {
                foreach (var kvp in issues)
                {
                    ObjectId targetId = kvp.Key;
                    CheckIssueItem issue = kvp.Value;
                    if (issue == null) continue;

                    _currentIssues[targetId] = issue;

                    bool isResolvedOrErased = issue.Status == IssueReviewStatus.Resolved || targetId.IsErased;

                    if (_objectIdToRowMap.TryGetValue(targetId, out PipeRowVm existingRow))
                    {
                        if (isResolvedOrErased)
                        {
                            Rows.Remove(existingRow);
                            _objectIdToRowMap.Remove(targetId);
                        }
                        else
                        {
                            // 单行就地刷新，不破坏表格滚动位置与选中项
                            existingRow.ApplyFromIssue(issue);
                        }
                    }
                    else if (!isResolvedOrErased)
                    {
                        var newRow = new PipeRowVm(issue);
                        Rows.Add(newRow);
                        if (!targetId.IsNull)
                        {
                            _objectIdToRowMap[targetId] = newRow;
                        }
                    }
                }
            }

            var activeSession = DocumentSessionManager.GetActiveSession();
            if (activeSession != null)
            {
                activeSession.CurrentIssues = new Dictionary<ObjectId, CheckIssueItem>(_currentIssues);
            }

            RefreshFilter();
            StatusText = $"已更新 {Rows.Count} 条记录";
        }

        /// <summary>
        /// 根据当前 Rows 中的图层/分类动态刷新左侧图层树节点。
        /// </summary>
        private void RebuildLayerTreeNodes()
        {
            var rootNode = new TreeNodeVm("全部图层");
            var distinctLayers = Rows
                .Select(r => r.Type)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (string layer in distinctLayers)
            {
                rootNode.Children.Add(new TreeNodeVm(layer));
            }

            TreeNodes.Clear();
            TreeNodes.Add(rootNode);
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
        /// 人工判读状态修改（Commit 2.3）：就地更新当前选中行的状态，不重建列表。
        /// </summary>
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
        /// 一键翻转当前选中管线的流向（Commit 4.3）：
        /// 封装为可逆复合操作 ReverseCurveOperation，在翻转 CAD 曲线顶点的同时同步更新表格行状态。
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
            StatusText = $"已翻转管线 [{targetRow.Id}] 流向";
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

    public class TreeNodeVm
    {
        public string Name { get; set; }
        public ObservableCollection<TreeNodeVm> Children { get; set; } = new ObservableCollection<TreeNodeVm>();
        public TreeNodeVm(string name) => Name = name;
    }

    /// <summary>
    /// 主核查窗口表格单行视图模型（Commit 2.1）。
    /// 
    /// 架构设计说明：
    /// 1. 实现 INotifyPropertyChanged：
    ///    原有的 PipeRowVm 所有属性均为只读 get，导致任何状态或属性变化都必须 Rows.Clear() 全表重建。
    ///    改为可通知属性后，人工修改（如状态变为“已修改/已忽略”、修改管径或备注）以及 Undo/Redo 回滚时，
    ///    只需更新对应行的属性即可触发 WPF 单元格局部刷新，保留表格滚动条位置与当前选中状态。
    /// 2. 绑定统一领域模型 CheckIssueItem：
    ///    内部持有 SourceIssue 引用，并提供 ApplyFromIssue() 方法支持单行就地同步。
    /// </summary>
    public class PipeRowVm : INotifyPropertyChanged
    {
        private string _id;
        private string _type;
        private string _status;
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
            set => SetField(ref _status, value);
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
            Type = !string.IsNullOrWhiteSpace(issue.LayerName) ? issue.LayerName : issue.RuleId;
            Status = issue.GetStatusDisplayText();
            Diameter = issue.Diameter;
            Height = issue.Height;
            Remark = !string.IsNullOrWhiteSpace(issue.Remark) ? issue.Remark : issue.Description;
        }

        /// <summary>
        /// 更新人工核查状态并同步刷新 Status 显示文本。
        /// </summary>
        public void UpdateReviewStatus(IssueReviewStatus newStatus, string newRemark = null)
        {
            if (SourceIssue != null)
            {
                SourceIssue.Status = newStatus;
                Status = SourceIssue.GetStatusDisplayText();
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
            Type = type;
            Status = status;
            Diameter = diameter;
            Height = height;
            Remark = remark;
            SourceIssue = new CheckIssueItem
            {
                IssueId = id,
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


            ICheckRule rule = new FlowArrowService(doc.Database, doc.Editor);
            var newProblems = rule.CheckAll();
            if (newProblems != null)
            {
                UpdateData(newProblems);
            }
        }
    }
}
