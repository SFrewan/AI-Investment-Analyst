namespace AI.Investment.Api.Security;

/// <summary>Names shared between the authentication handler, the policies and the tests.</summary>
public static class OperatorAuthentication
{
    /// <summary>The authentication scheme operator endpoints require.</summary>
    public const string Scheme = "OperatorKey";

    /// <summary>The header the operator key arrives in.</summary>
    public const string HeaderName = "X-Operator-Key";

    /// <summary>The claim type each granted privilege is written as.</summary>
    public const string PrivilegeClaimType = "operator:privilege";
}
