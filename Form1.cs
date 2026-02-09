using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace IIDXProgressDashboard
{
    // DataGridViewのちらつき防止用拡張メソッド
    public static class ExtensionMethods
    {
        public static void DoubleBuffered(this DataGridView dgv, bool setting)
        {
            Type dgvType = dgv.GetType();
            System.Reflection.PropertyInfo pi = dgvType.GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            pi.SetValue(dgv, setting, null);
        }
    }

    public partial class Form1 : Form
    {
        internal List<PlayRecord> _currentData = new List<PlayRecord>();

        public Form1()
        {
            InitializeComponent();
            SetupGrid();

            // 初期選択
            cmbLevel.SelectedIndex = 0; // "11"
        }

        private void SetupGrid()
        {
            dgvStats.DoubleBuffered(true); // 描画高速化（拡張メソッドが必要なら定義、なければ無視でも可）
            dgvStats.AllowUserToAddRows = false;
            dgvStats.ReadOnly = true;
            dgvStats.RowHeadersVisible = false;
            dgvStats.SelectionMode = DataGridViewSelectionMode.FullRowSelect;

            // カラム定義
            dgvStats.Columns.Clear();
            dgvStats.Columns.Add("Rank", "ランク");
            dgvStats.Columns.Add("Total", "曲数");
            dgvStats.Columns.Add("FC", "FC");
            dgvStats.Columns.Add("EXH", "EXH");
            dgvStats.Columns.Add("Hard", "Hard");
            dgvStats.Columns.Add("Normal", "Clear");
            dgvStats.Columns.Add("Easy", "Easy");
            dgvStats.Columns.Add("Assist", "Assist");
            dgvStats.Columns.Add("Failed", "Failed");
            dgvStats.Columns.Add("NoPlay", "NoPlay");

            // 列幅調整
            dgvStats.Columns["Rank"].Width = 80;
            dgvStats.Columns["Total"].Width = 60;
            // 他は自動調整など
        }

        private void LoadData()
        {
            if (cmbLevel.SelectedItem == null) return;

            int level = int.Parse(cmbLevel.SelectedItem.ToString());

            var manager = new LampManager();
            List<RankStats> stats = new List<RankStats>();

            try
            {
                stats = manager.GetRankStats(level);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"データ読み込みエラー:\n{ex.Message}\nDBファイルが正しい場所にあるか確認してください。");
                return;
            }

            // グリッドに反映
            dgvStats.Rows.Clear();
            foreach (var s in stats)
            {
                int idx = dgvStats.Rows.Add(
                    s.RankName,
                    s.TotalCount,
                    s.FC, s.EXH, s.Hard, s.Normal, s.Easy, s.Assist, s.Failed, s.NoPlay
                );

                // ランプ状況に応じてセルの背景色を変えると見やすいです（お好みで）
                ApplyColor(dgvStats.Rows[idx], s);
            }
        }

        // ヒートマップ風の色付け（例: ハード以上が半分超えてれば赤っぽくするなど）
        // ここではシンプルに、FCがあればFC列を光らせる等の装飾例
        private void ApplyColor(DataGridViewRow row, RankStats s)
        {
            // 例: FC列の文字色を水色に
            if (s.FC > 0) row.Cells["FC"].Style.ForeColor = Color.DarkCyan;
            if (s.EXH > 0) row.Cells["EXH"].Style.ForeColor = Color.OrangeRed;
            if (s.Hard > 0) row.Cells["Hard"].Style.ForeColor = Color.Red;

            // 行全体の背景色をランクごとに変えるなども可能
            if (s.RankName.Contains("地力")) row.DefaultCellStyle.BackColor = Color.White;
            else if (s.RankName.Contains("個人差")) row.DefaultCellStyle.BackColor = Color.WhiteSmoke;
        }

        private void Form1_Load(object sender, EventArgs e)
        {

        }

        private void btnLoad_Click(object sender, EventArgs e)
        {
            LoadData();
        }
    }
}
