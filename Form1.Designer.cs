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
            txtSongName = new TextBox();
            cmbDifficulty = new ComboBox();
            btnSearch = new Button();
            dgvHistory = new DataGridView();
            btnShowGraph = new Button();
            ((System.ComponentModel.ISupportInitialize)dgvHistory).BeginInit();
            SuspendLayout();
            // 
            // txtSongName
            // 
            txtSongName.Location = new Point(12, 12);
            txtSongName.Name = "txtSongName";
            txtSongName.Size = new Size(776, 23);
            txtSongName.TabIndex = 0;
            // 
            // cmbDifficulty
            // 
            cmbDifficulty.FormattingEnabled = true;
            cmbDifficulty.Items.AddRange(new object[] { "SPN", "SPH", "SPA", "SPL", "DPN", "DPH", "DPA", "DPL" });
            cmbDifficulty.Location = new Point(12, 54);
            cmbDifficulty.Name = "cmbDifficulty";
            cmbDifficulty.Size = new Size(614, 23);
            cmbDifficulty.TabIndex = 1;
            // 
            // btnSearch
            // 
            btnSearch.Location = new Point(632, 54);
            btnSearch.Name = "btnSearch";
            btnSearch.Size = new Size(75, 23);
            btnSearch.TabIndex = 2;
            btnSearch.Text = "検索";
            btnSearch.UseVisualStyleBackColor = true;
            btnSearch.Click += btnSearch_Click;
            // 
            // dgvHistory
            // 
            dgvHistory.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            dgvHistory.Location = new Point(12, 83);
            dgvHistory.Name = "dgvHistory";
            dgvHistory.Size = new Size(776, 355);
            dgvHistory.TabIndex = 3;
            // 
            // btnShowGraph
            // 
            btnShowGraph.Location = new Point(713, 54);
            btnShowGraph.Name = "btnShowGraph";
            btnShowGraph.Size = new Size(75, 23);
            btnShowGraph.TabIndex = 4;
            btnShowGraph.Text = "グラフ表示";
            btnShowGraph.UseVisualStyleBackColor = true;
            btnShowGraph.Click += btnShowGraph_Click;
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(800, 450);
            Controls.Add(btnShowGraph);
            Controls.Add(dgvHistory);
            Controls.Add(btnSearch);
            Controls.Add(cmbDifficulty);
            Controls.Add(txtSongName);
            Name = "Form1";
            Text = "Form1";
            Load += Form1_Load;
            ((System.ComponentModel.ISupportInitialize)dgvHistory).EndInit();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private TextBox txtSongName;
        private ComboBox cmbDifficulty;
        private Button btnSearch;
        private DataGridView dgvHistory;
        private Button btnShowGraph;
    }
}
