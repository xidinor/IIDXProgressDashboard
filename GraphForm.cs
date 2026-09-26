using IIDXProgressDashboard.Dashboard;
using ScottPlot;
using ScottPlot.WinForms;

namespace IIDXProgressDashboard;

public partial class GraphForm : Form
{
    private ChartView chart;
    private readonly FormsPlot bpPlot = new() { Dock = DockStyle.Fill };
    private readonly CheckBox exclude = new() { Text = "ミスカウント取得不可を除外", AutoSize = true };
    private readonly System.Windows.Forms.Label summary = new() { AutoSize = true };
    private readonly DataGridView historyGrid = Form1.NewGrid();
    private readonly ToolTip tooltip = new();
    private HistoryPoint[] visible = [];
    private string tooltipText = "";

    public GraphForm(ChartView chart, bool excludeMissingBp = false)
    {
        this.chart = chart;
        InitializeComponent();
        Controls.Clear(); MinimumSize = new Size(800, 650); Size = new Size(1100, 900);
        formsPlot1.Dock = DockStyle.Fill;
        // 共通X軸の範囲を常に1..Nに保つ。独立ズームによる上下の番号ずれを避ける。
        formsPlot1.UserInputProcessor.Disable(); bpPlot.UserInputProcessor.Disable();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 35));
        layout.RowStyles.Add(new(SizeType.Percent, 35)); layout.RowStyles.Add(new(SizeType.Percent, 30));
        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        header.Controls.AddRange([exclude, summary]);
        layout.Controls.Add(header, 0, 0); layout.Controls.Add(formsPlot1, 0, 1); layout.Controls.Add(bpPlot, 0, 2); layout.Controls.Add(historyGrid, 0, 3);
        Controls.Add(layout);
        exclude.Checked = excludeMissingBp; exclude.CheckedChanged += (_, _) => RenderChart();
        formsPlot1.MouseMove += (_, e) => Hover(formsPlot1, e, false);
        bpPlot.MouseMove += (_, e) => Hover(bpPlot, e, true);
        formsPlot1.MouseLeave += (_, _) => ClearTooltip(); bpPlot.MouseLeave += (_, _) => ClearTooltip();
        FormClosed += (_, _) => tooltip.Dispose();
    }
    public void UpdateChart(ChartView value) { chart = value; RenderChart(); }
    private void GraphForm_Load(object? sender, EventArgs e) => RenderChart();
    private void RenderChart()
    {
        Text = chart.Label + " — 履歴・グラフ";
        visible = DisplayValues.Visible(chart.History, exclude.Checked); ClearTooltip();
        Draw(formsPlot1, visible, false); Draw(bpPlot, visible.Where(p => p.MissCount.HasValue).ToArray(), true);
        summary.Text = $"全{chart.History.Count}プレイ / 表示{visible.Length}件　" +
            (chart.History.Count == 0 ? "未プレイ" : visible.Length == 0 ? "除外オプションにより表示対象なし" : "") +
            $"　現行Notes：{chart.Notes?.ToString() ?? "—"}　日時：{TimeZoneInfo.Local.DisplayName}";
        historyGrid.Columns.Clear();
        foreach (var name in new[] { "回", "プレイ日時", "ランプ", "Options", "EX SCORE", "BP", "DJ LEVEL", "Score Rate" }) historyGrid.Columns.Add(name, name);
        foreach (var p in visible)
        {
            var score = DisplayValues.Score(p.Score, chart.Notes);
            historyGrid.Rows.Add(p.Number, p.LocalDate, DisplayValues.Lamp(p.Lamp), p.Options, p.Score, p.MissCount, score.DjLevel, score.Rate);
        }
        historyGrid.AccessibleDescription = "同時刻は保存ID順。元の実プレイの先後を保証しません。";
    }
    private void Draw(FormsPlot control, HistoryPoint[] points, bool bp)
    {
        control.Plot.Clear();
        if (points.Length > 0)
        {
            var line = control.Plot.Add.Scatter(points.Select(p => (double)p.Number).ToArray(), points.Select(p => bp ? (double)p.MissCount!.Value : p.Score).ToArray());
            line.MarkerSize = 7; line.MarkerShape = MarkerShape.FilledCircle; line.Color = bp ? Colors.SeaGreen : Colors.RoyalBlue;
        }
        control.Plot.XLabel("Play number"); control.Plot.YLabel(bp ? "BP" : "EX SCORE");
        control.Plot.Axes.AutoScale();
        control.Plot.Axes.SetLimitsX(0.5, Math.Max(1, chart.History.Count) + 0.5);
        control.Refresh();
    }
    private void Hover(FormsPlot control, MouseEventArgs e, bool bp)
    {
        // 線上の補間点ではなく、描画した実サンプルの丸印だけをヒット判定する。
        var hit = visible.Where(p => !bp || p.MissCount.HasValue).Select(p =>
        {
            var pixel = control.Plot.GetPixel(new Coordinates(p.Number, bp ? p.MissCount!.Value : p.Score));
            var dx = pixel.X - e.X * control.DisplayScale; var dy = pixel.Y - e.Y * control.DisplayScale;
            return (Point: p, Distance: dx * dx + dy * dy);
        }).Where(p => p.Distance <= 100 * control.DisplayScale * control.DisplayScale).OrderBy(p => p.Distance).FirstOrDefault();
        if (hit.Point is null) { ClearTooltip(); return; }
        var point = hit.Point; var score = DisplayValues.Score(point.Score, chart.Notes);
        var text = $"#{point.Number}  {point.LocalDate}\n" + (bp ? $"BP: {point.MissCount}" : $"EX SCORE: {point.Score}\nDJ LEVEL: {score.DjLevel}\nScore Rate: {score.Rate}");
        if (tooltipText == text) return;
        ClearTooltip(); tooltipText = text; tooltip.Show(text, control, e.X + 14, e.Y + 14, 10000);
    }
    private void ClearTooltip()
    {
        tooltip.Hide(formsPlot1); tooltip.Hide(bpPlot); tooltipText = "";
    }
}
