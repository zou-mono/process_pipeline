using Autodesk.AutoCAD.ApplicationServices;
using process_pipeline.Utils;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace process_pipeline.Forms
{
    public partial class frmCheckArrow : Form
    {
        private readonly List<ProblemItem> problems;
        private readonly Document doc;

        public frmCheckArrow(List<ProblemItem> problems, Document doc)
        {
            InitializeComponent();
            this.problems = problems ?? new List<ProblemItem>();  // 防止 null
            this.doc = doc;

            SetupListViewColumns();

            // 初始化列表
            PopulateListView();
        }

        private void frmCheckArrow_Load(object sender, EventArgs e)
        {

        }

        // 定义四列
        private void SetupListViewColumns()
        {
            lvProblems.Columns.Clear();

            // 添加列（标题 + 宽度可调整）
            lvProblems.Columns.Add("管线ID", 120);      // PipeID 的 Handle（更易读）
            lvProblems.Columns.Add("箭头ID", 120);      // ArrowID 的 Handle（如果有）
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
                item.Text = problem.PipeId.ToString();

                // 第二列：Arrow Handle（如果没有匹配箭头，就显示 "无" 或空）
                string arrowHandle = String.IsNullOrEmpty(problem.ArrowId.ToString()) ? "无" : problem.ArrowId.ToString();
                item.SubItems.Add(arrowHandle);

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
    }
}
