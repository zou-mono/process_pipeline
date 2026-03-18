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

namespace process_pipeline.Forms
{
    public partial class ucCheckArrowResult : UserControl
    {
        private List<ProblemItem> problems;
        private Document doc;

        public ucCheckArrowResult(List<ProblemItem> problems)
        {
            InitializeComponent();
            this.problems = problems ?? new List<ProblemItem>();  // 防止 null

            tblLayoutPanel.Dock = DockStyle.Fill;
            toolbar.Dock = DockStyle.Fill;

            // 第一行：工具条固定高度
            tblLayoutPanel.RowStyles[0].SizeType = SizeType.Absolute;  
            tblLayoutPanel.RowStyles[0].Height = 25;  // 工具条高度

            // 第二行：ListView 占满剩余
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
        }

        public void UpdateProblems(List<ProblemItem> newProblems)
        {
            problems = newProblems ?? new List<ProblemItem>();
            //SetupDataGridView();
            PopulateDataGridView();
        }

        private void SetupToolbar() {
            //btnReversePolyline.Size = new Size(25, 25);
            //tooltip.SetToolTip(btnReversePolyline, "反转折线（自定义提示样式）");
        }

        // 定义四列
        private void SetupDataGridView()
        {
            dgvProblems.Columns.Clear();

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
            dgvProblems.Columns.Add("colIndex", "NO");
            dgvProblems.Columns["colIndex"].Width = 35;
            dgvProblems.Columns["colIndex"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;

            dgvProblems.Columns.Add("colPipeId", "管线ID");
            dgvProblems.Columns["colPipeId"].Width = 80;

            dgvProblems.Columns.Add("colLocation", "位置");
            dgvProblems.Columns["colLocation"].Width = 150;

            dgvProblems.Columns.Add("colDesc", "问题描述");
            // 最后一列自动填满剩余空间，不用担心右边留白
            dgvProblems.Columns["colDesc"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; 
        }

        private void PopulateDataGridView()
        {
            dgvProblems.Rows.Clear();
            
            int index = 1; // 定义一个从 1 开始的序号计数器

            foreach (var problem in problems)
            {
                // 格式化位置
                string posStr = problem.Location == null 
                    ? "未知" 
                    : $"({problem.Location.X:F2}, {problem.Location.Y:F2})";

                // 注意这里：把 index.ToString() 放在第一个参数位置
                int rowIndex = dgvProblems.Rows.Add(index.ToString(), problem.PipeId, posStr, problem.Description);

                //// 直接添加一行数据，并返回行索引
                //int rowIndex = dgvProblems.Rows.Add(problem.PipeId, posStr, problem.Description);

                // 把整个 ProblemItem 对象存到该行的 Tag 中，点击时可用
                dgvProblems.Rows[rowIndex].Tag = problem;

                index++;
            }

            // 清除默认选中状态
            dgvProblems.ClearSelection(); 
        }

        private void ucCheckArrowResult_Load(object sender, EventArgs e)
        {
            dgvProblems.ClearSelection();
            //toolbar.ShowItemToolTips = true;

            //// 强制刷新 ToolStrip 的 ToolTip
            //toolbar.Invalidate();
            //toolbar.Update();
        }

        private void lvProblems_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {

        }

        private void dgvProblems_SelectionChanged(object sender, EventArgs e)
        {
            // 确保有选中的行
            if (dgvProblems.SelectedRows.Count == 0) return;
            
            // 获取选中的第一行
            var row = dgvProblems.SelectedRows[0];
            
            // 从 Tag 里面取出对象
            if (row.Tag is ProblemItem p)
            {
                SelectByHandleCommands sbh = new SelectByHandleCommands();
                sbh.SelectByHandle(p.PipeId);
            }
        }

        private void btnReversePolyline_Click(object sender, EventArgs e)
        {

        }
    }

    public class palCheckArrow
    {
        private static PaletteSet paletteSet = null;
        //private List<ProblemItem> problems = null; 
        private ucCheckArrowResult currentControl;  // 保存控件引用
        
        // 静态单例字段（延迟创建）
        private static readonly palCheckArrow _instance = new palCheckArrow();
        public static palCheckArrow Instance => _instance;

        private palCheckArrow () : base () {
            // 构造函数里不做复杂初始化
        }

        public void Show(List<ProblemItem> problems)
        {
            var currentDoc = AcadApp.DocumentManager.MdiActiveDocument;
            if (currentDoc == null) return;

            if (paletteSet == null || paletteSet.IsDisposed)
            {
                // 必须提供一个 Guid 作为 toolId（可以随便生成一个固定值）
                Guid paletteGuid = Guid.NewGuid(); // 随便生成一个，不要重复即可

                paletteSet = new PaletteSet("管线箭头检查", "PipeCheckPalette", paletteGuid)
                {
                    DockEnabled = DockSides.Left | DockSides.Right | DockSides.Top | DockSides.Bottom,
                    MinimumSize = new Size(600, 400),
                    Size = new System.Drawing.Size(600, 750),           // 初始稍大
                    Visible = true
                };

                currentControl = new ucCheckArrowResult(problems);
                paletteSet.Add("检查结果", currentControl);
            }
            else {
                Update(problems);
            }

            paletteSet.Size = new System.Drawing.Size(601, 751);  // 故意微调一次，强制布局
            paletteSet.Visible = true;
            paletteSet.Activate(0);

            // 关键两行：强制抢焦点
            paletteSet.Focus();
            if (currentControl != null && !currentControl.IsDisposed)
            {
                currentControl.Focus();
            }
        }

        public void Hide()
        {
            if (paletteSet != null && !paletteSet.IsDisposed)
                paletteSet.Visible = false;
        }

        public void Update(List<ProblemItem> problems)
        {
            if (paletteSet == null || paletteSet.IsDisposed || currentControl == null || currentControl.IsDisposed)
            {
                // 如果面板不存在或已销毁，直接调用 Show 重新创建
                currentControl = new ucCheckArrowResult(problems);
                Show(problems);
                return;
            }

            // 直接更新已存在的控件
            currentControl.UpdateProblems(problems);
        }

    }
}
