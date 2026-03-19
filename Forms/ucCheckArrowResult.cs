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

namespace process_pipeline.Forms
{
    // 视图模型类
    public partial class ucCheckArrowResult : UserControl
    {
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
        }

        private void PopulateDataGridView()
        {
            // 转换为绑定源
            var bindableList = _currentProblems.Select((p, _ind) => new ProblemItemViewModel
            {
                NO = _ind + 1,
                PipeId = p.PipeId.Handle.ToString(),
                Location = p.Location == null ? "未知" : $"({p.Location.X:F2}, {p.Location.Y:F2})",
                Description = p.Description,
                OriginalItem = p // 保存原始对象
            }).ToList();

            _sortableList = new SortableBindingList<ProblemItemViewModel>(bindableList);
            // 绑定数据源（避免手动Add）
            dgvProblems.DataSource = _sortableList;

            // 清除默认选中状态
            dgvProblems.ClearSelection(); 
        }

        private void ucCheckArrowResult_Load(object sender, EventArgs e)
        {
            dgvProblems.ClearSelection();
        }

        private void dgvProblems_SelectionChanged(object sender, EventArgs e)
        {
            doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc is null || doc.IsDisposed) return;

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

            // 执行选中管线逻辑（记得加事务保护）
            using (var docLock = doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                try
                {
                    if (selectedItems.Count == 1)
                    {
                        ProblemItem op = selectedItems[0];
                        SelectByHandleCommands sbh = new SelectByHandleCommands();
                        sbh.SelectByHandle(op.PipeId);   // 你的跳转选中函数
                    }
                    else 
                    { 
                        // 多选：只选中实体，不跳转视图
                        var objectIds = selectedItems.Select(p => p.PipeId).ToArray();

                        // 使用 SetImpliedSelection 只选中实体（不跳转）
                        doc.Editor.SetImpliedSelection(objectIds);

                        // 可选：高亮所有选中的实体
                        foreach (var oid in objectIds)
                        {
                            if (!oid.IsNull)
                            {
                                var ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                                ent?.Highlight();
                            }
                        }
                    }
                    tr.Commit();
                }
                catch (Exception ex)
                {
                    //AcadApp.ShowAlertDialog($"选中管线失败：{ex.Message}");
                    tr.Abort();
                }
            }
        }

        private void btnReversePolyline_Click(object sender, EventArgs e)
        {
            // 修复成功后，移除已修复的问题
            _currentProblems.RemoveAll(p => p.IsFixed);  

            // 关键：通过 UpdateProblems 更新（会自动触发 ProblemsChanged 事件）
            UpdateProblems(_currentProblems);
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

        //// 静态单例字段（延迟创建）
        //private static readonly palCheckArrow _instance = new palCheckArrow();

        // 封装CurrentProblems，禁止外部直接修改
        //public List<ProblemItem> CurrentProblems { get; private set; } = new List<ProblemItem>();

        public IReadOnlyList<ProblemItem> CurrentProblems => _currentProblems?.AsReadOnly() ?? new List<ProblemItem>().AsReadOnly();

        private palCheckArrow () {
            // 构造函数里不做复杂初始化
        }

        public void Show(List<ProblemItem> initialProblems)
        {
            var currentDoc = AcadApp.DocumentManager.MdiActiveDocument;
            if (currentDoc == null) return;

            //_currentProblems = initialProblems ?? new List<ProblemItem>();

            Update(initialProblems);

            if (_paletteSet == null || _paletteSet.IsDisposed)
            {
                _paletteSet = new PaletteSet("管线箭头检查", "PipeCheckPalette", _paletteGuid)
                {
                    DockEnabled = DockSides.Left | DockSides.Right | DockSides.Top | DockSides.Bottom,
                    MinimumSize = new Size(600, 400),
                    Size = new System.Drawing.Size(600, 750),           // 初始稍大
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

            _paletteSet.Size = new System.Drawing.Size(601, 751);  // 故意微调一次，强制布局
            _paletteSet.Visible = true;
            _paletteSet.Activate(0);

            // 关键两行：强制抢焦点
            _paletteSet.Focus();
            if (_currentControl != null && ! _currentControl.IsDisposed)
            {
                _currentControl.Focus();
            }
        }

        public void Hide()
        {
            if (_paletteSet != null && !_paletteSet.IsDisposed)
                _paletteSet.Visible = false;
        }

        // 新增：当外部反转成功时调用
        public void MarkProblemFixed(ObjectId pipeId)
        {
            if (pipeId.IsNull) return;

            var item = _currentProblems.FirstOrDefault(p => p.PipeId == pipeId);
            if (item != null)
            {
                //item.IsFixed = true;
                _currentProblems.Remove(item);  // 或者保留但标记 IsFixed
                _currentControl?.UpdateProblems(_currentProblems);  // 通知 UI 刷新
            }
        }

        public void Update(List<ProblemItem> newProblems)
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

            _currentProblems.Clear();
            if (newProblems != null)
                _currentProblems.AddRange(newProblems);

            _currentControl?.UpdateProblems(_currentProblems);  // 通知UI刷新
        }

        // 实现IDisposable（核心：手动释放PaletteSet和控件）
        public void Dispose()
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
                _paletteSet.Dispose();
            }
            _paletteSet = null;

            // 清空数据
            _currentProblems = null;
        }
    }

    public class ProblemItemViewModel
    {
        public int NO { get; set; }
        public string PipeId { get; set; }
        public string Location { get; set; }
        public string Description { get; set; }
        public ProblemItem OriginalItem { get; set; } // 存储原始对象
    }
}
