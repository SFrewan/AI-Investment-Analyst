using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using AI.Investment.Application.Operators;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AI.Investment.Api.Security;

/// <summary>
/// Authenticates an operator from a keyed header, against hashes held in configuration.
/// </summary>
/// <remarks>
/// <para>
/// The audit that started this project recorded finding F-03: the original solution called
/// <c>UseAuthorization()</c> with no authentication scheme registered, which is a no-op that reads
/// as security in review. Phase 0 removed it and left an explicit, documented absence rather than
/// replacing it with something decorative. This is the replacement, and it is deliberately small
/// enough to be read in full.
/// </para>
/// <para>
/// <strong>Fail closed, in four ways.</strong> A request with no header is not authenticated. A
/// configured account whose hash is not sixty-four hexadecimal characters authenticates nobody
/// rather than everybody. An unrecognised privilege name refuses the whole account rather than
/// granting the rest of it. And an installation with no accounts configured authenticates no one at
/// all, which is the shipped default.
/// </para>
/// <para>
/// <strong>The key is never stored, logged or compared with <c>==</c>.</strong> Configuration holds
/// SHA-256 digests; the handler hashes what arrived and compares digests with
/// <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>, so
/// the comparison takes the same time whether the first byte differs or the last. The failure
/// message says only that the key was not recognised - naming which account nearly matched would
/// turn a wrong key into an account enumeration.
/// </para>
/// <para>
/// <strong>What this is not.</strong> It is a bearer credential, not an identity provider: no
/// rotation, no expiry, no revocation list, no second factor. It is the smallest mechanism that puts
/// a name on every sensitive action, and it is shaped so that replacing it with OIDC touches this
/// file and the options beside it and nothing below the controller.
/// </para>
/// </remarks>
public sealed class OperatorKeyAuthenticationHandler
    : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IOptionsMonitor<OperatorOptions> _operators;

    public OperatorKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptionsMonitor<OperatorOptions> operators)
        : base(options, logger, encoder)
    {
        _operators = operators ?? throw new ArgumentNullException(nameof(operators));
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(OperatorAuthentication.HeaderName, out var values))
        {
            // No credential offered. NoResult rather than Fail: the caller did not try, and an
            // anonymous request to a read endpoint is still a perfectly ordinary thing.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var presented = values.ToString();

        if (string.IsNullOrWhiteSpace(presented))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(presented));

        foreach (var account in _operators.CurrentValue.Accounts)
        {
            if (!TryReadDigest(account.KeySha256, out var configured))
            {
                // A malformed hash matches nothing. Skipping the account is the fail-closed
                // reading; treating an unparsable value as a wildcard is the other one.
                continue;
            }

            if (!CryptographicOperations.FixedTimeEquals(digest, configured))
            {
                continue;
            }

            if (!TryBuildIdentity(account, out var identity, out var problem))
            {
                return Task.FromResult(AuthenticateResult.Fail(problem));
            }

            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(Principal(identity!), OperatorAuthentication.Scheme)));
        }

        return Task.FromResult(AuthenticateResult.Fail("The operator key was not recognised."));
    }

    /// <summary>Turns a matched account into the identity the application layer reads.</summary>
    private static bool TryBuildIdentity(
        OperatorAccountOptions account,
        out OperatorIdentity? identity,
        out string problem)
    {
        identity = null;
        problem = string.Empty;

        var privileges = new List<OperatorPrivilege>(account.Privileges.Count);

        foreach (var name in account.Privileges)
        {
            if (!Enum.TryParse<OperatorPrivilege>(name, ignoreCase: true, out var privilege) ||
                privilege == OperatorPrivilege.None)
            {
                problem = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Operator '{account.Id}' is configured with an unrecognised privilege. The " +
                    $"account is refused rather than granted the rest of its list.");

                return false;
            }

            privileges.Add(privilege);
        }

        try
        {
            identity = OperatorIdentity.Create(account.Id, account.DisplayName, privileges);
        }
        catch (Domain.Exceptions.DomainException)
        {
            problem = "The operator account is not configured with a usable identity.";

            return false;
        }

        return true;
    }

    private static ClaimsPrincipal Principal(OperatorIdentity identity)
    {
        var claims = new List<Claim>(identity.Privileges.Count + 2)
        {
            new(ClaimTypes.NameIdentifier, identity.Id),
            new(ClaimTypes.Name, identity.DisplayName),
        };

        foreach (var privilege in identity.Privileges)
        {
            claims.Add(new Claim(OperatorAuthentication.PrivilegeClaimType, privilege.ToString()));
        }

        return new ClaimsPrincipal(
            new ClaimsIdentity(claims, OperatorAuthentication.Scheme, ClaimTypes.Name, string.Empty));
    }

    private static bool TryReadDigest(string configured, out byte[] digest)
    {
        digest = [];

        if (string.IsNullOrWhiteSpace(configured) || configured.Length != 64)
        {
            return false;
        }

        try
        {
            digest = Convert.FromHexString(configured);
        }
        catch (FormatException)
        {
            return false;
        }

        return digest.Length == 32;
    }
}
