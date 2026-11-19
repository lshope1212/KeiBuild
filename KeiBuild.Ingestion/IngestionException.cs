namespace KeiBuild.Ingestion;

public sealed class IngestionException(string message, Exception? innerException = null) : Exception(message, innerException)
{
    public int? HttpStatus { get; init; }
    public DateTimeOffset? RetryAt { get; init; }
    public bool IsTransient { get; init; }
}
