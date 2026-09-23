using IIDXProgressDashboard.Database;
using IIDXProgressDashboard.Dashboard;
using IIDXProgressDashboard.Difficulty;

namespace IIDXProgressDashboard;

public partial class Form1 : Form
{
    private readonly DataGridView chartsGrid = NewGrid();
    private readonly TextBox search = new() { Width = 230, PlaceholderText = "曲名（表外も検索）" };
    private readonly ComboBox style = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 };
    private readonly ComboBox difficulty = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 };
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly Dictionary<long, GraphForm> graphs = [];
    private DashboardSnapshot? snapshot;
    private BetaSettings? settings;
    private bool loading;
    private readonly Button importReflux = new() { Text = "Reflux取込", AutoSize = true };
    private bool importing;
    private bool searching;
    private bool renderingRanks;
    private string? selectedRank;

    public Form1()
    {
        InitializeComponent();
        Text = "IIDX Progress Dashboard — Beta2";
        MinimumSize = new Size(1050, 640); Size = new Size(1350, 850);
        Controls.Clear();
        cmbLevel.Items.Clear(); cmbLevel.DropDownStyle = ComboBoxStyle.DropDownList; cmbLevel.Width = 285;
        foreach (var kind in Enum.GetValues<DifficultyTableKind>()) cmbLevel.Items.Add(DifficultyTableDefinition.Get(kind));
        cmbLevel.DisplayMember = "DisplayName"; cmbLevel.SelectedIndex = 0;
        style.Items.AddRange(new object[] { "全SP/DP", "SP", "DP" }); style.SelectedIndex = 0;
        difficulty.Items.AddRange(new object[] { "全譜面", "B", "N", "H", "A", "L" }); difficulty.SelectedIndex = 0;
        var find = new Button { Text = "検索", AutoSize = true };
        var ranks = new Button { Text = "ランクへ戻る", AutoSize = true };
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        toolbar.Controls.AddRange([cmbLevel, btnLoad, importReflux, search, style, difficulty, find, ranks]);
        importReflux.Click += async (_, _) => await ImportRefluxAsync();
        FormClosing += (_, e) => { if (importing) e.Cancel = true; };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(10) };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize));
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(1200, 700), SplitterDistance = 280 };
        ConfigureGrid(dgvStats); dgvStats.Dock = DockStyle.Fill;
        split.Panel1.Controls.Add(dgvStats); split.Panel2.Controls.Add(chartsGrid);
        layout.Controls.Add(toolbar, 0, 0); layout.Controls.Add(split, 0, 1); layout.Controls.Add(status, 0, 2); Controls.Add(layout);
        cmbLevel.SelectedIndexChanged += (_, _) => { searching = false; selectedRank = null; RenderRanks(); };
        dgvStats.SelectionChanged += (_, _) =>
        {
            if (renderingRanks) return;
            if (dgvStats.CurrentRow?.Tag is string code) { selectedRank = code; searching = false; RenderCharts(); }
        };
        find.Click += (_, _) => { searching = true; RenderCharts(); };
        search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { searching = true; RenderCharts(); e.SuppressKeyPress = true; } };
        ranks.Click += (_, _) => { searching = false; RenderCharts(); };
        chartsGrid.CellClick += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == 0) OpenGraph(); };
        chartsGrid.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { OpenGraph(); e.SuppressKeyPress = true; } };
    }

    internal static DataGridView NewGrid()
    {
        var grid = new DataGridView { Dock = DockStyle.Fill }; ConfigureGrid(grid); return grid;
    }
    private static void ConfigureGrid(DataGridView grid)
    {
        grid.ReadOnly = true; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
        grid.RowHeadersVisible = false; grid.MultiSelect = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells;
        grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        grid.BackgroundColor = SystemColors.Window;
        grid.DefaultCellStyle.NullValue = "—";
    }
    private async void Form1_Load(object? sender, EventArgs e) => await LoadDataAsync();
    private async void btnLoad_Click(object? sender, EventArgs e) => await LoadDataAsync();

    private async Task LoadDataAsync()
    {
        if (loading) return;
        loading = true; btnLoad.Enabled = false; importReflux.Enabled = false; status.Text = "読み込み中…";
        try
        {
            var result = await Task.Run(() =>
            {
                var config = BetaSettings.Read(AppContext.BaseDirectory);
                if (!File.Exists(config.DatabasePath)) throw new FileNotFoundException("表示用DBが未準備です。ベータ版準備手順を参照してください。");
                // 空DBや旧DBを起動時に初期化しない。既知DBだけ既存Runnerで検査する。
                var database = new DatabaseInitializer(config.DatabasePath);
                using var connection = database.OpenConnection();
                using var check = connection.CreateCommand(); check.CommandText = "SELECT count(*) FROM sqlite_schema WHERE name='schema_migrations';";
                if (Convert.ToInt64(check.ExecuteScalar()) != 1) throw new InvalidDataException("旧形式または未準備のDBです。原本は変更していません。");
                new MigrationRunner().Run(connection);
                return (Config: config, Data: new DashboardRepository(config.DatabasePath).Read());
            });
            if (IsDisposed) return;
            settings = result.Config; snapshot = result.Data; RenderRanks();
            foreach (var pair in graphs.ToArray())
                if (snapshot.Charts.FirstOrDefault(c => c.ChartId == pair.Key) is { } chart) pair.Value.UpdateChart(chart);
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            snapshot = null; dgvStats.Rows.Clear(); chartsGrid.Rows.Clear(); status.Text = "読み込み失敗：" + ex.Message;
            MessageBox.Show(this, ex.Message + "\n本体横のINIと表示用DBを確認してください。", "ベータ版の読み込み", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { loading = false; if (!IsDisposed) { btnLoad.Enabled = !importing; importReflux.Enabled = !importing && snapshot != null; } }
    }

    private async Task ImportRefluxAsync()
    {
        if (loading || importing || settings is null) return;
        try
        {
            var importer = new BetaRefluxImport(settings);
            using var dialog = new RefluxImportDialog(importer.ReadSessions());
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            importing = true; importReflux.Enabled = false; btnLoad.Enabled = false;
            status.Text = "Reflux取込中… 完了までお待ちください。";
            var path = dialog.SourcePath; var id = dialog.SessionId; var options = dialog.Options;
            var result = await Task.Run(() => importer.ImportAsync(path, id, options));
            // 一覧と開いているグラフへ、旧履歴を含む同一DBのsnapshotを再配信する。
            await LoadDataAsync();
            MessageBox.Show(this, $"{result.Status}{(result.SessionHeld ? "（Session全体を保留）" : "")}\n読込 {result.Read} / 登録 {result.Imported} / 重複 {result.Duplicates}\n未解決 {result.Unresolved} / 不正 {result.Invalid} / 競合 {result.Conflicts}",
                "Reflux取込結果", MessageBoxButtons.OK, result.Status == "SUCCESS" ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Reflux取込失敗", MessageBoxButtons.OK, MessageBoxIcon.Error); status.Text = "Reflux取込失敗：" + ex.Message; }
        finally { importing = false; importReflux.Enabled = snapshot != null; btnLoad.Enabled = true; }
    }

    private TableView? CurrentTable => snapshot?.Tables.FirstOrDefault(t => t.Code == ((DifficultyTableDefinition)cmbLevel.SelectedItem!).Code);
    private void RenderRanks()
    {
        if (snapshot is null) return;
        renderingRanks = true;
        var previous = selectedRank;
        dgvStats.Columns.Clear();
        foreach (var name in new[] { "ランク", "総譜面", "FC", "EXH", "HARD", "CLEAR", "EASY", "ASSIST", "FAILED", "NO PLAY（履歴あり）", "未プレイ" })
            dgvStats.Columns.Add(name, name);
        var table = CurrentTable;
        if (table != null)
            foreach (var rank in table.Ranks)
            {
                var charts = snapshot.Charts.Where(c => table.Entries.GetValueOrDefault(c.ChartId) == rank.Code).ToArray();
                var values = new List<object> { rank.Name, charts.Length };
                for (int lamp = 7; lamp >= 0; lamp--) values.Add(charts.Count(c => c.BestLamp == lamp));
                values.Add(charts.Count(c => c.History.Count == 0));
                var row = dgvStats.Rows[dgvStats.Rows.Add(values.ToArray())]; row.Tag = rank.Code;
            }
        var selected = dgvStats.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => Equals(r.Tag, previous)) ?? dgvStats.Rows.Cast<DataGridViewRow>().FirstOrDefault();
        if (selected != null) { dgvStats.CurrentCell = selected.Cells[0]; selectedRank = (string)selected.Tag!; }
        else selectedRank = null;
        renderingRanks = false;
        RenderCharts();
    }

    private void RenderCharts()
    {
        if (snapshot is null) return;
        if (searching && string.IsNullOrWhiteSpace(search.Text))
        {
            status.Text = "曲名の一部を入力して検索してください（難易度表外も対象）。";
            return;
        }
        long? selectedId = (chartsGrid.CurrentRow?.Tag as ChartView)?.ChartId;
        chartsGrid.Columns.Clear();
        foreach (var name in new[] { "曲名（クリックで履歴）", "譜面", "☆", "直近ランプ", "直近スコア", "直近BP", "回数", "最高スコア", "最小BP", "最終プレイ", "状態", "評価" }) chartsGrid.Columns.Add(name, name);
        var table = CurrentTable;
        var candidates = snapshot.Charts.Where(c => searching
            ? c.Title.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase) && (style.SelectedIndex == 0 || c.Style == style.Text) && (difficulty.SelectedIndex == 0 || c.Difficulty == difficulty.Text)
            : table != null && selectedRank != null && table.Entries.GetValueOrDefault(c.ChartId) == selectedRank);
        foreach (var chart in candidates)
        {
            var latest = chart.Latest;
            var row = chartsGrid.Rows[chartsGrid.Rows.Add(chart.Title, chart.Style + chart.Difficulty, chart.Level,
                DisplayValues.Lamp(latest?.Lamp), latest?.Score, latest?.MissCount, chart.History.Count, chart.BestScore, chart.MinimumBp,
                latest?.LocalDate, (chart.Active ? "" : "非アクティブ / ") + (chart.Notes is null or <= 0 ? "マスターデータ不完全" : "通常"),
                table?.Entries.ContainsKey(chart.ChartId) == true ? (table.Retained.Contains(chart.ChartId) ? "保持した旧評価" : "今回確認された評価") : "表外")];
            row.Tag = chart;
            row.Cells[0].Style.ForeColor = Color.RoyalBlue;
            if (chart.ChartId == selectedId) chartsGrid.CurrentCell = row.Cells[0];
        }
        status.Text = $"最高ランプ別件数 / {(searching ? "検索結果" : table is null ? "表未取得" : "選択ランク")}：{chartsGrid.Rows.Count}譜面　未解決（通常表示対象外）：{snapshot.Pending}件　日時：{TimeZoneInfo.Local.DisplayName}";
    }

    private void OpenGraph()
    {
        if (chartsGrid.CurrentRow?.Tag is not ChartView chart) return;
        if (graphs.TryGetValue(chart.ChartId, out var existing)) { existing.Activate(); return; }
        var graph = new GraphForm(chart, settings?.ExcludeMissingBp ?? false);
        graphs.Add(chart.ChartId, graph); graph.FormClosed += (_, _) => graphs.Remove(chart.ChartId); graph.Show(this);
    }
}
