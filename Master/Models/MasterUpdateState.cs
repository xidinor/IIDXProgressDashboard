namespace IIDXProgressDashboard.Master;

// v1 DDLのoptions_jsonに版付きで所有範囲を記録する。失敗runは所有範囲の根拠にしない。
internal sealed record MasterUpdateState(int FormatVersion, string Scope, string ParserVersion,
    string NormalizerVersion, string Fingerprint, string[] OwnedSongs, MasterChartKey[] OwnedCharts,
    bool MissingConfirmed = false, int SongsDeactivated = 0, int ChartsDeactivated = 0);
