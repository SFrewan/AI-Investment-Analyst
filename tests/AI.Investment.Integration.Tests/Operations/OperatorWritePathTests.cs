using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Operations;
using AI.Investment.Infrastructure.Actions;
using AI.Investment.Infrastructure.Persistence;
using AI.Investment.Infrastructure.Policy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Investment.Integration.Tests.Operations;

/// <summary>
/// The two writes the operator surface needs, against a real PostgreSQL and the real write guard.
/// </summary>
/// <remarks>
/// <para>
/// Both claims are about the guard rather than about the code that calls it, and neither could be
/// established anywhere else. An escalation could not be answered at all until this block: the guard
/// refuses every modification of an operations record whose changed columns are not on an
/// allow-list, and an escalation had none - so <c>Acknowledge</c> and <c>Resolve</c> were
/// implemented in the domain, tested there, and would have thrown at the database. The allow-list is
/// four columns, and the test that matters is the one proving it is still only four.
/// </para>
/// <para>
/// The kill switch is the other: engaging it writes a row that is not exempt from the authorisation
/// requirement, so it commits only inside the window the gateway opens - which is what makes
/// engaging an audited act rather than an update.
/// </para>
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class OperatorWritePathTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public OperatorWritePathTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- answering an escalation ---------------------------------------------------------------

    /// <summary>An escalation can be acknowledged and resolved, and the answer survives.</summary>
    [SkippableFact]
    public async Task An_escalation_can_be_answered_and_the_answer_persists()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var id = await SeedEscalationAsync();

        var authorization = new ScopedWriteAuthorization();

        await using (var context = _fixture.CreateContext(authorization))
        {
            var escalation = await context.Escalations.FirstAsync(e => e.EscalationId == id);

            escalation.Acknowledge("alex@example.test", Now);
            escalation.Resolve("alex@example.test", "Reviewed and declined.", Now.AddMinutes(1));

            using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
            {
                await context.SaveChangesAsync();
            }
        }

        await using var verification = _fixture.CreateContext(new ScopedWriteAuthorization());

        var stored = await verification.Escalations.FirstAsync(e => e.EscalationId == id);

        Assert.True(stored.IsResolved);
        Assert.Equal("alex@example.test", stored.AcknowledgedBy);
        Assert.Contains("declined", stored.Resolution!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The question itself stays immutable. An escalation whose expiry could be pushed out is one
    /// that is never unhandled, and the count of unhandled escalations is a measurement unattended
    /// operation is judged on.
    /// </summary>
    [SkippableFact]
    public async Task The_question_an_escalation_asked_cannot_be_rewritten()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var id = await SeedEscalationAsync();

        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        var escalation = await context.Escalations.FirstAsync(e => e.EscalationId == id);

        context.Entry(escalation).Property(e => e.ExpiresAtUtc).CurrentValue = Now.AddYears(1);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            // Refused even inside an authorisation window, exactly as a cycle's or an outbox
            // message's identity is. Authorisation permits an effect; it has never permitted
            // editing the account of what the platform asked.
            var error = await Assert.ThrowsAsync<UnauthorizedWriteException>(
                () => context.SaveChangesAsync());

            Assert.Contains("record their own progress", error.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>And it cannot be deleted, answered or not.</summary>
    [SkippableFact]
    public async Task An_escalation_cannot_be_deleted()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var id = await SeedEscalationAsync();

        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        context.Escalations.Remove(await context.Escalations.FirstAsync(e => e.EscalationId == id));

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            await Assert.ThrowsAsync<UnauthorizedWriteException>(() => context.SaveChangesAsync());
        }
    }

    // ---- engaging the kill switch --------------------------------------------------------------

    /// <summary>
    /// Engaging writes a flag the read side sees, and it commits only inside an authorisation
    /// window.
    /// </summary>
    [SkippableFact]
    public async Task Engaging_the_kill_switch_is_visible_to_the_read_side()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();

        await using (var context = _fixture.CreateContext(authorization))
        {
            var administration = new EfKillSwitchAdministration(context);

            using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
            {
                await administration.EngageAsync(capability: null, "the provider is misbehaving", Now);

                // Engaging twice adds nothing: the read side asks whether any engaged flag matches,
                // so a second row would change no answer and leave two reasons for one act.
                await administration.EngageAsync(capability: null, "again", Now.AddMinutes(1));
            }
        }

        await using var verification = _fixture.CreateContext(new ScopedWriteAuthorization());

        Assert.Equal(1, await verification.KillSwitchFlags.CountAsync());

        var flag = await verification.KillSwitchFlags.FirstAsync();

        Assert.True(flag.Engaged);
        Assert.Null(flag.Capability);
        Assert.Contains("misbehaving", flag.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Outside an authorisation window it does not commit. Engaging is an audited act rather than
    /// an update, and the guard is what makes that true rather than the caller remembering.
    /// </summary>
    [SkippableFact]
    public async Task Engaging_the_kill_switch_outside_the_seam_is_refused()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        await Assert.ThrowsAsync<UnauthorizedWriteException>(() =>
            new EfKillSwitchAdministration(context)
                .EngageAsync(capability: null, "no window is open", Now));

        await using var verification = _fixture.CreateContext(new ScopedWriteAuthorization());

        Assert.Equal(0, await verification.KillSwitchFlags.CountAsync());
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>
    /// An escalation, written the way the platform writes one: creatable with nothing authorised,
    /// because a refusal is exactly the situation in which no authorisation exists.
    /// </summary>
    private async Task<Guid> SeedEscalationAsync()
    {
        var escalation = Escalation.Raise(
            Capability.OpportunityManagement,
            EscalationReason.NoAutonomyGrant,
            "A proposal needs a person.",
            Now,
            TimeSpan.FromHours(24));

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        await context.Escalations.AddAsync(escalation);
        await context.SaveChangesAsync();

        return escalation.EscalationId;
    }
}
