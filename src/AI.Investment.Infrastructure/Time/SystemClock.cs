using AI.Investment.Application.Abstractions;

namespace AI.Investment.Infrastructure.Time;

/// <summary>The real clock. The only place in the system that reads the machine's time.</summary>
public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
