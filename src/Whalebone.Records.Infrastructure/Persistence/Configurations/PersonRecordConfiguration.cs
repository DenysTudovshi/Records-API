using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Whalebone.Records.Application.Domain;

namespace Whalebone.Records.Infrastructure.Persistence.Configurations;

internal sealed class PersonRecordConfiguration : IEntityTypeConfiguration<PersonRecord>
{
    public void Configure(EntityTypeBuilder<PersonRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("person_records");

        builder.HasKey(record => record.Id);
        builder.Property(record => record.Id).HasColumnName("id").UseIdentityByDefaultColumn();

        builder.Property(record => record.ExternalId).HasColumnName("external_id").IsRequired();

        // The uniqueness that makes POST /save idempotent, and the index that makes
        // GET /{id} a single-row lookup rather than a scan.
        builder.HasIndex(record => record.ExternalId)
            .IsUnique()
            .HasDatabaseName("ix_person_records_external_id");

        builder.Property(record => record.Name)
            .HasColumnName("name").HasMaxLength(200).IsRequired();

        builder.Property(record => record.Email)
            .HasColumnName("email").HasMaxLength(320).IsRequired();

        // Stored as the absolute instant; the caller's offset lives in its own column so
        // the API can echo the original document back byte for byte.
        builder.Property(record => record.DateOfBirthUtc)
            .HasColumnName("date_of_birth_utc").HasColumnType("timestamp with time zone").IsRequired();

        builder.Property(record => record.DateOfBirthOffsetMinutes)
            .HasColumnName("date_of_birth_offset_minutes").IsRequired();

        builder.Property(record => record.CreatedAtUtc)
            .HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();

        builder.Property(record => record.UpdatedAtUtc)
            .HasColumnName("updated_at_utc").HasColumnType("timestamp with time zone").IsRequired();

        // Computed from the two columns above; without this EF hunts for a backing field.
        builder.Ignore(record => record.DateOfBirth);
    }
}
