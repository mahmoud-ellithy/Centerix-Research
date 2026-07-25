namespace Centerix.Application.Common.Interfaces;

using System.Text.Json;

/// <summary>
/// Writes tenant-scoped audit entries capturing who did what to which entity.
/// Implementations must be fire-and-forget safe: a failed audit write must not
/// fail the business operation that triggered it.
/// </summary>
public interface IAuditWriter
{
    Task WriteAsync(
        string action,
        string? entityType = null,
        string? entityId = null,
        string? oldValue = null,
        string? newValue = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Helper for handlers to serialize audit payloads without taking a dependency on
/// a specific JSON library configuration. Callers can also pass raw strings.
/// </summary>
public static class AuditPayload
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string? Serialize<T>(T value) =>
        value is null ? null : JsonSerializer.Serialize(value, Options);
}

