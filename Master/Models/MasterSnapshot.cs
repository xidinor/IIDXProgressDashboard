namespace IIDXProgressDashboard.Master;

/// <summary>検証済みの入力候補。取得成功は欠落項目の非アクティブ化を許可しない。</summary>
public sealed record MasterSnapshot(
    IReadOnlyList<MasterSong> Songs,
    IReadOnlyList<MasterChart> Charts,
    IReadOnlyList<MasterDiagnostic> Diagnostics,
    TextageSourceSnapshot Sources)
{
    public string Scope => "TEXTAGE_ACTBL_CATALOG_V1";
    public string ParserVersion => "1";
    public bool CanDeactivateMissing => false;
}
