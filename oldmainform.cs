using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IIDXProgressDashboard
{
    internal class oldmainform
    {
        /*
        internal List<PlayRecord> _currentData = new List<PlayRecord>();

        public Form1()
        {
            InitializeComponent();
            // DataGridViewの初期設定
            dgvHistory.AutoGenerateColumns = true;

            // コンボボックスのサンプル値（必要に応じて追加してください）
            cmbDifficulty.Items.AddRange(new string[] { "SPA", "SPH", "SPL", "DPA", "DPH", "DPL" });
            cmbDifficulty.SelectedIndex = 0;
        }

        private void Form1_Load(object sender, EventArgs e)
        {

        }

        private void btnSearch_Click(object sender, EventArgs e)
        {
            try
            {
                string song = txtSongName.Text.Trim();
                string diff = cmbDifficulty.Text.Trim();

                if (string.IsNullOrEmpty(song))
                {
                    MessageBox.Show("曲名の一部を入力してください。");
                    return;
                }

                _currentData = DbHelper.GetHistory(song, diff);

                // グリッドに表示
                // 簡易的にリストをそのままバインドします
                dgvHistory.DataSource = null;
                dgvHistory.DataSource = _currentData;

                if (_currentData.Count == 0)
                {
                    MessageBox.Show("データが見つかりませんでした。");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("エラー: " + ex.Message);
            }
        }

        private void btnShowGraph_Click(object sender, EventArgs e)
        {
            if (_currentData == null || _currentData.Count == 0)
            {
                MessageBox.Show("グラフを表示するデータがありません。検索してください。");
                return;
            }

            // グラフフォームを開く
            var graphForm = new GraphForm(_currentData);
            graphForm.Show();
        }
        */
    }
}
