namespace process_pipeline.Forms
{
    partial class ucCheckArrowResult
    {
        /// <summary> 
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary> 
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Component Designer generated code

        /// <summary> 
        /// Required method for Designer support - do not modify 
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(ucCheckArrowResult));
            this.dgvProblems = new System.Windows.Forms.DataGridView();
            this.tblLayoutPanel = new System.Windows.Forms.TableLayoutPanel();
            this.toolbar = new System.Windows.Forms.ToolStrip();
            this.btnReversePolyline = new System.Windows.Forms.ToolStripButton();
            this.btn_Refresh = new System.Windows.Forms.ToolStripButton();
            ((System.ComponentModel.ISupportInitialize)(this.dgvProblems)).BeginInit();
            this.tblLayoutPanel.SuspendLayout();
            this.toolbar.SuspendLayout();
            this.SuspendLayout();
            // 
            // dgvProblems
            // 
            this.dgvProblems.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dgvProblems.Location = new System.Drawing.Point(3, 66);
            this.dgvProblems.Name = "dgvProblems";
            this.dgvProblems.RowTemplate.Height = 23;
            this.dgvProblems.Size = new System.Drawing.Size(194, 71);
            this.dgvProblems.TabIndex = 0;
            this.dgvProblems.SelectionChanged += new System.EventHandler(this.dgvProblems_SelectionChanged);
            // 
            // tblLayoutPanel
            // 
            this.tblLayoutPanel.ColumnCount = 1;
            this.tblLayoutPanel.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 48.5F));
            this.tblLayoutPanel.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 51.5F));
            this.tblLayoutPanel.Controls.Add(this.toolbar, 0, 0);
            this.tblLayoutPanel.Controls.Add(this.dgvProblems, 0, 1);
            this.tblLayoutPanel.Location = new System.Drawing.Point(84, 69);
            this.tblLayoutPanel.Name = "tblLayoutPanel";
            this.tblLayoutPanel.RowCount = 2;
            this.tblLayoutPanel.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 33.33333F));
            this.tblLayoutPanel.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 66.66666F));
            this.tblLayoutPanel.Size = new System.Drawing.Size(353, 189);
            this.tblLayoutPanel.TabIndex = 1;
            // 
            // toolbar
            // 
            this.toolbar.ImageScalingSize = new System.Drawing.Size(25, 25);
            this.toolbar.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.btnReversePolyline,
            this.btn_Refresh});
            this.toolbar.Location = new System.Drawing.Point(0, 0);
            this.toolbar.Name = "toolbar";
            this.toolbar.RenderMode = System.Windows.Forms.ToolStripRenderMode.System;
            this.toolbar.Size = new System.Drawing.Size(353, 32);
            this.toolbar.TabIndex = 1;
            this.toolbar.Text = "toolStrip1";
            // 
            // btnReversePolyline
            // 
            this.btnReversePolyline.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.btnReversePolyline.Image = ((System.Drawing.Image)(resources.GetObject("btnReversePolyline.Image")));
            this.btnReversePolyline.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.btnReversePolyline.Name = "btnReversePolyline";
            this.btnReversePolyline.Size = new System.Drawing.Size(29, 29);
            this.btnReversePolyline.Text = "反转管线绘制方向";
            this.btnReversePolyline.TextImageRelation = System.Windows.Forms.TextImageRelation.TextAboveImage;
            this.btnReversePolyline.Click += new System.EventHandler(this.btnReversePolyline_Click);
            // 
            // btn_Refresh
            // 
            this.btn_Refresh.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.btn_Refresh.Image = ((System.Drawing.Image)(resources.GetObject("btn_Refresh.Image")));
            this.btn_Refresh.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.btn_Refresh.Name = "btn_Refresh";
            this.btn_Refresh.Size = new System.Drawing.Size(29, 29);
            this.btn_Refresh.Text = "toolStripButton1";
            this.btn_Refresh.Click += new System.EventHandler(this.btn_Refresh_Click);
            // 
            // ucCheckArrowResult
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 12F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.tblLayoutPanel);
            this.Name = "ucCheckArrowResult";
            this.Size = new System.Drawing.Size(471, 338);
            this.Load += new System.EventHandler(this.ucCheckArrowResult_Load);
            ((System.ComponentModel.ISupportInitialize)(this.dgvProblems)).EndInit();
            this.tblLayoutPanel.ResumeLayout(false);
            this.tblLayoutPanel.PerformLayout();
            this.toolbar.ResumeLayout(false);
            this.toolbar.PerformLayout();
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.DataGridView dgvProblems;
        private System.Windows.Forms.TableLayoutPanel tblLayoutPanel;
        private System.Windows.Forms.ToolStrip toolbar;
        private System.Windows.Forms.ToolStripButton btnReversePolyline;
        private System.Windows.Forms.ToolStripButton btn_Refresh;
    }
}
