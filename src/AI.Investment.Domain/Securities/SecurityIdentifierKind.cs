namespace AI.Investment.Domain.Securities;

/// <summary>The kind of identifier an assertion carries.</summary>
/// <remarks>
/// Listed as kinds rather than as properties on the security because an identifier is a
/// <em>claim made by a source over an interval</em>, not an attribute of the instrument. Acquiring
/// FIGI, CUSIP or ISIN is deferred; naming the kinds costs nothing and stops the first one that
/// arrives from being modelled as a second ticker column.
/// </remarks>
public enum SecurityIdentifierKind
{
    /// <summary>Unset. Present so the default value is representable and obviously wrong.</summary>
    Unknown = 0,

    /// <summary>An exchange ticker. Recycled between issuers, and never a key.</summary>
    Ticker = 1,

    /// <summary>A vendor's own symbol, meaningful only to that vendor.</summary>
    VendorSymbol = 2,

    /// <summary>OpenFIGI identifier.</summary>
    Figi = 3,

    /// <summary>CUSIP.</summary>
    Cusip = 4,

    /// <summary>ISIN.</summary>
    Isin = 5,
}
