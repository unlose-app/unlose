namespace Unlose.Core.Ipc;

/// <summary>IPC message envelope (unified format, Service ↔ Client)</summary>
public class PipeMessage
{
    public string Type { get; set; } = string.Empty;
    public string? RequestId { get; set; }
    public object? Payload { get; set; }
    // Backward compatibility: legacy code uses Command
    public string Command
    {
        get => Type;
        set => Type = value;
    }
    public Dictionary<string, string> Parameters { get; set; } = new();
}

/// <summary>IPC response envelope</summary>
public class PipeEnvelope
{
    public string Type { get; set; } = string.Empty;
    public string? RequestId { get; set; }
    public bool Success { get; set; }
    public object? Payload { get; set; }
    public string? ErrorMessage { get; set; }
}

