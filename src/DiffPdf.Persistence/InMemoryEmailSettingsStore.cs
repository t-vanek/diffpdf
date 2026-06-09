using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;

namespace DiffPdf.Persistence;

/// <summary>
/// Thread-safe in-memory single-row e-mail settings store for single-instance / dev use. Mirrors the
/// optimistic-concurrency semantics of the relational store. Does not survive restart.
/// </summary>
public sealed class InMemoryEmailSettingsStore : IEmailSettingsStore
{
    private readonly object _gate = new();
    private EmailSettings? _settings;

    public Task<EmailSettings?> GetAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_settings);
    }

    public Task<EmailSettings> SaveAsync(EmailSettings settings, long expectedVersion, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_settings is null)
            {
                _settings = settings with { Version = 1, UpdatedAt = DateTimeOffset.UtcNow };
                return Task.FromResult(_settings);
            }
            if (_settings.Version != expectedVersion)
                throw new ConcurrencyConflictException(
                    $"E-mail settings version {_settings.Version} != expected {expectedVersion}.");

            _settings = settings with { Version = _settings.Version + 1, UpdatedAt = DateTimeOffset.UtcNow };
            return Task.FromResult(_settings);
        }
    }
}
