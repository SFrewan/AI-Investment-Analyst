using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AI.Investment.Infrastructure.Persistence.Configurations;

/// <summary>Maps one provider request/response exchange. Append-only, additive, references nothing.</summary>
/// <remarks>
/// <para>
/// Keyed by a surrogate id with a unique constraint on (run, ordinal) rather than a composite
/// primary key: the natural key is the pair, but a surrogate keeps the row addressable on its own
/// while the unique index still refuses a second row claiming to be the same exchange.
/// </para>
/// <para>
/// No foreign key to <c>ingestion_runs</c> is declared. The gateway writes these only after the run
/// is durable, so the reference is sound in practice - but a hard constraint would mean a provenance
/// write could fail on a run recorded through a different unit of work, and evidence that refuses to
/// be written is worse than evidence pointing at a run that a later query finds. The index on
/// <c>ingestion_run_id</c> gives the join without the coupling.
/// </para>
/// <para>
/// The two JSON columns are <c>jsonb</c> because they are queried, not just displayed: "which
/// exchanges sent a from= that disagrees with the run's window" is the question a future empty-series
/// policy has to ask, and it should not be a string scan.
/// </para>
/// </remarks>
public sealed class ProviderExchangeConfiguration : IEntityTypeConfiguration<ProviderExchange>
{
    public void Configure(EntityTypeBuilder<ProviderExchange> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("provider_exchanges");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(e => e.IngestionRunId)
            .HasColumnName("ingestion_run_id")
            .HasConversion(id => id.Value, value => IngestionRunId.Create(value))
            .IsRequired();

        builder.Property(e => e.ExchangeOrdinal).HasColumnName("exchange_ordinal").IsRequired();

        builder.Property(e => e.SourceId)
            .HasColumnName("source_id")
            .HasMaxLength(SourceId.MaxLength)
            .HasConversion(id => id.Value, value => SourceId.Create(value))
            .IsRequired();

        builder.Property(e => e.RequestFingerprint)
            .HasColumnName("request_fingerprint")
            .HasMaxLength(ProviderExchange.MaxFingerprintLength)
            .IsRequired();

        builder.Property(e => e.EndpointTemplate)
            .HasColumnName("endpoint_template")
            .HasMaxLength(ProviderExchange.MaxEndpointTemplateLength)
            .IsRequired();

        builder.Property(e => e.RedactedRequestParameters)
            .HasColumnName("redacted_request_parameters")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(e => e.RequestedFromUtc).HasColumnName("requested_from_utc");

        builder.Property(e => e.RequestedToUtc).HasColumnName("requested_to_utc");

        builder.Property(e => e.HttpStatusCode).HasColumnName("http_status_code");

        builder.Property(e => e.SelectedResponseHeaders)
            .HasColumnName("selected_response_headers")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(e => e.RetrievedAtUtc).HasColumnName("retrieved_at_utc").IsRequired();

        builder.Property(e => e.ResponseContentHash)
            .HasColumnName("response_content_hash")
            .HasMaxLength(ContentHash.HexLength);

        builder.Property(e => e.ResponseByteLength).HasColumnName("response_byte_length");

        builder.Property(e => e.ProviderCorrelationId)
            .HasColumnName("provider_correlation_id")
            .HasMaxLength(ProviderExchange.MaxCorrelationIdLength);

        builder.Property(e => e.RecordedAtUtc).HasColumnName("recorded_at_utc").IsRequired();

        builder.Ignore(e => e.ProducedAPayload);

        // One row per exchange, enforced rather than assumed.
        builder.HasIndex(e => new { e.IngestionRunId, e.ExchangeOrdinal })
            .IsUnique()
            .HasDatabaseName("ux_provider_exchanges_run_ordinal");

        builder.HasIndex(e => e.IngestionRunId)
            .HasDatabaseName("ix_provider_exchanges_ingestion_run_id");

        builder.HasIndex(e => e.RequestFingerprint)
            .HasDatabaseName("ix_provider_exchanges_request_fingerprint");

        // The index that answers the question the archive cannot: which runs produced these bytes.
        builder.HasIndex(e => e.ResponseContentHash)
            .HasDatabaseName("ix_provider_exchanges_response_content_hash");

        builder.HasIndex(e => e.RetrievedAtUtc)
            .HasDatabaseName("ix_provider_exchanges_retrieved_at_utc");
    }
}
