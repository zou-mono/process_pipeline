using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Windows;
using process_pipeline.Core;
using process_pipeline.Utils;
using process_pipeline.Commands;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using Autodesk.AutoCAD.Geometry;

namespace process_pipeline.Forms
{
    // 视图模型类
    public partial class ucCheckArrowResult : UserControl
    {
        private bool _isUpdatingData = false;  // 全局锁，确定是不是要刷新DataGridView
        private List<ProblemItem> _currentProblems = new List<ProblemItem>();
        private Document doc;
        private SortableBindingList<ProblemItemViewModel> _sortableList;

        // 公开属性（只读），外部只能读取，不能直接修改
        public IReadOnlyList<ProblemItem> CurrentProblems => _currentProblems.AsReadOnly();

        // 自定义事件：当 problems 变化时触发
        public event EventHandler ProblemsChanged;

        public ucCheckArrowResult(List<ProblemItem> problems)
        {
            InitializeComponent();
            _currentProblems = problems ?? new List<ProblemItem>();  // 防止 null

            tblLayoutPanel.Dock = DockStyle.Fill;
            toolbar.Dock = DockStyle.Top;

            // 第一行：工具条固定高度
            tblLayoutPanel.RowStyles[0].SizeType = SizeType.Absolute;  
            tblLayoutPanel.RowStyles[0].Height = 25;  // 工具条高度

            // 第二行：DataGridView占满剩余
            tblLayoutPanel.RowStyles[1].SizeType = SizeType.Percent;  
            tblLayoutPanel.RowStyles[1].Height = 100F;  // 工具条高度

            // 构造函数时获取一次（作为默认）
            doc = AcadApp.DocumentManager.MdiActiveDocument;

            // 让 dgv停靠填充整个控件
            dgvProblems.Dock = DockStyle.Fill;
           
            // 1. 初始化表格列和现代样式
            SetupDataGridView();

            SetupToolbar();

            // 2. 填充数据
            PopulateDataGridView();

            // 订阅自己的 ProblemsChanged 事件（自动刷新 UI）
            ProblemsChanged += (s, e) => PopulateDataGridView();

            Disposed += (s, e) =>
            {
                // 取消所有事件订阅，释放资源
                ProblemsChanged = null;
                //doc = null; // 释放Document引用
                GraphicManager.ClearAuxiliaryGraphics(); // 控件销毁时，务必擦除屏幕上的临时线
            };
        }

        public void UpdateProblems(List<ProblemItem> newProblems)
        {
            _currentProblems = newProblems ?? new List<ProblemItem>();
            //SetupDataGridView();
            //PopulateDataGridView();
            // 触发事件（通知所有订阅者，包括自己）
            OnProblemsChanged(EventArgs.Empty);
        }

        // 保护方法：触发 ProblemsChanged 事件（标准事件模式）
        protected virtual void OnProblemsChanged(EventArgs e)
        {
            ProblemsChanged?.Invoke(this, e);
        }

        private void SetupToolbar() {
            //btnReversePolyline.Size = new Size(25, 25);
            //tooltip.SetToolTip(btnReversePolyline, "反转折线（自定义提示样式）");
        }

