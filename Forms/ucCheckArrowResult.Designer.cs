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
            this.lvProblems = new System.Windows.Forms.ListView();
            this.SuspendLayout();
            // 
            // lvProblems
            // 
            this.lvProblems.HideSelection = false;
            this.lvProblems.Location = new System.Drawing.Point(12, 15);
            this.lvProblems.Name = "lvProblems";
            this.lvProblems.Size = new System.Drawing.Size(121, 97);
            this.lvProblems.TabIndex = 0;
            this.lvProblems.UseCompatibleStateImageBehavior = false;
            this.lvProblems.SelectedIndexChanged += new System.EventHandler(this.lvProblems_SelectedIndexChanged);
            // 
            // ucCheckArrowResult
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 12F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.lvProblems);
            this.Name = "ucCheckArrowResult";
            this.Load += new System.EventHandler(this.ucCheckArrowResult_Load);
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.ListView lvProblems;
    }
}
