using IIDXProgressDashboard;
using ScottPlot;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IIDXProgressDashboard
{
    public partial class GraphForm : Form
    {
        internal List<PlayRecord> _data;

        internal GraphForm(List<PlayRecord> data)
        {
            InitializeComponent();
            _data = data;
        }

        private void GraphForm_Load(object sender, EventArgs e)
        {
            SetupChart();
        }

        private void SetupChart()
        {
            formsPlot1.Plot.Clear();

            // 日付とスコアのリストを作成
            List<DateTime> dates = new List<DateTime>();
            List<double> scores = new List<double>();
            //List<double> rates = new List<double>();
            List<Double> misscounts = new List<Double>();

            foreach (var record in _data)
            {
                // プロパティ経由で変換済みの日付を取得
                DateTime dt = record.PlayedAtDate;

                // パース失敗(MinValue)のデータはグラフから除外する
                if (dt == DateTime.MinValue) continue;

                dates.Add(dt);
                scores.Add(record.Score);
                //rates.Add(record.GetScoreRate());
                misscounts.Add(record.MissCount);
            }

            // データがない場合は戻る
            if (dates.Count == 0) return;

            // --- スコアの描画 (左軸) ---
            var scorePlot = formsPlot1.Plot.Add.Scatter(dates, scores);
            scorePlot.LegendText = "Score";
            scorePlot.Color = Colors.Blue;
            scorePlot.MarkerSize = 5;

            // 左軸のタイトル設定
            formsPlot1.Plot.Axes.Left.Label.Text = "Score";

            // --- レートの描画 (右軸) ---
            //var ratePlot = formsPlot1.Plot.Add.Scatter(dates, rates);
            //ratePlot.LegendText = "Rate (%)";
            //ratePlot.Color = Colors.Red;
            //ratePlot.MarkerSize = 5;
            var missPlot = formsPlot1.Plot.Add.Scatter(dates, misscounts);
            missPlot.LegendText = "Misses";
            missPlot.Color = Colors.Green;
            missPlot.MarkerSize = 5;

            // 右軸を使う設定
            //ratePlot.Axes.YAxis = formsPlot1.Plot.Axes.Right;
            //formsPlot1.Plot.Axes.Right.Label.Text = "Rate (%)";
            missPlot.Axes.YAxis = formsPlot1.Plot.Axes.Right;
            formsPlot1.Plot.Axes.Right.Label.Text = "Misses";

            // --- 軸の共通設定 ---
            // 横軸を日付形式にする
            formsPlot1.Plot.Axes.DateTimeTicksBottom();

            // 凡例を表示
            formsPlot1.Plot.ShowLegend();

            // 再描画
            formsPlot1.Refresh();
        }
    }
}
