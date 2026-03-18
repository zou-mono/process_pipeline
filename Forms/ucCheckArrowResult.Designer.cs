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
            this.dgvProblems = new System.Windows.Forms.DataGridView();
            ((System.ComponentModel.ISupportInitialize)(this.dgvProblems)).BeginInit();
            this.SuspendLayout();
            // 
            // dgvProblems
            // 
            this.dgvProblems.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dgvProblems.Location = new System.Drawing.Point(65, 96);
            this.dgvProblems.Name = "dgvProblems";
            this.dgvProblems.RowTemplate.Height = 23;
            this.dgvProblems.Size = new System.Drawing.Size(240, 150);
            this.dgvProblems.TabIndex = 0;
            this.dgvProblems.CellContentClick += new System.Windows.Forms.DataGridViewCellEventHandler(this.lvProblems_CellContentClick);
            this.dgvProblems.SelectionChanged += new System.EventHandler(this.dgvProblems_SelectionChanged);
            // 
            // ucCheckArrowResult
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 12F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.dgvProblems);
            this.Name = "ucCheckArrowResult";
            this.Size = new System.Drawing.Size(403, 367);
            this.Load += new System.EventHandler(this.ucCheckArrowResult_Load);
            ((System.ComponentModel.ISupportInitialize)(this.dgvProblems)).EndInit();
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.DataGridView dgvProblems;
    }
}
