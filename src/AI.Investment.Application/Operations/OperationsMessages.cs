using System.Globalization;
using System.Text.Json;

namespace AI.Investment.Application.Operations;

/// <summary>
/// The message types the operating loop queues, and how their payloads are built.
/// </summary>
/// <remarks>
/// <para>
/// Versioned names, so a handler that meets a shape from a newer build refuses it rather than
/// guessing. A queue whose messages are untyped becomes a queue nobody can change.
/// </para>
/// <para>
/// Payloads are flat string maps rather than serialised objects. Two reasons: the rows are permanent
/// and a flat map is readable by a person six months later without the type that wrote it, and a
/// deterministic serialisation makes the deduplication keys below stable across processes and builds.
/// </para>
/// </remarks>
public static class OperationsMessages
{
    /// <summary>Something was put to a human.</summary>
    public const string EscalationRaised = "operations.escalation-raised@1";

    /// <summary>A cycle finished, one way or another.</summary>
    public const string CycleFinished = "operations.cycle-finished@1";

    /// <summary>A shadow measurement was recorded. Nothing was executed.</summary>
    public const string ShadowDecisionRecorded = "operations.shadow-decision-recorded@1";

    /// <summary>A queued message ran out of attempts. Never quiet.</summary>
    public const string OutboxAbandoned = "operations.outbox-abandoned@1";

    /// <summary>Serialises a payload deterministically: keys ordered, invariant culture.</summary>
    public static string Payload(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var ordered = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in values)
        {
            ordered[entry.Key] = entry.Value ?? string.Empty;
        }

        return JsonSerializer.Serialize(ordered);
    }

    /// <summary>Reads a payload back. Returns an empty map for anything unreadable.</summary>
    /// <remarks>
    /// Fail-soft rather than fail-closed, and deliberately: a handler that threw on a payload it
    /// could not parse would retry it until it was abandoned, and the thing that most often produces
    /// an unparseable payload is a message written by a build that has since been replaced.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Read(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(payload)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>A stable number for a payload value.</summary>
    public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A stable timestamp for a payload value.</summary>
    public static string Instant(DateTime value) =>
        value.ToString("O", CultureInfo.InvariantCulture);
}
