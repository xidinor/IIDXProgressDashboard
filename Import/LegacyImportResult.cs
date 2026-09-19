namespace IIDXProgressDashboard.Import;

/// <summary>件数は元行単位の排他的な分類。UnresolvedにはInvalid/Conflictsを含めない。</summary>
public sealed record LegacyImportResult(long ImportRunId, string BackupPath, string Status,
    int Read, int Imported, int Duplicates, int Unresolved, int Invalid, int Conflicts);
