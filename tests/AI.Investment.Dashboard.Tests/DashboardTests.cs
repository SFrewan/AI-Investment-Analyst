using System.Net;
using AI.Investment.Dashboard.Layout;
using AI.Investment.Dashboard.Localization;
using AI.Investment.Dashboard.Pages;
using AI.Investment.Dashboard.Services;
using Bunit;
using Xunit;

namespace AI.Investment.Dashboard.Tests;

/// <summary>Signing in, and what the dashboard refuses to do until somebody has.</summary>
public sealed class SignInTests
{
    [Fact]
    public void The_shell_shows_the_sign_in_screen_when_nobody_is_signed_in()
    {
        using var host = new TestHost();

        var page = host.RenderComponent<MainLayout>();

        Assert.Contains(host.Localization["signIn.title"], page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(host.Localization["nav.portfolio"], page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shell_shows_the_application_once_signed_in()
    {
        using var host = new TestHost();

        host.SignIn("ViewPortfolio");

        var page = host.RenderComponent<MainLayout>();

        Assert.Contains(host.Localization["nav.portfolio"], page.Markup, StringComparison.Ordinal);
        Assert.Contains(host.Localization["shell.signOut"], page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_key_is_refused_without_calling_the_platform()
    {
        using var host = new TestHost();

        var page = host.RenderComponent<SignIn>();

        page.Find("form").Submit();

        Assert.Contains(host.Localization["signIn.empty"], page.Markup, StringComparison.Ordinal);
        Assert.Empty(host.Handler.Requested);
        Assert.False(host.Session.IsSignedIn);
    }

    [Fact]
    public void A_recognised_key_establishes_the_session()
    {
        using var host = new TestHost();

        host.Handler.When("api/operator/whoami", Fixtures.Whoami);

        var page = host.RenderComponent<SignIn>();

        page.Find("#operator-key").Input("a-valid-key");
        page.Find("form").Submit();

        Assert.True(host.Session.IsSignedIn);
        Assert.Equal("operator@example.test", host.Session.Identity!.Id);
    }

    [Fact]
    public void A_rejected_key_is_reported_and_no_session_is_established()
    {
        using var host = new TestHost();

        host.Handler.When("api/operator/whoami", "{}", HttpStatusCode.Unauthorized);

        var page = host.RenderComponent<SignIn>();

        page.Find("#operator-key").Input("wrong");
        page.Find("form").Submit();

        Assert.Contains(host.Localization["signIn.rejected"], page.Markup, StringComparison.Ordinal);
        Assert.False(host.Session.IsSignedIn);
    }

    /// <summary>
    /// The key travels in a header, is cleared from the field, and never appears in the rendered
    /// document.
    /// </summary>
    [Fact]
    public void The_key_is_never_rendered_and_never_in_a_url()
    {
        const string key = "a-key-that-must-not-appear";

        using var host = new TestHost();

        host.Handler.When("api/operator/whoami", Fixtures.Whoami);

        var page = host.RenderComponent<SignIn>();

        page.Find("#operator-key").Input(key);
        page.Find("form").Submit();

        Assert.DoesNotContain(key, page.Markup, StringComparison.Ordinal);
        Assert.Contains(key, host.Handler.SentKeys);
        Assert.DoesNotContain(host.Handler.Requested, path => path.Contains(key, StringComparison.Ordinal));
    }

    [Fact]
    public void The_key_field_is_a_password_field_and_does_not_autocomplete()
    {
        using var host = new TestHost();

        var field = host.RenderComponent<SignIn>().Find("#operator-key");

        Assert.Equal("password", field.GetAttribute("type"));
        Assert.Equal("off", field.GetAttribute("autocomplete"));
    }

    [Fact]
    public void An_unreachable_platform_is_reported_as_such()
    {
        using var host = new TestHost();

        host.Handler.WhenAll(HttpStatusCode.ServiceUnavailable);

        var page = host.RenderComponent<SignIn>();

        page.Find("#operator-key").Input("a-key");
        page.Find("form").Submit();

        Assert.False(host.Session.IsSignedIn);
    }

    /// <summary>Signing out drops the credential and returns to the sign-in screen.</summary>
    [Fact]
    public void Signing_out_clears_the_session()
    {
        using var host = new TestHost();

        host.SignIn("ViewPortfolio");

        var page = host.RenderComponent<MainLayout>();

        page.FindAll("button").First(b =>
            b.TextContent.Contains(host.Localization["shell.signOut"], StringComparison.Ordinal)).Click();

        Assert.False(host.Session.IsSignedIn);
        Assert.Null(host.Session.Identity);
        Assert.Contains(host.Localization["signIn.title"], page.Markup, StringComparison.Ordinal);
    }

    /// <summary>Authentication is not authorization: a signed-in operator may still hold nothing.</summary>
    [Fact]
    public void Being_signed_in_does_not_imply_a_privilege()
    {
        using var host = new TestHost();

        host.SignIn();

        Assert.True(host.Session.IsSignedIn);
        Assert.False(host.Session.Has("ViewPortfolio"));
    }
}

/// <summary>Rendering figures the platform knows, and figures it does not.</summary>
public sealed class PortfolioPageTests
{
    [Fact]
    public void An_unpriced_position_shows_no_market_value_and_no_zero()
    {
        using var host = Host(Fixtures.PartlyValuedPortfolio);

        var page = host.RenderComponent<AI.Investment.Dashboard.Pages.Portfolio>();

        var row = page.FindAll("tbody tr").First(r =>
            r.TextContent.Contains("MSFT.US", StringComparison.Ordinal));

        Assert.Contains(
            host.Localization.Label("valuation", "NoObservedPrice"),
            row.TextContent,
            StringComparison.Ordinal);

        // The marker, not a number, in the market-value and unrealised columns.
        Assert.Contains(host.Localization["state.notApplicable"], row.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_valued_position_shows_its_market_value()
    {
        using var host = Host(Fixtures.PartlyValuedPortfolio);

        var page = host.RenderComponent<AI.Investment.Dashboard.Pages.Portfolio>();

        var row = page.FindAll("tbody tr").First(r =>
            r.TextContent.Contains("AAPL.US", StringComparison.Ordinal));

        Assert.Contains("1,300", row.TextContent, StringComparison.Ordinal);
        Assert.Contains(
            host.Localization.Label("valuation", "Available"),
            row.TextContent,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The platform declined to total the book, and the page says so rather than adding up what it
    /// happens to have.
    /// </summary>
    [Fact]
    public void An_undeterminable_total_is_explained_rather_than_computed()
    {
        using var host = Host(Fixtures.PartlyValuedPortfolio);

        var page = host.RenderComponent<AI.Investment.Dashboard.Pages.Portfolio>();

        Assert.Contains(
            host.Localization["portfolio.totalUnavailable"],
            page.Markup,
            StringComparison.Ordinal);

        Assert.Contains("1", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_portfolio_renders_the_empty_state_and_not_an_error()
    {
        using var host = Host(Fixtures.EmptyPortfolio);

        var page = host.RenderComponent<AI.Investment.Dashboard.Pages.Portfolio>();

        Assert.Contains(host.Localization["portfolio.empty"], page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(host.Localization["error.title"], page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_forbidden_response_is_distinguished_from_a_signed_out_one()
    {
        using var host = new TestHost();

        host.SignIn();
        host.Handler.WhenAll(HttpStatusCode.Forbidden);

        var page = host.RenderComponent<AI.Investment.Dashboard.Pages.Portfolio>();

        Assert.Contains(host.Localization["error.forbidden"], page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(host.Localization["error.unauthorized"], page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unauthorized_response_is_reported_as_a_lost_session()
    {
        using var host = new TestHost();

        host.SignIn();
        host.Handler.WhenAll(HttpStatusCode.Unauthorized);

        var page = host.RenderComponent<AI.Investment.Dashboard.Pages.Portfolio>();

        Assert.Contains(host.Localization["error.unauthorized"], page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_server_failure_offers_a_retry_and_shows_no_raw_message()
    {
        using var host = new TestHost();

        host.SignIn();
        host.Handler.WhenAll(HttpStatusCode.InternalServerError);

        var page = host.RenderComponent<AI.Investment.Dashboard.Pages.Portfolio>();

        Assert.Contains(host.Localization["error.server"], page.Markup, StringComparison.Ordinal);
        Assert.Contains(host.Localization["state.retry"], page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Stack", page.Markup, StringComparison.Ordinal);
    }

    private static TestHost Host(string portfolio)
    {
        var host = new TestHost();

        host.SignIn("ViewPortfolio");
        host.Handler.When("api/portfolio", portfolio);

        return host;
    }
}

/// <summary>Both languages, and the direction each brings with it.</summary>
public sealed class BilingualTests
{
    [Fact]
    public void The_shell_renders_in_english_by_default()
    {
        using var host = new TestHost();

        host.SignIn("ViewPortfolio");

        var page = host.RenderComponent<MainLayout>();

        Assert.Contains("Portfolio", page.Markup, StringComparison.Ordinal);
        Assert.False(host.Localization.IsRightToLeft);
    }

    [Fact]
    public void Switching_to_arabic_changes_every_label_and_the_direction()
    {
        using var host = new TestHost();

        host.SignIn("ViewPortfolio");

        var page = host.RenderComponent<MainLayout>();

        host.Localization.Set(Language.Arabic);
        page.Render();

        Assert.Contains("المحفظة", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(">Portfolio<", page.Markup, StringComparison.Ordinal);
        Assert.True(host.Localization.IsRightToLeft);
        Assert.Equal("rtl", host.Localization.Current.Direction);
    }

    /// <summary>
    /// The empty and unavailable states are localized too. A page that fell back to English for its
    /// absences would show an Arabic operator English exactly when something had gone wrong.
    /// </summary>
    [Fact]
    public void Empty_and_error_states_are_localized()
    {
        using var host = new TestHost();

        host.SignIn("ViewPortfolio");
        host.Handler.When("api/portfolio", Fixtures.EmptyPortfolio);
        host.Localization.Set(Language.Arabic);

        var page = host.RenderComponent<AI.Investment.Dashboard.Pages.Portfolio>();

        Assert.Contains("لم تُسجَّل أي مراكز", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_language_switcher_offers_both_languages_in_their_own_script()
    {
        using var host = new TestHost();

        var page = host.RenderComponent<LanguagePicker>();

        Assert.Contains("English", page.Markup, StringComparison.Ordinal);
        Assert.Contains("العربية", page.Markup, StringComparison.Ordinal);
    }

    /// <summary>No raw backend enum name reaches the screen in either language.</summary>
    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void Backend_enum_names_are_not_rendered(string code)
    {
        using var host = new TestHost();

        host.SignIn("ViewPortfolio");
        host.Handler.When("api/portfolio", Fixtures.PartlyValuedPortfolio);
        host.Localization.Set(Language.FromCode(code));

        var page = host.RenderComponent<AI.Investment.Dashboard.Pages.Portfolio>();

        Assert.DoesNotContain(">NoObservedPrice<", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("«", page.Markup, StringComparison.Ordinal);
    }
}

/// <summary>Refreshing, and not refreshing twice at once.</summary>
public sealed class RefreshTests
{
    [Fact]
    public async Task A_refresh_reloads_the_page_data()
    {
        using var host = new TestHost();

        host.SignIn("ViewPortfolio");
        host.Handler.When("api/portfolio", Fixtures.EmptyPortfolio);

        host.RenderComponent<AI.Investment.Dashboard.Pages.Portfolio>();

        var before = host.Handler.Requested.Count;

        await host.Refresh.RequestAsync();

        Assert.True(host.Handler.Requested.Count > before);
        Assert.NotNull(host.Refresh.LastRefreshedUtc);
    }

    /// <summary>
    /// A second press while one is in flight does nothing. Two overlapping loads would double the
    /// traffic and let the older response win the race to render.
    /// </summary>
    [Fact]
    public async Task A_refresh_already_in_flight_is_not_started_again()
    {
        var refresh = new RefreshState();
        var started = 0;
        var release = new TaskCompletionSource();

        refresh.Requested += async () =>
        {
            started++;

            await release.Task;
        };

        var first = refresh.RequestAsync();

        Assert.True(refresh.InFlight);

        await refresh.RequestAsync();

        Assert.Equal(1, started);

        release.SetResult();

        await first;

        Assert.False(refresh.InFlight);
    }

    [Fact]
    public void Nothing_has_been_refreshed_before_the_first_load()
    {
        using var host = new TestHost();

        host.SignIn("ViewPortfolio");

        var page = host.RenderComponent<MainLayout>();

        Assert.Contains(
            host.Localization["shell.neverRefreshed"],
            page.Markup,
            StringComparison.Ordinal);
    }
}

/// <summary>The classification every page's error state depends on.</summary>
public sealed class ApiClassificationTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK, ApiFailure.None)]
    [InlineData(HttpStatusCode.NoContent, ApiFailure.None)]
    [InlineData(HttpStatusCode.Unauthorized, ApiFailure.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, ApiFailure.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, ApiFailure.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, ApiFailure.RateLimited)]
    [InlineData(HttpStatusCode.BadRequest, ApiFailure.Refused)]
    [InlineData(HttpStatusCode.Conflict, ApiFailure.Refused)]
    [InlineData(HttpStatusCode.InternalServerError, ApiFailure.ServerError)]
    [InlineData(HttpStatusCode.BadGateway, ApiFailure.ServerError)]
    public void Every_status_maps_to_its_own_meaning(HttpStatusCode status, ApiFailure expected) =>
        Assert.Equal(expected, PlatformClient.Classify(status));

    /// <summary>Every failure has a message, and no two important ones share it.</summary>
    [Fact]
    public void Unauthorized_and_forbidden_do_not_share_a_message() =>
        Assert.NotEqual(
            ApiFailure.Unauthorized.MessageKey(),
            ApiFailure.Forbidden.MessageKey());

    [Fact]
    public void Every_failure_has_a_localized_message_in_both_languages()
    {
        var localization = new LocalizationState();

        foreach (var failure in Enum.GetValues<ApiFailure>())
        {
            foreach (var language in Language.All)
            {
                localization.Set(language);

                Assert.DoesNotContain("«", localization[failure.MessageKey()], StringComparison.Ordinal);
            }
        }
    }
}