        // 定义四列
        private void SetupDataGridView()
        {
            dgvProblems.Columns.Clear();
            dgvProblems.AutoGenerateColumns = false; // 禁止自动生成列

            // ================= 基础行为设置 =================
            dgvProblems.AllowUserToAddRows = false;      // 隐藏底部空白新增行
            dgvProblems.AllowUserToDeleteRows = false;
            dgvProblems.AllowUserToResizeRows = false;
            dgvProblems.ReadOnly = true;                 // 只读
            dgvProblems.SelectionMode = DataGridViewSelectionMode.FullRowSelect; // 整行选中
            dgvProblems.MultiSelect = true;             // 单选
            dgvProblems.RowHeadersVisible = false;       // 隐藏最左侧的自带行头(小箭头那列)
            dgvProblems.BackgroundColor = Color.White;   // 背景色
            dgvProblems.BorderStyle = BorderStyle.None;

            // ================= 现代风格表头设置 =================
            dgvProblems.EnableHeadersVisualStyles = false; // 【关键】必须关闭，才能自定义表头颜色
            //dgvProblems.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(41, 57, 85); // 藏青色背景
            dgvProblems.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(68, 68, 68); 
            dgvProblems.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;                // 白色文字
            dgvProblems.ColumnHeadersDefaultCellStyle.Font = new System.Drawing.Font("微软雅黑", 10F, FontStyle.Bold); // 加粗
            dgvProblems.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter; // 居中
            dgvProblems.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            //dgvProblems.ColumnHeadersHeight = 35;
            dgvProblems.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;

            // ================= 数据行样式设置 =================
            dgvProblems.GridColor = Color.LightGray;     // 浅灰色网格线
            //dgvProblems.RowTemplate.Height = 30;         // 行高
            dgvProblems.DefaultCellStyle.Font = new System.Drawing.Font("微软雅黑", 9F, FontStyle.Regular);
            dgvProblems.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 120, 215); // 选中时的Win10蓝
            // 斑马线交替行颜色（浅灰），让表格更好看
            dgvProblems.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(245, 245, 245); 

