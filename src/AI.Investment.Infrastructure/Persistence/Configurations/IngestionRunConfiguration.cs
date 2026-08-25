using System.Text.Json;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AI.Investment.Infrastructure.Persistence.Configurations;

/// <summary>Maps the append-only ingestion ledger.</summary>
/// <remarks>
/// <para>
/// The request is flattened into real columns rather than serialised, because every part of it is
/// something an operator asks about: which source, which category, which subject, which region,
/// when. "Show me every run for this company in the last week" has to be a query, not a scan.
/// </para>
/// <para>
/// <strong>The request fingerprint is a shadow property.</strong> It is derived - a SHA-256 over
/// the canonical request - so it is not domain state and does not belong on
/// <see cref="IngestionRequest"/> as a stored field. But it cannot be computed in SQL either, and
/// <c>IIngestionRunStore.HasCompletedAsync</c> needs to look runs up by it. A shadow property is
/// exactly the right shape: stored and indexed, absent from the object model, written by the store
/// that knows how to derive it.
/// </para>
/// <para>
/// Artifacts are content hashes - an ordered list of fixed-width strings with no independent
/// identity. <c>jsonb</c> in one column, like audit detail, rather than a child table that would
/// add a join to every read for rows nothing ever queries individually.
/// </para>
/// </remarks>
public sealed class IngestionRunConfiguration : IEntityTypeConfiguration<IngestionRun>
{
    /// <summary>Shadow property holding <see cref="IngestionRequest.Fingerprint"/>.</summary>
    public const string FingerprintProperty = "RequestFingerprint";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    public void Configure(EntityTypeBuilder<IngestionRun> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ingestion_runs");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new IngestionRunId(value))
            .ValueGeneratedNever();

        builder.Property(r => r.StartedAtUtc).HasColumnName("started_at_utc").IsRequired();
        builder.Property(r => r.CompletedAtUtc).HasColumnName("completed_at_utc");

        builder.Property(r => r.Outcome)
            .HasColumnName("outcome")
            .HasConversion<string>()
            .HasMaxLength(40)
            .IsRequired();

        builder.Property(r => r.Reason)
            .HasColumnName("reason")
            .HasMaxLength(IngestionRun.MaxFailureReasonLength);

        builder.Property(r => r.RefusalRuleId)
            .HasColumnName("refusal_rule_id")
            .HasMaxLength(120);

        // Computed from other state.
        builder.Ignore(r => r.IsComplete);
        builder.Ignore(r => r.SourceId);
        builder.Ignore(r => r.Artifacts);

        builder.Property<List<ContentHash>>("_artifacts")
            .HasColumnName("artifacts")
            .HasColumnType("jsonb")
            .HasConversion(
                artifacts => SerialiseArtifacts(artifacts),
                json => DeserialiseArtifacts(json),
                new ValueComparer<List<ContentHash>>(
                    (left, right) => left != null && right != null && left.SequenceEqual(right),
                    list => list.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.Value)),
                    list => new List<ContentHash>(list)))
            .IsRequired();

        builder.Property<string>(FingerprintProperty)
            .HasColumnName("request_fingerprint")
            .HasMaxLength(ContentHash.HexLength)
            .IsRequired();

        builder.OwnsOne(r => r.Request, request =>
        {
            request.Property(x => x.SourceId)
                .HasColumnName("source_id")
                .HasMaxLength(SourceId.MaxLength)
                .HasConversion(id => id.Value, value => SourceId.Create(value))
                .IsRequired();

            request.Property(x => x.Category)
                .HasColumnName("category")
                .HasConversion<string>()
                .HasMaxLength(60)
                .IsRequired();

            request.Property(x => x.Region)
                .HasColumnName("region")
                .HasMaxLength(10)
                .HasConversion(region => region.Code, value => Region.Create(value))
                .IsRequired();

            request.Property(x => x.CorrelationId)
                .HasColumnName("correlation_id")
                .HasMaxLength(CorrelationId.MaxLength)
                .HasConversion(id => id.Value, value => CorrelationId.Create(value))
                .IsRequired();

            request.Property(x => x.RequestedAtUtc).HasColumnName("requested_at_utc").IsRequired();

            request.OwnsOne(x => x.Subject, subject =>
            {
                subject.Property(s => s.Kind)
                    .HasColumnName("subject_kind")
                    .HasMaxLength(IngestionSubject.MaxKindLength)
                    .IsRequired();

                subject.Property(s => s.Identifier)
                    .HasColumnName("subject_identifier")
                    .HasMaxLength(IngestionSubject.MaxIdentifierLength);

                subject.Ignore(s => s.IsSpecific);
            });

            request.Navigation(x => x.Subject).IsRequired();

            // Optional: a request for "the latest value" has no period. Both columns are nullable
            // and EF treats both-null as an absent window.
            request.OwnsOne(x => x.Window, window =>
            {
                window.Property(w => w.StartUtc).HasColumnName("window_start_utc");
                window.Property(w => w.EndUtc).HasColumnName("window_end_utc");
                window.Ignore(w => w.Duration);
            });
        });

        builder.Navigation(r => r.Request).IsRequired();

        // "Has this exact request already succeeded?" - the read side of the idempotency key.
        builder.HasIndex(FingerprintProperty).HasDatabaseName("ix_ingestion_runs_request_fingerprint");

        // "When did we last try this source, and how has it been behaving?" - freshness and
        // reliability both read this way.
        builder.HasIndex(nameof(IngestionRun.StartedAtUtc))
            .HasDatabaseName("ix_ingestion_runs_started_at_utc");

        builder.HasIndex(nameof(IngestionRun.Outcome)).HasDatabaseName("ix_ingestion_runs_outcome");
    }

    private static string SerialiseArtifacts(List<ContentHash> artifacts) =>
        JsonSerializer.Serialize(artifacts.Select(hash => hash.Value).ToList(), JsonOptions);

    private static List<ContentHash> DeserialiseArtifacts(string json)
    {
        var values = JsonSerializer.Deserialize<List<string>>(json, JsonOptions);
        var artifacts = new List<ContentHash>();

        if (values is null)
        {
            return artifacts;
        }

        foreach (var value in values)
        {
            artifacts.Add(ContentHash.Create(value));
        }

        return artifacts;
    }
}
