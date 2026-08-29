using System.Text.Json.Serialization;

namespace AI.Investment.Dashboard.Services;

/// <summary>Who the platform says is calling, as <c>whoami</c> answers it.</summary>
public sealed record OperatorIdentityDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("privileges")] IReadOnlyList<string> Privileges);

/// <summary>
/// The signed-in operator, and the key that proves it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The key lives here and nowhere else.</strong> It is never put in a URL, never written to
/// the console, never rendered, and never returned by any property this class exposes - only
/// <see cref="ApplyTo"/> can read it, and it only ever writes it into a request header. Signing out
/// clears it along with everything derived from it.
/// </para>
/// <para>
/// Authentication is not authorization. <see cref="Identity"/> says who is calling;
/// <see cref="Has"/> says what they may do, and the answer comes from the platform's own view of
/// the account rather than from anything this client decided.
/// </para>
/// </remarks>
public sealed class OperatorSession
{
    /// <summary>The header the platform authenticates with.</summary>
    public const string HeaderName = "X-Operator-Key";

    private string? _key;

    /// <summary>Raised when the session is established or cleared.</summary>
    public event Action? Changed;

    public OperatorIdentityDto? Identity { get; private set; }

    public bool IsSignedIn => _key is not null && Identity is not null;

    /// <summary>Whether the platform granted this operator a named privilege.</summary>
    /// <remarks>
    /// Advisory only: it decides what to render, never what is permitted. The backend refuses an
    /// unprivileged call whatever this returns, and a page that hid a control would still have to
    /// handle a 403.
    /// </remarks>
    public bool Has(string privilege) =>
        Identity is not null &&
        Identity.Privileges.Contains(privilege, StringComparer.Ordinal);

    public void Establish(string key, OperatorIdentityDto identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(identity);

        _key = key;
        Identity = identity;

        Changed?.Invoke();
    }

    public void Clear()
    {
        _key = null;
        Identity = null;

        Changed?.Invoke();
    }

    /// <summary>Puts the credential on one outgoing request. The only reader of the key.</summary>
    internal void ApplyTo(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_key is not null)
        {
            request.Headers.TryAddWithoutValidation(HeaderName, _key);
        }
    }
}
