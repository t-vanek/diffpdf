using DiffPdf.Core.Models;
using DiffPdf.Core.Storage;
using DiffPdf.Persistence.SqlServer.Entities;
using DiffPdf.Persistence.SqlServer.Mapping;
using Microsoft.EntityFrameworkCore;

namespace DiffPdf.Persistence.SqlServer;

/// <summary>EF Core (SQL Server) single-row e-mail (SMTP) settings store — the runtime-managed mail transport.</summary>
public sealed class SqlServerEmailSettingsStore(DiffPdfDbContext db, EntityMapper mapper) : IEmailSettingsStore
{
    public async Task<EmailSettings?> GetAsync(CancellationToken ct = default)
    {
        var e = await db.EmailSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        return e is null ? null : mapper.ToDomain(e);
    }

    public async Task<EmailSettings> SaveAsync(EmailSettings settings, long expectedVersion, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var existing = await db.EmailSettings.FirstOrDefaultAsync(ct);

        if (existing is null)
        {
            // First save — insert the single row. expectedVersion is ignored here (there is nothing to conflict with).
            var entity = new EmailSettingsEntity
            {
                Id = Guid.NewGuid(),
                Host = settings.Host,
                Port = settings.Port,
                UseSsl = settings.UseSsl,
                Username = settings.Username,
                Password = settings.Password,
                FromAddress = settings.FromAddress,
                FromName = settings.FromName,
                UpdatedAt = now,
                Version = 1,
            };
            db.EmailSettings.Add(entity);
            await db.SaveChangesAsync(ct);
            return mapper.ToDomain(entity);
        }

        if (existing.Version != expectedVersion)
            throw new ConcurrencyConflictException(
                $"E-mail settings update conflict (expected version {expectedVersion}, found {existing.Version}).");

        existing.Host = settings.Host;
        existing.Port = settings.Port;
        existing.UseSsl = settings.UseSsl;
        existing.Username = settings.Username;
        existing.Password = settings.Password;
        existing.FromAddress = settings.FromAddress;
        existing.FromName = settings.FromName;
        existing.UpdatedAt = now;
        existing.Version += 1;
        await db.SaveChangesAsync(ct);
        return mapper.ToDomain(existing);
    }
}
