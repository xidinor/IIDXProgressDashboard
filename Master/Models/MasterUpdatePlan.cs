namespace IIDXProgressDashboard.Master;

/// <summary>取得・全件検証済みの変更案。生成後の入力変更や別DBへの流用を許さない。</summary>
public sealed class MasterUpdatePlan
{
    public IReadOnlyList<MasterSong> Songs { get; }
    public IReadOnlyList<MasterChart> Charts { get; }
    public IReadOnlyList<string> MissingSongs { get; }
    public IReadOnlyList<MasterChartKey> MissingCharts { get; }
    internal string DatabasePath { get; }
    internal string Revision { get; }
    internal string[] NormalizedTitles { get; }
    internal MasterUpdateState State { get; }

    internal MasterUpdatePlan(string databasePath, string revision, MasterSong[] songs, MasterChart[] charts,
        string[] normalizedTitles, string[] missingSongs, MasterChartKey[] missingCharts, MasterUpdateState state)
    {
        DatabasePath = databasePath;
        Revision = revision;
        Songs = Array.AsReadOnly(songs);
        Charts = Array.AsReadOnly(charts);
        NormalizedTitles = normalizedTitles;
        MissingSongs = Array.AsReadOnly(missingSongs);
        MissingCharts = Array.AsReadOnly(missingCharts);
        State = state;
    }
}
