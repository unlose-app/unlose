using Unlose.Core.Enums;
using Unlose.Core.Interfaces;
using Unlose.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Unlose.Service;

/// <summary>
/// Protection pause manager — IProtectionStateManager + IHostedService
///
/// PauseAsync(duration):
///   - Sets IsPaused = true
///   - Notifies all ISuspendable modules to suspend
///   - Starts a timer (not for UntilReboot) that auto-calls ResumeAsync on expiry
///   - Publishes ProtectionStateChangedEvent to the event bus
///
/// ResumeAsync(): cancels the pause, resumes all modules, publishes the resume event
///
/// Manual snapshots are unaffected by pause (SnapshotManager.CreateAsync does not check this state)
/// </summary>
public class ProtectionPauseManager : BackgroundService, IProtectionStateManager
{
    private readonly ILogger<ProtectionPauseManager> _logger;
    private readonly EventBus _eventBus;

    // Late registration (avoids circular dependencies); modules register via RegisterSuspendable after the Worker starts
    private readonly List<ISuspendable> _suspendables = new();
    private readonly object _stateLock = new();

    private bool _isPaused;
    private DateTimeOffset? _pausedUntil;
    private string? _pausedBy;
    private CancellationTokenSource? _timerCts;

    public event EventHandler<ProtectionState>? StateChanged;

    public ProtectionPauseManager(
        ILogger<ProtectionPauseManager> logger,
        EventBus eventBus)
    {
        _logger = logger;
        _eventBus = eventBus;
    }

    /// <summary>Register a module that must respond to pause/resume</summary>
    public void RegisterSuspendable(ISuspendable s) { lock (_stateLock) _suspendables.Add(s); }

    // ── IProtectionStateManager ───────────────────────────────────────────────

    public ProtectionState GetState()
    {
        lock (_stateLock)
        {
            // Check whether the timer has expired
            if (_isPaused && _pausedUntil.HasValue && DateTimeOffset.UtcNow >= _pausedUntil.Value)
                ResumeInternalLocked();
            return new ProtectionState
            {
                IsActive = !_isPaused,
                PausedUntil = _pausedUntil?.UtcDateTime,
                PausedBy = _pausedBy
            };
        }
    }

    public async Task PauseAsync(PauseDuration? duration, string? requestedBy = null, CancellationToken ct = default)
    {
        TimeSpan? span = duration switch
        {
            PauseDuration.ThirtyMinutes => TimeSpan.FromMinutes(30),
            PauseDuration.OneHour       => TimeSpan.FromHours(1),
            PauseDuration.UntilReboot   => (TimeSpan?)null,
            null                        => TimeSpan.FromHours(1),
            _                           => TimeSpan.FromHours(1)
        };

        // Cancel the old timer
        CancellationTokenSource? oldCts;
        lock (_stateLock)
        {
            oldCts = _timerCts;
            _isPaused    = true;
            _pausedBy    = requestedBy ?? "user";
            _pausedUntil = span.HasValue ? DateTimeOffset.UtcNow.Add(span.Value) : (DateTimeOffset?)null;
            _timerCts    = span.HasValue ? new CancellationTokenSource() : null;
        }
        oldCts?.Cancel();

        // Suspend all ISuspendable modules
        IReadOnlyList<ISuspendable> snapshot;
        lock (_stateLock) snapshot = _suspendables.ToList();
        foreach (var s in snapshot) s.Suspend();

        _logger.LogWarning("Protection paused: duration={D}, until={U}", duration, _pausedUntil);
        var state = GetState();
        StateChanged?.Invoke(this, state);
        _eventBus.Publish(new ProtectionStateChangedEvent(_isPaused, _pausedUntil?.UtcDateTime));

        // Auto-resume timer
        if (span.HasValue && _timerCts is not null)
        {
            var cts = _timerCts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(span.Value, cts.Token);
                    await ResumeAsync(ct);
                }
                catch (OperationCanceledException) { }
            });
        }

        await Task.CompletedTask;
    }

    public async Task ResumeAsync(CancellationToken ct = default)
    {
        lock (_stateLock)
        {
            if (!_isPaused) return;
            ResumeInternalLocked();
        }

        IReadOnlyList<ISuspendable> snapshot;
        lock (_stateLock) snapshot = _suspendables.ToList();
        foreach (var s in snapshot) s.Resume();

        _logger.LogInformation("Protection resumed.");
        var state = GetState();
        StateChanged?.Invoke(this, state);
        _eventBus.Publish(new ProtectionStateChangedEvent(false, null));
        await Task.CompletedTask;
    }

    // ── BackgroundService ─────────────────────────────────────────────────────
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // No dedicated loop needed; all logic is driven by PauseAsync/ResumeAsync
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _timerCts?.Cancel();
        await base.StopAsync(cancellationToken);
    }

    // ── Internal ─────────────────────────────────────────────────────────────────
    private void ResumeInternalLocked()
    {
        _isPaused    = false;
        _pausedUntil = null;
        _pausedBy    = null;
        _timerCts?.Cancel();
        _timerCts    = null;
    }
}
