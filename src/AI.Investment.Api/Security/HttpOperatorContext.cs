using System.Security.Claims;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Operators;

namespace AI.Investment.Api.Security;

/// <summary>
/// The authenticated principal, as the application layer's operator identity.
/// </summary>
/// <remarks>
/// <para>
/// The same shape of adapter as <c>HttpCorrelationContext</c>: the transport owns the mechanism, the
/// application owns the concept, and this is the one line between them. Scoped, because an identity
/// belongs to one request and must not leak into work started beside it.
/// </para>
/// <para>
/// <strong>Anonymous is null, and null is refused everywhere downstream.</strong> A claim set that
/// cannot be read as an identity - no identifier, an unrecognised privilege - also produces null
/// rather than a partial identity. Half an operator is not an operator.
/// </para>
/// </remarks>
public sealed class HttpOperatorContext : IOperatorContext
{
    private readonly IHttpContextAccessor _accessor;

    public HttpOperatorContext(IHttpContextAccessor accessor) =>
        _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));

    public OperatorIdentity? Current
    {
        get
        {
            var user = _accessor.HttpContext?.User;

            if (user?.Identity is not { IsAuthenticated: true })
            {
                return null;
            }

            var id = user.FindFirstValue(ClaimTypes.NameIdentifier);

            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            var privileges = new List<OperatorPrivilege>();

            foreach (var claim in user.FindAll(OperatorAuthentication.PrivilegeClaimType))
            {
                if (!Enum.TryParse<OperatorPrivilege>(claim.Value, out var privilege) ||
                    privilege == OperatorPrivilege.None)
                {
                    return null;
                }

                privileges.Add(privilege);
            }

            try
            {
                return OperatorIdentity.Create(
                    id,
                    user.FindFirstValue(ClaimTypes.Name) ?? id,
                    privileges);
            }
            catch (Domain.Exceptions.DomainException)
            {
                return null;
            }
        }
    }
}
