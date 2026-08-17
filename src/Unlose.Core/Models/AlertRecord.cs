namespace Unlose.Core.Models;

public class AlertRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    public Enums.AlertSeverity Severity { get; set; }
    public Enums.ThreatType ThreatType { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool Acknowledged { get; set; }
}
