namespace KeiBuild.Ingestion;

public sealed class IngestionException(string message, Exception? innerException = null) : Exception(message, innerException);
