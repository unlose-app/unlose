namespace Unlose.Core.Interfaces;

public interface IAuditService
{
    Task LogAsync(Models.AuditEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<Models.AuditEntry>> QueryAsync(DateTime? from = null, DateTime? to = null, CancellationToken ct = default);
}
