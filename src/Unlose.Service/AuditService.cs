using Unlose.Core.Interfaces;
using Unlose.Core.Models;
using Unlose.Core.Data;
using Microsoft.Extensions.Logging;

namespace Unlose.Service;

public class AuditService : IAuditService
{
    private readonly ILogger<AuditService> _logger;
    private readonly SqliteRepository? _repo;
    private readonly List<AuditEntry> _entries = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AuditService(ILogger<AuditService> logger)
    {
        _logger = logger;
    }

    public AuditService(ILogger<AuditService> logger, SqliteRepository repo)
    {
        _logger = logger;
        _repo = repo;
    }

    public async Task LogAsync(AuditEntry entry, CancellationToken ct = default)
    {
        if (_repo is not null)
        {
            await _repo.InsertAuditEntryAsync(entry, ct);
            _logger.LogInformation("[AUDIT] {Action} by {Actor}: {Details}", entry.Action, entry.Actor, entry.Details);
            return;
        }

        await _lock.WaitAsync(ct);
        try
        {
            _entries.Add(entry);
            _logger.LogInformation("[AUDIT] {Action} by {Actor}: {Details}", entry.Action, entry.Actor, entry.Details);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<AuditEntry>> QueryAsync(DateTime? from = null, DateTime? to = null, CancellationToken ct = default)
    {
        if (_repo is not null)
        {
            return (await _repo.QueryAuditEntriesAsync(from, to, ct)).AsReadOnly();
        }

        await _lock.WaitAsync(ct);
        try
        {
            var query = _entries.AsEnumerable();
            if (from.HasValue) query = query.Where(e => e.Timestamp >= from.Value);
            if (to.HasValue) query = query.Where(e => e.Timestamp <= to.Value);
            return query.OrderByDescending(e => e.Timestamp).ToList().AsReadOnly();
        }
        finally
        {
            _lock.Release();
        }
    }
}
