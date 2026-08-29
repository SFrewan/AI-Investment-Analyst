using AI.Investment.Application.Operators;
using Microsoft.AspNetCore.Authorization;

namespace AI.Investment.Api.Security;

/// <summary>
/// One authorization policy per operator privilege, named so a controller can state which it needs.
/// </summary>
/// <remarks>
/// <para>
/// Every policy requires the operator scheme and one privilege claim. There is no policy that
/// requires only authentication: an endpoint that any authenticated operator could call would grant
/// the privilege by being added, and privileges are meant to be granted deliberately.
/// </para>
/// <para>
/// The policies are the transport half of a check that is made twice. <c>OperatorConsole</c> makes
/// it again from the identity it is handed, because a policy attribute can be forgotten on a new
/// endpoint and the rule itself must not depend on somebody remembering.
/// </para>
/// </remarks>
public static class OperatorPolicies
{
    public const string DecideOpportunities = "operator.decide-opportunities";
    public const string AnswerEscalations = "operator.answer-escalations";
    public const string AdministerKillSwitch = "operator.administer-kill-switch";
    public const string AdministerWatches = "operator.administer-watches";

    /// <summary>Reading financial state. The only read privilege, and it grants no action.</summary>
    public const string ViewPortfolio = "operator.view-portfolio";

    /// <summary>Registers every policy. Adding a privilege without a policy is a compile-time job.</summary>
    public static void Register(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Add(options, DecideOpportunities, OperatorPrivilege.DecideOpportunities);
        Add(options, AnswerEscalations, OperatorPrivilege.AnswerEscalations);
        Add(options, AdministerKillSwitch, OperatorPrivilege.AdministerKillSwitch);
        Add(options, AdministerWatches, OperatorPrivilege.AdministerWatches);
        Add(options, ViewPortfolio, OperatorPrivilege.ViewPortfolio);
    }

    private static void Add(AuthorizationOptions options, string name, OperatorPrivilege privilege) =>
        options.AddPolicy(name, policy => policy
            .AddAuthenticationSchemes(OperatorAuthentication.Scheme)
            .RequireAuthenticatedUser()
            .RequireClaim(OperatorAuthentication.PrivilegeClaimType, privilege.ToString()));
}
