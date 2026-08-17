namespace Unlose.Core.Ipc;

/// <summary>Backward-compatible IPC response (legacy format)</summary>
public class PipeResponse
{
    public bool Success { get; set; }
    public string? Data { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime RespondedAt { get; set; } = DateTime.UtcNow;
}

