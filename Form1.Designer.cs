namespace IIDXProgressDashboard
{
    partial class Form1
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
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

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            cmbLevel = new ComboBox();
            dgvStats = new DataGridView();
            btnLoad = new Button();
            ((System.ComponentModel.ISupportInitialize)dgvStats).BeginInit();
            SuspendLayout();
            // 
            // cmbLevel
            // 
            cmbLevel.FormattingEnabled = true;
            cmbLevel.Items.AddRange(new object[] { "11", "12" });
            cmbLevel.Location = new Point(12, 12);
            cmbLevel.Name = "cmbLevel";
            cmbLevel.Size = new Size(151, 23);
            cmbLevel.TabIndex = 0;
            // 
            // dgvStats
            // 
            dgvStats.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            dgvStats.Location = new Point(12, 41);
            dgvStats.Name = "dgvStats";
            dgvStats.Size = new Size(923, 517);
            dgvStats.TabIndex = 1;
            // 
            // btnLoad
            // 
            btnLoad.Location = new Point(169, 12);
            btnLoad.Name = "btnLoad";
            btnLoad.Size = new Size(75, 23);
            btnLoad.TabIndex = 2;
            btnLoad.Text = "読み込み";
            btnLoad.UseVisualStyleBackColor = true;
            btnLoad.Click += btnLoad_Click;
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(947, 570);
            Controls.Add(btnLoad);
            Controls.Add(dgvStats);
            Controls.Add(cmbLevel);
            Name = "Form1";
            Text = "Form1";
            Load += Form1_Load;
            ((System.ComponentModel.ISupportInitialize)dgvStats).EndInit();
            ResumeLayout(false);
        }

        #endregion

        private ComboBox cmbLevel;
        private DataGridView dgvStats;
        private Button btnLoad;
    }
}
