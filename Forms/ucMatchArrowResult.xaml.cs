using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using process_pipeline.Commands;
using process_pipeline.Core;
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
    /// 
    // UserControl类，纯View类
    public partial class ucMatchArrowResult : UserControl, IPaletteControl<Dictionary<ObjectId, ProblemItem>>
    {
        private readonly ucMatchArrowResultViewModel _vm = new ucMatchArrowResultViewModel();
        private Document _doc;

        public IReadOnlyDictionary<ObjectId, ProblemItem> CurrentProblems => _vm.CurrentProblems;

        public ucMatchArrowResult() : this(null) { }

        public ucMatchArrowResult(Dictionary<ObjectId, ProblemItem> problems) : base()
        {
            InitializeComponent();

            _doc = AcadApp.DocumentManager.MdiActiveDocument;

            dgvProblems.ItemsSource = _vm.ObservableList;
            _vm.Initialize(dgvProblems, problems ?? new Dictionary<ObjectId, ProblemItem>());

            // 如需启用 CAD 反向联动，在 Loaded 里订阅更稳妥
            // if (_doc != null) _doc.ImpliedSelectionChanged += Doc_ImpliedSelectionChanged;
        }

        public void UpdateData(Dictionary<ObjectId, ProblemItem> data)
        {
            _vm.UpdateData(data);
        }

        private void dgvProblems_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var (rowHeader, row) = VisualTree.GetRowContext(e.OriginalSource);

            if (rowHeader != null && row?.Item is ProblemItemViewModel)
            {
                _vm.ExecuteCadSelection(true);
                e.Handled = true;
                return;
            }
        }

        private void dgvProblems_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _vm.ExecuteCadSelection(false);
        }

        private void CtxZoomToExtent_Click(object sender, RoutedEventArgs e)
        {
            _vm.ExecuteCadSelection(true);
        }

        private void CtxRefresh_Click(object sender, RoutedEventArgs e)
        {
            Dispatcher.Invoke(() => _vm.RefreshByService());
        }

        private void CtxCopy_Click(object sender, RoutedEventArgs e)
        {
            _vm.UpdateCopyHeaderPolicy();

            if (sender is MenuItem mi && mi.Parent is ContextMenu cm)
                cm.IsOpen = false;

            dgvProblems.Dispatcher.BeginInvoke(new Action(() =>
            {
                _vm.TryCopy(CopyFormat.Smart);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void CtxSelect_Click(object sender, RoutedEventArgs e)
        {
            _vm.ToggleRowSelectionFromTag();
        }

        private void CtxClearSelected_Click(object sender, RoutedEventArgs e)
        {
            _vm.ClearSelected();
        }

        private void DataGrid_ExecuteCopy(object sender, ExecutedRoutedEventArgs e)
        {
            _vm.TryCopy(CopyFormat.Smart);
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            CadThemes.ApplyCadTheme(this);

            _doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (_doc != null)
                _doc.ImpliedSelectionChanged += Doc_ImpliedSelectionChanged;
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_doc != null)
                _doc.ImpliedSelectionChanged -= Doc_ImpliedSelectionChanged;
        }

        private void Doc_ImpliedSelectionChanged(object sender, EventArgs e)
        {
            _vm.OnImpliedSelectionChanged();
        }

        private void dgvProblems_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DependencyObject dep = (DependencyObject)e.OriginalSource;
            DependencyObject temp = dep;
            DataGridRowHeader rowHeader = null;
            DataGridCell cell = null;

            while (temp != null && temp != dgvProblems)
            {
                if (temp is DataGridRowHeader rh) { rowHeader = rh; break; }
                if (temp is DataGridCell c) { cell = c; break; }
                temp = VisualTreeHelper.GetParent(temp);
            }

            if (rowHeader != null)
            {
                return;
            }
            else if (cell != null)
            {
                if (Keyboard.Modifiers == ModifierKeys.None)
                {
                    dgvProblems.SelectedCells.Clear();

                    DataGridRow row = ItemsControl.ContainerFromElement(dgvProblems, cell) as DataGridRow;
                    if (row != null)
                    {
                        DataGridCellInfo cellInfo = new DataGridCellInfo(cell);
                        dgvProblems.SelectedCells.Add(cellInfo);
                        row.IsSelected = false;
                    }

                    dgvProblems.Focus();
                }
            }
        }

        private void dgvProblems_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            e.Row.Header = (e.Row.GetIndex() + 1).ToString();
        }

        private void dgvProblems_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            DependencyObject dep = (DependencyObject)e.OriginalSource;

            while ((dep != null) && !(dep is DataGridRow))
            {
                dep = VisualTreeHelper.GetParent(dep);
            }

            UpdateMenuState();

            if (dep is DataGridRow row)
            {
                dgvProblems.Tag = row;

                if (!row.IsSelected)
                {
                    e.Handled = true;

                    var contextMenu = row.ContextMenu ?? dgvProblems.ContextMenu;
                    if (contextMenu != null)
                    {
                        contextMenu.PlacementTarget = row;
                        contextMenu.IsOpen = true;
                    }
                }
            }
        }

        private void UpdateMenuState()
        {
            var state = _vm.GetMenuState();

            if (ctxCopy != null) ctxCopy.IsEnabled = state.hasCopy;
            if (ctxZoomToExtent != null) ctxZoomToExtent.IsEnabled = state.hasRowSelection;
            if (ctxClearSelected != null) ctxClearSelected.IsEnabled = state.hasAnySelection;
        }

        private void UserControl_MouseEnter(object sender, MouseEventArgs e)
        {
            Focus();
            dgvProblems.Focus();
        }

        private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 保留空实现：与原逻辑一致（不执行任何动作）
        }

        private void dgvProblems_Loaded(object sender, RoutedEventArgs e)
        {
            // 保留空实现：与原逻辑一致
        }

        private void DataGridRow_Click(object sender, MouseButtonEventArgs e)
        {
            // 保留空实现：与原逻辑一致
        }
    }

    /// <summary>
    /// ucMatchArrowResult 的业务逻辑 ViewModel
    /// </summary>
    public class ucMatchArrowResultViewModel
    {
        private Dictionary<ObjectId, ProblemItem> _currentProblems = new Dictionary<ObjectId, ProblemItem>();
        private readonly ObservableCollection<ProblemItemViewModel> _observableList = new ObservableCollection<ProblemItemViewModel>();
        private readonly Dictionary<ObjectId, ProblemItemViewModel> _idToViewModelMap = new Dictionary<ObjectId, ProblemItemViewModel>();

        private readonly DataGridCopyOptions _copyOptions = new DataGridCopyOptions
        {
            IncludeHeader = true,
            VisibleColumnsOnly = true,
            KeepDisplayOrder = true,
            KeepSelectionOrder = true
        };

        private bool _isSyncing;
        private Document _doc;
        private DataGrid _grid;

        public IReadOnlyDictionary<ObjectId, ProblemItem> CurrentProblems => _currentProblems;
        public ObservableCollection<ProblemItemViewModel> ObservableList => _observableList;

        public void Initialize(DataGrid grid, Dictionary<ObjectId, ProblemItem> initialData)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _doc = AcadApp.DocumentManager.MdiActiveDocument;
            UpdateData(initialData ?? new Dictionary<ObjectId, ProblemItem>());
        }

        public void UpdateData(Dictionary<ObjectId, ProblemItem> data)
        {
            _currentProblems = data ?? new Dictionary<ObjectId, ProblemItem>();

            _grid.Dispatcher.Invoke(() =>
            {
                _observableList.Clear();
                _idToViewModelMap.Clear();

                int index = 1;
                foreach (var kvp in _currentProblems.Where(p =>
                             !p.Value.IsFixed &&
                             !p.Value.PipeId.IsErased &&
                             !p.Value.PipeId.IsNull))
                {
                    var vm = new ProblemItemViewModel(index++, kvp.Value);
                    _observableList.Add(vm);
                    _idToViewModelMap[kvp.Value.PipeId] = vm;
                }

                _grid.Items.Refresh();
            });
        }

        public void ExecuteCadSelection(bool isZoomTo = false)
        {
            if (_isSyncing) return;
            if (_doc == null || _grid == null) return;

            _isSyncing = true;
            try
            {
                if (_grid.SelectedItems == null || _grid.SelectedItems.Count == 0)
                    return;

                var selectedItems = _grid.SelectedItems
                    .Cast<ProblemItemViewModel>()
                    .Where(vm => vm?.OriginalItem != null)
                    .Select(vm => vm.OriginalItem)
                    .ToList();

                if (selectedItems.Count == 0) return;

                var objectIds = selectedItems.Select(p => p.PipeId).ToArray();

                var sbh = new SelectByHandleService(_doc.Database, _doc.Editor);
                sbh.SelectByHandles(objectIds, isZoomTo);

                GraphicManager.ClearAuxiliaryGraphics();

                if (selectedItems.Count == 1 &&
                    selectedItems[0].Type == ProblemType.OneToMany)
                {
                    GraphicManager.DrawAuxiliaryLines(selectedItems[0].PossibleMatches);
                }
            }
            finally
            {
                _isSyncing = false;
            }
        }

        public void OnImpliedSelectionChanged()
        {
            if (_isSyncing) return;
            if (_grid == null) return;
            if (AcadApp.DocumentManager.MdiActiveDocument == null) return;

            _grid.Dispatcher.BeginInvoke(new Action(() =>
            {
                _isSyncing = true;
                try
                {
                    var ed = AcadApp.DocumentManager.MdiActiveDocument.Editor;
                    var psr = ed.SelectImplied();

                    var selectedIds = new HashSet<ObjectId>();
                    if (psr.Status == PromptStatus.OK)
                    {
                        foreach (var id in psr.Value.GetObjectIds())
                            selectedIds.Add(id);
                    }

                    _grid.SelectedItems.Clear();

                    foreach (var id in selectedIds)
                    {
                        if (_idToViewModelMap.TryGetValue(id, out var vm))
                            _grid.SelectedItems.Add(vm);
                    }
                }
                catch
                {
                    // 可接入日志系统
                }
                finally
                {
                    _isSyncing = false;
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        public void RefreshByService()
        {
            if (_doc == null) return;
            var service = new FlowArrowService(_doc.Database, _doc.Editor, useEditor: false);
            service.Run(Properties.Settings.Default.taskFlowArrow, true);
        }

        public void TryCopy(CopyFormat fmt)
        {
            if (_grid == null) return;

            try
            {
                if (!DataGridCopyHelper.HasSelection(_grid))
                    return;

                DataGridCopyHelper.CopyToClipboard(_grid, fmt, _copyOptions);
            }
            catch
            {
                // 可接入日志系统
            }
        }

        public void UpdateCopyHeaderPolicy()
        {
            if (_grid == null) return;
            _copyOptions.IncludeHeader = (_grid.SelectedItems != null && _grid.SelectedItems.Count > 0);
        }

        public void ToggleRowSelectionFromTag()
        {
            if (_grid?.Tag is DataGridRow row)
            {
                row.IsSelected = !row.IsSelected;
                _grid.Tag = null;
                ExecuteCadSelection(false);
                _grid.Focus();
            }
        }

        public void ClearSelected()
        {
            if (_grid == null) return;
            _grid.SelectedCells.Clear();
            _grid.SelectedItems.Clear();
        }

        public (bool hasCopy, bool hasRowSelection, bool hasAnySelection) GetMenuState()
        {
            if (_grid == null) return (false, false, false);

            bool hasCopy = DataGridCopyHelper.HasSelection(_grid);
            bool hasRow = _grid.SelectedItems != null && _grid.SelectedItems.Count > 0;
            bool hasCell = _grid.SelectedCells != null && _grid.SelectedCells.Count > 0;
            return (hasCopy, hasRow, hasRow || hasCell);
        }
    }

    public class palMatchArrowResult : PaletteSetBase<ucMatchArrowResult, Dictionary<ObjectId, ProblemItem>>
    {
        // 子类单例（不变）
        private static readonly Lazy<palMatchArrowResult> _instance = new Lazy<palMatchArrowResult>(() => new palMatchArrowResult());
        public static palMatchArrowResult Instance => _instance.Value;

        //private Dictionary<ObjectId, ProblemItem> _currentProblems = new Dictionary<ObjectId, ProblemItem>();
        ////public IReadOnlyDictionary<ObjectId, ProblemItem> CurrentProblems => _currentProblems;
        
        // 和userControl的updateData保持一致
        public IReadOnlyDictionary<ObjectId, ProblemItem> CurrentProblems => _currentControl?.CurrentProblems ?? new Dictionary<ObjectId, ProblemItem>();


        private palMatchArrowResult() : base() { }

        // 【新增】：实现抽象属性，提供唯一 GUID（生成新 GUID，避免与基类冲突）
        protected override Guid PaletteGuid => new Guid("7e8d4f9a-5b7c-4890-8a7b-123456789abc");

        // 实现其他抽象方法（不变）
        protected override string GetPaletteTitle() => "管线箭头匹配检查";
        protected override string GetPaletteName() => "MatchArrowPalette";
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
