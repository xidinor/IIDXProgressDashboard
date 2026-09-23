using IIDXProgressDashboard.Import;

namespace IIDXProgressDashboard.Dashboard;

/// <summary>新規Sessionと既存Sessionの再取込を明示的に選択する最小画面。</summary>
internal sealed class RefluxImportDialog : Form
{
    private readonly ComboBox sessions = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 620 };
    private readonly TextBox path = new() { Width = 520 };
    private readonly CheckBox local = new() { Text = "Local時刻（Refluxのuselocaltime有効時）", AutoSize = true };
    private readonly ComboBox zones = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 620, Enabled = false };
    public string SourcePath => path.Text;
    public Guid? SessionId => (sessions.SelectedItem as BetaRefluxSession)?.Id;
    public RefluxImportOptions Options => sessions.SelectedItem is BetaRefluxSession saved ? saved.Options
        : local.Checked ? new(RefluxTimeMode.Local, (zones.SelectedItem as TimeZoneInfo)?.Id) : new();

    public RefluxImportDialog(IReadOnlyList<BetaRefluxSession> saved)
    {
        Text = "Reflux Session取込"; ClientSize = new(680, 310); StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new(12) };
        layout.Controls.Add(new Label { Text = "初回は新規。再取込・追記・コピー・改名は元のSessionを選んでください。", AutoSize = true });
        sessions.Items.Add("新しいSession（別の実プレイファイル）");
        foreach (var session in saved) sessions.Items.Add(session);
        layout.Controls.Add(sessions);
        var fileRow = new FlowLayoutPanel { Width = 650, Height = 38 };
        var browse = new Button { Text = "TSV選択", AutoSize = true };
        fileRow.Controls.AddRange([path, browse]); layout.Controls.Add(fileRow);
        layout.Controls.Add(new Label { Text = "日時は初期値UTC。出力設定を確認してから取り込んでください。", AutoSize = true });
        layout.Controls.Add(local);
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones()) zones.Items.Add(zone);
        zones.DisplayMember = "DisplayName"; layout.Controls.Add(zones);
        local.CheckedChanged += (_, _) => zones.Enabled = local.Checked && SessionId is null;
        sessions.SelectedIndexChanged += (_, _) =>
        {
            var session = sessions.SelectedItem as BetaRefluxSession;
            if (session != null) path.Text = session.Path;
            local.Checked = session?.Options.TimeMode == RefluxTimeMode.Local;
            local.Enabled = session is null; zones.Enabled = session is null && local.Checked;
            zones.SelectedItem = zones.Items.Cast<TimeZoneInfo>().FirstOrDefault(z => z.Id == session?.Options.TimeZoneId);
        };
        browse.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { Filter = "Reflux Session (*.tsv)|*.tsv", CheckFileExists = true };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            path.Text = picker.FileName;
            var known = saved.FirstOrDefault(s => s.MatchesPath(picker.FileName));
            if (known != null) sessions.SelectedItem = known;
            path.Text = picker.FileName;
        };
        var start = new Button { Text = "取り込む", AutoSize = true };
        start.Click += (_, _) =>
        {
            try
            {
                if (!File.Exists(SourcePath)) throw new InvalidDataException("Session TSVを選択してください。");
                Options.GetTimeZone(); DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        layout.Controls.Add(start); Controls.Add(layout); sessions.SelectedIndex = 0;
    }
}