            // ================= 添加列 =================
            dgvProblems.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "colIndex",
                HeaderText = "NO",
                DataPropertyName = "NO", // 绑定匿名类的NO属性
                Width = 35,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter }
            });

            dgvProblems.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "colPipeId",
                HeaderText = "管线ID",
                DataPropertyName = "PipeId",
                Width = 80
            });

            dgvProblems.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "colLocation",
                HeaderText = "位置",
                DataPropertyName = "Location",
                Width = 120
            });

            dgvProblems.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "colDesc",
                HeaderText = "问题描述",
                DataPropertyName = "Description",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 100
            });            
            
            foreach (DataGridViewColumn col in dgvProblems.Columns)
            {
                col.SortMode = DataGridViewColumnSortMode.Automatic;  // 默认就是，但显式设置更保险
            }

            // 增加这个事件订阅：每次数据源绑定彻底完成后，强行清空选中状态
            dgvProblems.DataBindingComplete += (s, e) =>
            {
                // 必须同时清空 CurrentCell 和 Selection
                dgvProblems.CurrentCell = null; 
                dgvProblems.ClearSelection();
            };

           // 2. 【核心修改】：订阅鼠标点击事件
           dgvProblems.CellMouseClick += DgvProblems_CellMouseClick;

           // 3. 【强烈建议】：订阅键盘事件（如果用户习惯用键盘上下键切换查看管线，这个很有用）
           dgvProblems.KeyUp += DgvProblems_KeyUp;
        }

        private void PopulateDataGridView()
        {
            // 转换为绑定源
            var bindableList = _currentProblems
            .Where(p => p.IsFixed == false) // 先筛选：只保留未修复的原始项
            .Select((p, _ind) => new ProblemItemViewModel // 再生成ViewModel，_ind是筛选后的索引
            {
                NO = _ind + 1, // 此时索引从0开始，+1后就是1、2、3...连续增长
                IsFixed = p.IsFixed,
                PipeId = p.PipeId.Handle.ToString(),
                Location = p.Location == null ? "未知" : $"({p.Location.X:F2}, {p.Location.Y:F2})",
                Description = p.Description,
                OriginalItem = p // 保留原始对象
            })
            .ToList();

            _sortableList = new SortableBindingList<ProblemItemViewModel>(bindableList);
            // 绑定数据源（避免手动Add）
            dgvProblems.DataSource = _sortableList;

            // 清除默认选中状态
            dgvProblems.ClearSelection(); 
        }

        private void DgvProblems_CellMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            // e.RowIndex == -1 表示点击的是表头（排序操作），直接忽略！
            if (e.RowIndex < 0) return;

            ExecuteCadSelection();
        }

        // 支持键盘上下键切换（可选，但体验更好）
        private void DgvProblems_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
            {
                ExecuteCadSelection();
            }
        }

        // 将原先 SelectionChanged 里的 CAD 跳转逻辑提炼成独立方法
        private void ExecuteCadSelection()
        {
            // 确保有选中的行
            if (dgvProblems.SelectedRows.Count == 0) return;

            // 获取所有选中的 ProblemItem
            var selectedItems = dgvProblems.SelectedRows
                .Cast<DataGridViewRow>()
                .Select(row => row.DataBoundItem as ProblemItemViewModel)
                .Where(vm => vm != null && vm.OriginalItem != null)
                .Select(vm => vm.OriginalItem)
                .ToList();

            if (selectedItems.Count == 0) return;

            var objectIds = selectedItems.Select(p => p.PipeId).ToArray();
            SelectByHandleCommands sbh = new SelectByHandleCommands();

            if (objectIds.Count() > 500)  // 太多了就不计算整体Extent
            {
                sbh.SelectByHandles(objectIds, false);   // 你的跳转选中函数
            }
            else
            {
                sbh.SelectByHandles(objectIds);   // 你的跳转选中函数
            }

            if (selectedItems.Count == 1) {
                if (selectedItems[0].Type == ProblemType.OneToMany) { 
                    List<MatchItem> PossibleMatches = selectedItems[0].PossibleMatches;
                    GraphicManager.DrawAuxiliaryLines(PossibleMatches);
                }
            }
        }

        private void ucCheckArrowResult_Load(object sender, EventArgs e)
        {
            dgvProblems.ClearSelection();
            //this.AutoScroll = true;               // UserControl 本身允许滚动（备用）
            //dgvProblems.Dock = DockStyle.Fill;
            //dgvProblems.ScrollBars = ScrollBars.Both;   // 务必明确设置
        }

        // 拦截 1：数据绑定或重绘完成时，强行清空选中
        private void DgvProblems_DataBindingComplete(object sender, DataGridViewBindingCompleteEventArgs e)
        {
            dgvProblems.CurrentCell = null;
            dgvProblems.ClearSelection();
        }

        //private void dgvProblems_SelectionChanged(object sender, EventArgs e)
        //{
        //    if (_isUpdatingData) return;  // 拦截非用户主动点击的触发
        //    //doc = AcadApp.DocumentManager.MdiActiveDocument;
        //    //if (doc is null || doc.IsDisposed) return;

        //    // 确保有选中的行
        //    if (dgvProblems.SelectedRows.Count == 0) return;
 
        //    // 获取所有选中的 ProblemItem
        //    var selectedItems = dgvProblems.SelectedRows
        //        .Cast<DataGridViewRow>()
        //        .Select(row => row.DataBoundItem as ProblemItemViewModel)
        //        .Where(vm => vm != null && vm.OriginalItem != null)
        //        .Select(vm => vm.OriginalItem)
        //        .ToList();

        //    if (selectedItems.Count == 0) return;

        //    var objectIds = selectedItems.Select(p => p.PipeId).ToArray();
        //    SelectByHandleCommands sbh = new SelectByHandleCommands();

        //    if (objectIds.Count() > 500)
        //    {
        //        sbh.SelectByHandles(objectIds, false);   // 你的跳转选中函数
        //    }
        //    else
        //    {
        //        sbh.SelectByHandles(objectIds);   // 你的跳转选中函数
        //    }
        //}

        private void btnReversePolyline_Click(object sender, EventArgs e)
        {
            // 修复成功后，移除已修复的问题
            //_currentProblems.RemoveAll(p => p.IsFixed);  

            // 关键：通过 UpdateProblems 更新（会自动触发 ProblemsChanged 事件）
            UpdateProblems(_currentProblems);
        }

        private void btn_Refresh_Click(object sender, EventArgs e)
        {
            var service = new FlowArrowService(doc.Database, doc.Editor, useEditor: false);
            var problems = service.RunChecker();
            
            palCheckArrow.Instance.Update(problems);
        }
    }

    public class palCheckArrow : IDisposable
    {
        private static PaletteSet _paletteSet = null;
        //private List<ProblemItem> problems = null; 
        private ucCheckArrowResult _currentControl;  // 保存控件引用
        private List<ProblemItem> _currentProblems = new List<ProblemItem>();

        // 1. 固定GUID（替换你之前的随机GUID）
        private readonly Guid _paletteGuid = new Guid("7e8d4f9a-5b7c-4890-8a7b-123456789abc"); // 随便生成一个，不要重复即可

        // 2. 静态懒汉单例（避免AutoCAD启动时过早初始化）
        private static readonly Lazy<palCheckArrow> _instance = new Lazy<palCheckArrow>(() => new palCheckArrow());
        public static palCheckArrow Instance => _instance.Value;

        public IReadOnlyList<ProblemItem> CurrentProblems => _currentProblems?.AsReadOnly() ?? new List<ProblemItem>().AsReadOnly();

        private bool _needRefresh = false;
        private EventHandler _idleHandler;

        private palCheckArrow()
        {  
             _currentProblems = new List<ProblemItem>();
            // 构造函数里不做复杂初始化
            AcadApp.DocumentManager.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
        }

        private void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs e)
        {
            Dispose();
        }

        public void Show(List<ProblemItem> initialProblems)
        {
            var currentDoc = AcadApp.DocumentManager.MdiActiveDocument;
            if (currentDoc == null) return;

            //_currentProblems = initialProblems ?? new List<ProblemItem>();
            Update(initialProblems);

            if (_paletteSet == null || _paletteSet.IsDisposed)
            {
                //Guid _paletteGuid = Guid.NewGuid();
                _paletteSet = new PaletteSet("管线箭头检查", "PipeCheckPalette", _paletteGuid)
                {
                    DockEnabled = DockSides.Left | DockSides.Right | DockSides.Top | DockSides.Bottom,
                    MinimumSize = new Size(600, 400),
                    Size = new System.Drawing.Size(600, 700),           // 初始稍大
                    Visible = true
                };

                // 释放旧控件（如果有）
                if (_currentControl != null && !_currentControl.IsDisposed)
                {
                    _currentControl.Dispose();
                }

                // 创建新控件并订阅其Disposed事件
                _currentControl = new ucCheckArrowResult(initialProblems);
                _currentControl.Disposed += (s, e) => _currentControl = null;  // 控件释放后置空
                _paletteSet.Add("检查结果", _currentControl);
            }
            else { 
                // 【核心修复】：如果面板已经存在，必须调用控件的方法更新数据！
                if (_currentControl != null && !_currentControl.IsDisposed)
                {
                    // 调用你写好的 UpdateProblems 方法来刷新 DataGridView
                    _currentControl.UpdateProblems(initialProblems);
                }
            }

            //_paletteSet.Size = new System.Drawing.Size(601, 751);  // 故意微调一次，强制布局
            _paletteSet.Visible = true;
            _paletteSet.Activate(0);

            //如果还没订阅 Idle，就订阅一次（全局只订阅一次）
            if (_idleHandler == null)
            {
                _idleHandler = (s, e) =>
                {
                    if (_needRefresh)
                    {
                        _needRefresh = false;
                        AcadApp.Idle -= _idleHandler;  // 只执行一次就卸载

                        if (_paletteSet != null && !_paletteSet.IsDisposed)
                        {
                            var originalSize = _paletteSet.Size;
                            _paletteSet.Size = new Size(originalSize.Width + 1, originalSize.Height);
                            _paletteSet.Size = originalSize;  // 恢复

                            if (_currentControl != null && !_currentControl.IsDisposed)
                            {
                                _currentControl.PerformLayout();
                                _currentControl.Refresh();
                                // 或直接针对 DataGridView：
                                // var dgv = _currentControl.Controls.OfType<DataGridView>().FirstOrDefault();
                                // if (dgv != null) { dgv.PerformLayout(); dgv.Refresh(); }
                            }
                        }
                    }
                };
                AcadApp.Idle += _idleHandler;
            }

            _needRefresh = true;  // 标记需要刷新，下次 Idle 时执行

            // 关键两行：强制抢焦点
            _paletteSet.Focus();
            if (_currentControl != null && !_currentControl.IsDisposed)
            {
                _currentControl.Focus();
            }
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

        // 新增：当外部反转成功时调用
        //public void MarkProblemFixed(ObjectId pipeId)
        //{
        //    if (pipeId.IsNull) return;

        //    var item = _currentProblems.FirstOrDefault(p => p.PipeId == pipeId);
        //    if (item != null)
        //    {
        //        //item.IsFixed = true;
        //        _currentProblems.Remove(item);  // 或者保留但标记 IsFixed
        //        _currentControl?.UpdateProblems(_currentProblems);  // 通知 UI 刷新
        //    }
        //}
        public void MarkProblemFixed(IEnumerable<ObjectId> _pipeIds, bool _isFixed)
        {
            ObjectId[] pipeIds = _pipeIds.ToArray();
            if (pipeIds == null || pipeIds.Length == 0) return;

            // 过滤掉无效 ID
            var validIds = pipeIds.Where(id => !id.IsNull).ToHashSet();

            if (validIds.Count == 0) return;

            //// 一次性移除所有匹配的项（用 HashSet 加速查找）
            //int removedCount = _currentProblems.RemoveAll(p => validIds.Contains(p.PipeId));

            HashSet<ObjectId> pipeIdSet = new HashSet<ObjectId>(pipeIds);

            int fixedCount = 0;
            foreach (var p in _currentProblems)
            {
                if (!p.IsFixed && pipeIdSet.Contains(p.PipeId))
                {
                    p.IsFixed = _isFixed;
                    fixedCount++;
                }
            }

            // 如果有修复的，才刷新 UI
            if (fixedCount > 0)
            {
                _currentControl?.UpdateProblems(_currentProblems);
            }
        }

        public void RefreshProblems()
        {
            Document Doc = AcadApp.DocumentManager.MdiActiveDocument;
            
            var service = new FlowArrowService(Doc.Database, Doc.Editor);
            List<ProblemItem> newProblems = service.RunChecker();  // 完整检查

            if (newProblems != null)
            {
                // 只更新内存和表格 UI，不抢焦点，不 Regen
                Update(newProblems);
            }
        }

        public void Update(List<ProblemItem> _newProblems)
        {
            //if (_paletteSet == null || _paletteSet.IsDisposed || _currentControl == null || _currentControl.IsDisposed)
            //{
            //    // 如果面板不存在或已销毁，直接调用 Show 重新创建
            //    //currentControl = new ucCheckArrowResult(CurrentProblems);
            //    Show(_currentProblems);
            //    return;
            //}

            //// 直接更新已存在的控件
            //_currentControl.UpdateProblems(_currentProblems);
            _currentProblems = new List<ProblemItem>();

            if (_newProblems != null)
            {
                //List<ProblemItem> newProblems = _newProblems
                //    .Where(p => p.IsFixed == false)
                //    .ToList();
                _currentProblems.AddRange(_newProblems);
            }

            if (_currentControl != null && !_currentControl.IsDisposed)
            {
                if (_currentControl.InvokeRequired)
                {
                    _currentControl.Invoke(new Action(() => 
                    {
                        _currentControl.UpdateProblems(_currentProblems);
                    }));
                }
                else
                {
                    _currentControl.UpdateProblems(_currentProblems);
                }
            }

            //_currentControl?.UpdateProblems(_currentProblems);  // 通知UI刷新
        }

        // 实现IDisposable（核心：手动释放PaletteSet和控件）
        public void Dispose()
        {
            try
            {
                // 释放控件
                if (_currentControl != null && !_currentControl.IsDisposed)
                {
                    _currentControl.Dispose();
                }
                _currentControl = null;

                // 释放PaletteSet（AutoCAD的PaletteSet支持Dispose）
                if (_paletteSet != null && !_paletteSet.IsDisposed)
                {
                    _paletteSet.Visible = false;
                    _paletteSet.Close();
                    _paletteSet.Dispose();
                }
                _paletteSet = null;

                // 清空数据
                _currentProblems = null;
                _idleHandler = null;
                _needRefresh = false;
            }
            catch (System.Exception ex)
            {
                //// 记录日志，避免崩溃
                //Application.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
                //    $"\n关闭 PaletteSet 失败：{ex.Message}");
            }
        }
    }

        public class ProblemItemViewModel
        {
            public int NO { get; set; }
            public bool IsFixed { get; set; }
            public string PipeId { get; set; }
            public string Location { get; set; }
            public string Description { get; set; }
            public ProblemItem OriginalItem { get; set; } // 存储原始对象
        }
}
