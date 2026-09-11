namespace IIDXProgressDashboard.Master;

public sealed record MasterUpdateResult(long ImportRunId, string BackupPath, int SongsUpserted,
    int ChartsUpserted, int SongsDeactivated, int ChartsDeactivated);
