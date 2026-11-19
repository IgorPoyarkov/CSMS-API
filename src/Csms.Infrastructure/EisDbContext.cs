using System.Text.Json;
using Csms.Domain;
using Microsoft.EntityFrameworkCore;

namespace Csms.Infrastructure;

/// <summary>
/// Контекст базы шлюза: схема <c>eis</c>, таблицы пакетов и событий.
///
/// Контекст ничего не знает о правилах переходов — только о том, как состояние
/// домена лежит в таблицах. Поэтому в домен и прикладной слой он не протянут.
/// </summary>
public sealed class EisDbContext(DbContextOptions<EisDbContext> options) : DbContext(options)
{
    private static readonly JsonSerializerOptions FlkJsonOptions = new(JsonSerializerDefaults.Web);

    public DbSet<EisPackageRow> Packages => Set<EisPackageRow>();

    public DbSet<EisPackageEventRow> Events => Set<EisPackageEventRow>();

    /// <summary>Версии справочников НСИ: своя схема, не смешиваем с пакетами.</summary>
    public DbSet<ReferenceDatasetRow> ReferenceDatasets => Set<ReferenceDatasetRow>();

    public DbSet<ReferenceItemRow> ReferenceItems => Set<ReferenceItemRow>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("eis");

        builder.Entity<EisPackageRow>(entity =>
        {
            entity.ToTable("packages");
            entity.HasKey(row => row.Id);

            entity.Property(row => row.DocumentType).HasMaxLength(64).IsRequired();
            entity.Property(row => row.ContractEisNumber).HasMaxLength(32).IsRequired();
            entity.Property(row => row.Ikz).HasMaxLength(64).IsRequired();
            entity.Property(row => row.PayloadSchemaVersion).HasMaxLength(64).IsRequired();
            entity.Property(row => row.Status).HasMaxLength(32).IsRequired();
            entity.Property(row => row.SignatureBase64);
            entity.Property(row => row.SignerThumbprint).HasMaxLength(128);
            entity.Property(row => row.EisGuid).HasMaxLength(128);
            entity.Property(row => row.LastError);

            // Ошибки ФЛК — как пришли от ЕИС, одним jsonb-полем: это данные
            // пакета, а не отдельная сущность, которую кто-то будет править
            entity.Property(row => row.FlkErrors)
                .HasColumnType("jsonb")
                .HasConversion(
                    value => JsonSerializer.Serialize(value, FlkJsonOptions),
                    json => JsonSerializer.Deserialize<List<FlkError>>(json, FlkJsonOptions) ?? new List<FlkError>());

            // Оптимистичная блокировка без изменений в домене: xmin — колонка,
            // которую PostgreSQL генерирует сам, но EF ведёт проверку в WHERE
            // обновлений и не выставляет её сам
            entity.Property(row => row.Xmin)
                .ValueGeneratedOnAddOrUpdate()
                .HasColumnName("xmin");
            entity.Property(row => row.Xmin).IsConcurrencyToken();

            entity.HasIndex(row => row.ContractEisNumber);
            entity.HasIndex(row => row.Status);
        });

        builder.Entity<EisPackageEventRow>(entity =>
        {
            entity.ToTable("package_events");
            entity.HasKey(row => row.Id);

            entity.Property(row => row.From).HasMaxLength(32).IsRequired();
            entity.Property(row => row.To).HasMaxLength(32).IsRequired();
            entity.Property(row => row.Comment).HasMaxLength(512).IsRequired();

            entity.HasIndex(row => new { row.PackageId, row.OrderIndex });
        });

        builder.Entity<ReferenceDatasetRow>(entity =>
        {
            entity.ToTable("datasets", "ref");
            entity.HasKey(row => row.Id);

            entity.Property(row => row.Code).HasMaxLength(64).IsRequired();
            entity.Property(row => row.Version).HasMaxLength(64).IsRequired();
            entity.Property(row => row.SourceDocument).HasMaxLength(256);
            entity.Property(row => row.SourceUrl).HasMaxLength(2048);
            entity.Property(row => row.SourceSha256).HasMaxLength(64);

            entity.HasIndex(row => new { row.Code, row.Version }).IsUnique();

            // Одна активная версия на справочник. Фильтр на уровне БД не даёт
            // двум импортам одновременно пометить разные версии активными.
            entity.HasIndex(row => row.Code)
                .IsUnique()
                .HasFilter("\"IsActive\" = TRUE");
        });

        builder.Entity<ReferenceItemRow>(entity =>
        {
            entity.ToTable("items", "ref");
            entity.HasKey(row => row.Id);

            entity.Property(row => row.Code).HasMaxLength(64).IsRequired();
            entity.Property(row => row.ParentCode).HasMaxLength(64);
            entity.Property(row => row.Name).HasMaxLength(2000);
            entity.Property(row => row.PayloadJson).HasColumnType("jsonb").IsRequired();

            entity.HasIndex(row => new { row.DatasetId, row.Code }).IsUnique();
            entity.HasIndex(row => new { row.DatasetId, row.ParentCode });

            entity.HasOne(row => row.Dataset)
                .WithMany(dataset => dataset.Items)
                .HasForeignKey(row => row.DatasetId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
