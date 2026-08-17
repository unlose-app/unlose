using Unlose.Core.Models;

namespace Unlose.Core.Interfaces;

/// <summary>Module interface for components that can be suspended/resumed (uniform response while protection is paused)</summary>
public interface ISuspendable
{
    void Suspend();
    void Resume();
    bool IsSuspended { get; }
}

/// <summary>Protection state management interface</summary>
public interface IProtectionStateManager
{
    Task PauseAsync(Enums.PauseDuration? duration, string? pausedBy = null, CancellationToken ct = default);
    Task ResumeAsync(CancellationToken ct = default);
    ProtectionState GetState();
    event EventHandler<ProtectionState>? StateChanged;
}
