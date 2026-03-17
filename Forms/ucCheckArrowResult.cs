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
            
            // 构造函数时获取一次（作为默认）
            doc = AcadApp.DocumentManager.MdiActiveDocument;

            // 让 lvProblems 停靠填充整个控件
            lvProblems.Dock = DockStyle.Fill;

            SetupListViewColumns();

            // 初始化列表
            PopulateListView();
        }

        public void UpdateProblems(List<ProblemItem> newProblems)
        {
            problems = newProblems ?? new List<ProblemItem>();
            PopulateListView();
        }

        // 定义四列
        private void SetupListViewColumns()
        {
            lvProblems.Columns.Clear();

            // 添加列（标题 + 宽度可调整）
            lvProblems.Columns.Add("管线ID", 120);      // PipeID 的 Handle（更易读）
            //lvProblems.Columns.Add("箭头ID", 120);      // ArrowID 的 Handle（如果有）
            lvProblems.Columns.Add("位置", 180);              // Position (x,y,z)
            lvProblems.Columns.Add("问题描述", 300);          // Description

            lvProblems.View = View.Details;
            lvProblems.FullRowSelect = true;
            lvProblems.GridLines = true;
        }

        private void PopulateListView()
        {
            lvProblems.Items.Clear();
            foreach (var problem in problems)
            {
                // 创建一行
                var item = new ListViewItem();

                // 第一列：Pipe Handle（Handle 是更友好的显示方式）
                item.Text = problem.PipeId;

                //// 第二列：Arrow Handle（如果没有匹配箭头，就显示 "无" 或空）
                //string arrowHandle = String.IsNullOrEmpty(problem.ArrowId) ? "无" : problem.ArrowId;
                //item.SubItems.Add(arrowHandle);

                // 第三列：位置（格式化 Point3d）
                string posStr = problem.Location == null 
                    ? "未知" 
                    : $"({problem.Location.X:F2}, {problem.Location.Y:F2})";
                item.SubItems.Add(posStr);

                // 第四列：问题描述
                item.SubItems.Add(problem.Description);

                // 把整个 ProblemItem 对象存到 Tag，点击时可用
                item.Tag = problem;

                lvProblems.Items.Add(item);
            }
        }

        private void ucCheckArrowResult_Load(object sender, EventArgs e)
        {

        }

        private void lvProblems_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (lvProblems.SelectedItems.Count == 0) return;
            var p = (ProblemItem)lvProblems.SelectedItems[0].Tag;

            SelectByHandleCommands sbh = new SelectByHandleCommands();
            sbh.SelectByHandle(p.PipeId);
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
                    Visible = true
                };

                currentControl = new ucCheckArrowResult(problems);
                paletteSet.Add("检查结果", currentControl);
            }

            paletteSet.Visible = true;
            paletteSet.Activate(0);
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
