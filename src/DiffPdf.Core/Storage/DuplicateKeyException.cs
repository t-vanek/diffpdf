namespace DiffPdf.Core.Storage;

/// <summary>Thrown when creating an entity whose key already exists in its scope.</summary>
public sealed class DuplicateKeyException(string message) : Exception(message);
