using IIDXProgressDashboard.Import;
using IIDXProgressDashboard.Matching;

namespace IIDXProgressDashboard.Dashboard;

/// <summary>選択は画面内に仮置きし、「保存」までDBへ書き込まない。</summary>
public sealed class UnresolvedImportDialog : Form
{
    private readonly ManualPlayResolution service;
    private readonly DataGridView pending = Form1.NewGrid();
    private readonly DataGridView candidates = Form1.NewGrid();
    private readonly TextBox detail = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill };
    private readonly TextBox search = new() { Width = 260, PlaceholderText = "候補の曲名・tagで検索" };
    private readonly TextBox note = new() { Width = 360, PlaceholderText = "この譜面と判断した理由（必須）" };
    private readonly Button choose = new() { Text = "選択を確定予定に追加", AutoSize = true, Enabled = false };
    private readonly Button chooseBulk = new() { Text = "同じ取込元・曲名・譜面を一括選択", AutoSize = true, Enabled = false };
    private readonly Button save = new() { Text = "確定予定を保存", AutoSize = true, Enabled = false };
    private readonly Button cancel = new() { Text = "キャンセル", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly Label information = new() { AutoSize = true, MaximumSize = new Size(1000, 0) };
    private readonly Dictionary<long, ManualPlayDecision> decisions = [];
    private readonly HashSet<long> bulkIds = [];
    private ManualPlayReview? review;
    private int generation;
    private bool saving;

    public UnresolvedImportDialog(ManualPlayResolution service)
    {
        this.service = service;
        Text = "未解決行の確認・手動確定";
        Size = new Size(1200, 900); MinimumSize = new Size(1000, 700);
        StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 7, ColumnCount = 1, Padding = new Padding(10) };
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 30));
        layout.RowStyles.Add(new(SizeType.Percent, 25));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 45));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(new Label { AutoSize = true, Text = "未解決行 → 譜面候補 → 判断理由の順に選び、最後に保存してください。キャンセルは確定予定をすべて破棄します。" }, 0, 0);
        layout.Controls.Add(pending, 0, 1); layout.Controls.Add(detail, 0, 2);
        var filter = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var showAll = new Button { Text = "同じ譜面種別をすべて表示", AutoSize = true };
        filter.Controls.AddRange([search, showAll]); layout.Controls.Add(filter, 0, 3);
        layout.Controls.Add(candidates, 0, 4);
        var action = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        var remove = new Button { Text = "この行の確定予定を取消", AutoSize = true };
        action.Controls.AddRange([note, choose, chooseBulk, remove]); layout.Controls.Add(action, 0, 5);
        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        footer.Controls.AddRange([save, cancel, information]); layout.Controls.Add(footer, 0, 6); Controls.Add(layout);
        CancelButton = cancel;
        foreach (var name in new[] { "ID", "取込元", "種別", "元曲名", "譜面", "保留理由", "確定予定" }) pending.Columns.Add(name, name);
        foreach (var name in new[] { "曲名", "tag", "譜面", "☆", "Notes", "活動状態", "確認結果" }) candidates.Columns.Add(name, name);
        pending.SelectionChanged += async (_, _) => await ReviewSelectionAsync();
        candidates.SelectionChanged += (_, _) => UpdateSelection();
        note.TextChanged += (_, _) => UpdateSelection();
        search.TextChanged += (_, _) => RenderCandidates();
        showAll.Click += (_, _) => search.Clear();
        choose.Click += (_, _) => StageSelection();
        chooseBulk.Click += async (_, _) => await StageBulkAsync();
        remove.Click += (_, _) =>
        {
            if (review is null) return;
            decisions.Remove(review.Row.Id); bulkIds.Remove(review.Row.Id); UpdateStagedRows();
        };
        save.Click += async (_, _) => await SaveAsync();
        FormClosing += (_, e) => { if (saving) e.Cancel = true; else generation++; };
        Shown += async (_, _) => await LoadPendingAsync();
    }

    private async Task LoadPendingAsync()
    {
        information.Text = "未解決行を読み込み中…";
        try
        {
            var rows = await Task.Run(service.ReadPending);
            if (IsDisposed) return;
            foreach (var row in rows)
                pending.Rows[pending.Rows.Add(row.Id, row.Source, row.Entity, row.Title, row.Difficulty, row.Reason, "")].Tag = row;
            if (pending.Rows.Count > 0) pending.CurrentCell = pending.Rows[0].Cells[0];
            information.Text = $"未解決の元行 {rows.Count}件。同一元行の再試行記録はまとめて表示します。";
            await ReviewSelectionAsync();
        }
        catch (Exception ex) { if (!IsDisposed) information.Text = "読み込み失敗：" + ex.Message; }
    }

    private async Task ReviewSelectionAsync()
    {
        var current = ++generation;
        review = null; choose.Enabled = false; chooseBulk.Enabled = false; candidates.Rows.Clear(); note.Clear();
        if (pending.CurrentRow?.Tag is not PendingImport row) return;
        detail.Text = "元行を確認中…";
        try
        {
            var value = await Task.Run(() => service.Review(row));
            if (IsDisposed || current != generation) return;
            review = value;
            detail.Text = $"元曲名：{row.Title} / 譜面：{row.Difficulty}\r\n取込元：{row.Source} / 元行キー：{row.Key}\r\n"
                + $"保存時の理由：{row.Reason}\r\n{row.Detail}\r\n"
                + (value.BlockedReason is null ? $"入力：☆{value.Request!.Level} / Notes {value.Request.TotalNotes}\r\n" : "確定不可：" + value.BlockedReason + "\r\n")
                + $"\r\n現在の照合根拠：\r\n{value.Explanation}\r\n\r\n元データ：\r\n{row.Raw}";
            search.Text = row.Title ?? "";
            if (decisions.TryGetValue(row.Id, out var decision)) note.Text = decision.Note;
            RenderCandidates();
        }
        catch (Exception ex) { if (!IsDisposed && current == generation) detail.Text = "確認失敗：" + ex.Message; }
    }

    private void RenderCandidates()
    {
        candidates.Rows.Clear();
        if (review?.Request is not { } request) { UpdateSelection(); return; }
        var query = search.Text.Trim();
        foreach (var chart in review.Charts.Where(c => c.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || c.Tag.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            var error = ManualPlayResolution.Incompatibility(request, chart);
            var row = candidates.Rows[candidates.Rows.Add(chart.Title, chart.Tag, chart.PlayStyle + chart.Difficulty,
                chart.Level, chart.TotalNotes, chart.SongIsActive && chart.ChartIsActive ? "通常" : "非アクティブ", error ?? "選択可能")];
            row.Tag = chart;
            if (error is not null) row.DefaultCellStyle.ForeColor = Color.Gray;
        }
        // 最初の候補を自動確定する操作にならないよう、選択は利用者が明示する。
        candidates.ClearSelection(); candidates.CurrentCell = null; UpdateSelection();
    }

    private void UpdateSelection()
    {
        choose.Enabled = !saving && review?.BlockedReason is null && review?.Request is { } request
            && candidates.CurrentRow?.Tag is ChartCandidate chart && !string.IsNullOrWhiteSpace(note.Text)
            && ManualPlayResolution.Incompatibility(request, chart) is null;
        chooseBulk.Enabled = choose.Enabled;
    }

    private void StageSelection()
    {
        if (!choose.Enabled || review is null || candidates.CurrentRow?.Tag is not ChartCandidate chart) return;
        decisions[review.Row.Id] = new(review.Row, chart, note.Text.Trim());
        bulkIds.Remove(review.Row.Id);
        UpdateStagedRows();
    }

    private async Task StageBulkAsync()
    {
        if (!chooseBulk.Enabled || review is null || candidates.CurrentRow?.Tag is not ChartCandidate chart) return;
        var anchor = review.Row; var reason = note.Text.Trim();
        chooseBulk.Enabled = false;
        try
        {
            var group = await Task.Run(() => service.PrepareBulk(anchor, chart, reason));
            if (IsDisposed) return;
            foreach (var decision in group)
            {
                decisions[decision.Row.Id] = decision;
                bulkIds.Add(decision.Row.Id);
            }
            UpdateStagedRows();
            information.Text = $"同じ取込元・種別・曲名・譜面の{group.Count}元行を一括確定予定に追加しました。保存時に対象件数を確認します。";
        }
        catch (Exception ex) { if (!IsDisposed) MessageBox.Show(this, ex.Message, "一括選択できません", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { if (!IsDisposed) UpdateSelection(); }
    }

    private void UpdateStagedRows()
    {
        foreach (DataGridViewRow gridRow in pending.Rows)
            if (gridRow.Tag is PendingImport row)
                gridRow.Cells[6].Value = decisions.TryGetValue(row.Id, out var d) ? $"{d.Chart.Title} [{d.Chart.Tag}] {d.Chart.PlayStyle}{d.Chart.Difficulty}" : "";
        save.Enabled = !saving && decisions.Count > 0;
        information.Text = $"確定予定 {decisions.Count}件。保存するまでDBへ反映されません。";
    }

    private async Task SaveAsync()
    {
        if (saving || decisions.Count == 0) return;
        var batch = decisions.Values.ToArray();
        var bulkSelectionIds = bulkIds.ToArray();
        if (bulkIds.Count > 0 && MessageBox.Show(this,
            $"同じ取込元・種別・曲名・譜面で一括選択した{bulkIds.Count}元行を含む、合計{batch.Length}元行を保存します。\n各元行は別プレイとして登録されます。続行しますか？",
            "一括確定の確認", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        saving = true; Enabled = false;
        try
        {
            await Task.Run(() => service.Save(batch, bulkSelectionIds));
            saving = false; DialogResult = DialogResult.OK; Close();
        }
        catch (Exception ex)
        {
            saving = false; Enabled = true;
            MessageBox.Show(this, ex.Message + "\n履歴への反映は行われていません。", "手動確定の保存失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
