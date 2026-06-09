using DiffPdf.Core.Models;

namespace DiffPdf.Persistence;

/// <summary>
/// Persistence for the single-row e-mail (SMTP) settings — the outbound notification transport, managed at
/// runtime. <see cref="GetAsync"/> returns null until settings are first saved. Updates use optimistic
/// concurrency (<c>expectedVersion</c>; pass 0 for the initial save).
/// </summary>
public interface IEmailSettingsStore
{
    Task<EmailSettings?> GetAsync(CancellationToken ct = default);

    /// <summary>Inserts (first save) or updates the settings row; throws on a version mismatch.</summary>
    Task<EmailSettings> SaveAsync(EmailSettings settings, long expectedVersion, CancellationToken ct = default);
}
