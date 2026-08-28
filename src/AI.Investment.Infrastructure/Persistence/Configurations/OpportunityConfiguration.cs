using System.Text.Json;
using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AI.Investment.Infrastructure.Persistence.Configurations;

/// <summary>Maps the opportunity aggregate.</summary>
/// <remarks>
/// <para>
/// Three storage shapes, chosen by what is asked of each part. Identity, status and timestamps are
/// real columns because every list query filters on them. The score is real columns because
/// opportunities are ordered by it. Economics and risk are <c>jsonb</c>, because nothing
/// safety-relevant is queried from either and a column per money amount would be a dozen columns
/// nobody reads individually.
/// </para>
/// <para>
/// The per-type <c>detail</c> payload is <c>jsonb</c> for a stronger reason: it is the part that
/// should never need a migration when a new opportunity type arrives, which is most of what makes
/// adding one cheap.
/// </para>
/// </remarks>
public sealed class OpportunityConfiguration : IEntityTypeConfiguration<Opportunity>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    public void Configure(EntityTypeBuilder<Opportunity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("opportunities");

        builder.HasKey(o => o.OpportunityId);

        builder.Property(o => o.OpportunityId)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new OpportunityId(value))
            .ValueGeneratedNever();

        builder.Property(o => o.Type)
            .HasColumnName("type")
            .HasMaxLength(OpportunityType.MaxLength)
            .HasConversion(type => type.Value, value => OpportunityType.Create(value))
            .IsRequired();

        builder.Property(o => o.Title)
            .HasColumnName("title")
            .HasMaxLength(Opportunity.MaxTitleLength)
            .IsRequired();

        builder.Property(o => o.Description)
            .HasColumnName("description")
            .HasMaxLength(Opportunity.MaxDescriptionLength)
            .IsRequired();

        builder.Property(o => o.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(40)
            .IsRequired();

        builder.Property(o => o.Confidence)
            .HasColumnName("confidence")
            .HasPrecision(5, 4)
            .HasConversion(
                confidence => confidence!.Value,
                value => Domain.ValueObjects.Confidence.Create(value));

        builder.Property(o => o.ApprovalTokenId).HasColumnName("approval_token_id");
        builder.Property(o => o.ExecutionId).HasColumnName("execution_id");

        builder.Property(o => o.Resolution)
            .HasColumnName("resolution")
            .HasMaxLength(Opportunity.MaxReasonLength);

        builder.Property(o => o.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(o => o.StatusChangedAtUtc).HasColumnName("status_changed_at_utc").IsRequired();

        builder.Ignore(o => o.IsTerminal);
        builder.Ignore(o => o.Evidence);
        builder.Ignore(o => o.ProposalIds);

        // The evidence an opportunity rests on, and the proposals it produced. Both are read whole
        // or not at all, so jsonb over a join table: a child table would buy queryability nobody
        // asks for and cost a join on every read.
        builder.Property<List<ClaimId>>("_evidence")
            .HasColumnName("evidence")
            .HasColumnType("jsonb")
            .HasConversion(
                claims => JsonSerializer.Serialize(claims.Select(c => c.Value).ToList(), JsonOptions),
                json => (JsonSerializer.Deserialize<List<Guid>>(json, JsonOptions) ?? new List<Guid>())
                    .Select(ClaimId.Create)
                    .ToList(),
                new ValueComparer<List<ClaimId>>(
                    (left, right) => left != null && right != null && left.SequenceEqual(right),
                    list => list.Aggregate(0, (hash, item) => HashCode.Combine(hash, item)),
                    list => new List<ClaimId>(list)))
            .IsRequired();

        builder.Property<List<Guid>>("_proposalIds")
            .HasColumnName("proposal_ids")
            .HasColumnType("jsonb")
            .HasConversion(
                ids => JsonSerializer.Serialize(ids, JsonOptions),
                json => JsonSerializer.Deserialize<List<Guid>>(json, JsonOptions) ?? new List<Guid>(),
                new ValueComparer<List<Guid>>(
                    (left, right) => left != null && right != null && left.SequenceEqual(right),
                    list => list.Aggregate(0, (hash, item) => HashCode.Combine(hash, item)),
                    list => new List<Guid>(list)))
            .IsRequired();

        // Only the inputs are stored; the derived figures are recomputed by the domain factory on
        // read, so a stored profit can never disagree with the numbers it came from.
        builder.Property(o => o.Economics)
            .HasColumnName("economics")
            .HasColumnType("jsonb")
            .HasConversion(
                economics => OpportunityJson.SerializeEconomics(economics!),
                json => OpportunityJson.DeserializeEconomics(json));

        builder.Property(o => o.Risk)
            .HasColumnName("risk")
            .HasColumnType("jsonb")
            .HasConversion(
                risk => OpportunityJson.SerializeRisk(risk!),
                json => OpportunityJson.DeserializeRisk(json));

        builder.OwnsOne(o => o.Subject, subject =>
        {
            subject.Property(s => s.Kind)
                .HasColumnName("subject_kind")
                .HasMaxLength(IngestionSubject.MaxKindLength)
                .IsRequired();

            subject.Property(s => s.Identifier)
                .HasColumnName("subject_identifier")
                .HasMaxLength(IngestionSubject.MaxIdentifierLength);

            subject.Ignore(s => s.IsSpecific);

            subject.HasIndex(s => new { s.Kind, s.Identifier })
                .HasDatabaseName("ix_opportunities_subject");
        });

        builder.Navigation(o => o.Subject).IsRequired();

        builder.OwnsOne(o => o.Source, source =>
        {
            source.Property(s => s.DiscovererId)
                .HasColumnName("discoverer_id")
                .HasMaxLength(SourceId.MaxLength)
                .HasConversion(id => id.Value, value => SourceId.Create(value))
                .IsRequired();

            source.Property(s => s.DiscoveredAtUtc).HasColumnName("discovered_at_utc").IsRequired();
        });

        builder.Navigation(o => o.Source).IsRequired();

        builder.OwnsOne(o => o.Detail, detail =>
        {
            detail.Property(d => d.Type)
                .HasColumnName("detail_type")
                .HasMaxLength(OpportunityType.MaxLength)
                .HasConversion(type => type.Value, value => OpportunityType.Create(value))
                .IsRequired();

            detail.Property(d => d.Json)
                .HasColumnName("detail")
                .HasColumnType("jsonb")
                .IsRequired();
        });

        builder.Navigation(o => o.Detail).IsRequired();

        // Real columns rather than jsonb: this is the field opportunities are ordered by, and the
        // version beside it is what makes two stored scores comparable at all.
        builder.OwnsOne(o => o.Score, score =>
        {
            // Marked required inside an optional owned type: it is the property EF uses to tell
            // "no score yet" from "a score whose every field happens to be null".
            score.Property(s => s.Metric)
                .HasColumnName("score_metric")
                .HasMaxLength(MetricId.MaxLength)
                .HasConversion(metric => metric.Value, value => MetricId.Create(value))
                .IsRequired();

            score.Property(s => s.Value).HasColumnName("score_value").HasPrecision(18, 6);

            score.Property(s => s.Version)
                .HasColumnName("score_version")
                .HasMaxLength(20)
                .HasConversion(version => version.ToString(), value => CalculationVersion.Parse(value));

            score.Property(s => s.AsOfUtc).HasColumnName("score_as_of_utc");
        });

        // The two questions a dashboard asks: what is in this state, and what changed recently.
        builder.HasIndex(o => new { o.Status, o.StatusChangedAtUtc })
            .HasDatabaseName("ix_opportunities_status");
    }
}
