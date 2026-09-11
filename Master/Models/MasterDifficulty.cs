namespace IIDXProgressDashboard.Master;

public static class MasterDifficulty
{
    public static string FromLegacyName(string name) => name switch
    {
        "BEGINNER" => "B", "NORMAL" => "N", "HYPER" => "H",
        "ANOTHER" => "A", "LEGGENDARIA" => "L",
        _ => throw new InvalidDataException($"不正difficulty: {name}")
    };
}
