namespace AI.Investment.Dashboard.Services;

/// <summary>
/// One refresh signal for the whole shell, and the record of when it last produced data.
/// </summary>
/// <remarks>
/// <para>
/// The toolbar's refresh button and every page listen to the same signal, so one press reloads what
/// is on screen rather than each page inventing its own button.
/// </para>
/// <para>
/// <strong>A refresh already in flight is not started again.</strong> Pressing the button twice
/// during a slow load would otherwise double the traffic and let two responses race to render, with
/// the older one able to win.
/// </para>
/// <para>
/// There is no automatic polling. The platform's data changes when an operating cycle runs, not
/// continuously, and a dashboard that re-fetched every few seconds would imply a liveness the data
/// does not have while quietly multiplying the load on it.
/// </para>
/// </remarks>
public sealed class RefreshState
{
    /// <summary>Raised when something should reload.</summary>
    public event Func<Task>? Requested;

    /// <summary>Raised when the in-flight or last-refreshed state changed, so the shell re-renders.</summary>
    public event Action? Changed;

    public bool InFlight { get; private set; }

    /// <summary>When the last refresh completed, or null before the first one.</summary>
    public DateTime? LastRefreshedUtc { get; private set; }

    public async Task RequestAsync()
    {
        if (InFlight)
        {
            return;
        }

        InFlight = true;
        Changed?.Invoke();

        try
        {
            if (Requested is not null)
            {
                await Requested.Invoke().ConfigureAwait(false);
            }

            LastRefreshedUtc = DateTime.UtcNow;
        }
        finally
        {
            InFlight = false;
            Changed?.Invoke();
        }
    }
}
