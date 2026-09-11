namespace IIDXProgressDashboard.Master;

public sealed record TextageSourceFile(string Name, string Content, long ByteLength, string Sha256)
{
    public TextageEncoding Encoding { get; init; }
    public string? Location { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? ETag { get; init; }
    public DateTimeOffset? LastModified { get; init; }
}
