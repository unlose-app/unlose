namespace Unlose.Core.Models;

public class AuditEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Action { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string? Details { get; set; }
    public bool Success { get; set; }
}
