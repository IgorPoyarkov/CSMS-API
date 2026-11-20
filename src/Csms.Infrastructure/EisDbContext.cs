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

    /// <summary>Версии правил по официальным актам и сами правила по ОКПД2.</summary>
    public DbSet<NpaDocumentRow> NpaDocuments => Set<NpaDocumentRow>();

    public DbSet<NpaRuleRow> NpaRules => Set<NpaRuleRow>();

    // Пользователи, организации и права живут в отдельной схеме: это не
    // пакеты ЕИС и не версии справочников, у них свой жизненный цикл.
    public DbSet<AccessOrganizationRow> AccessOrganizations => Set<AccessOrganizationRow>();
    public DbSet<AccessUserRow> AccessUsers => Set<AccessUserRow>();
    public DbSet<AccessEmployeeRow> AccessEmployees => Set<AccessEmployeeRow>();
    public DbSet<AccessContractServiceRow> AccessContractServices => Set<AccessContractServiceRow>();
    public DbSet<AccessFunctionRow> AccessFunctions => Set<AccessFunctionRow>();
    public DbSet<AccessRequestRow> AccessRequests => Set<AccessRequestRow>();
    public DbSet<AccessRoleRow> AccessRoles => Set<AccessRoleRow>();
    public DbSet<AccessPermissionRow> AccessPermissions => Set<AccessPermissionRow>();
    public DbSet<AccessRolePermissionRow> AccessRolePermissions => Set<AccessRolePermissionRow>();
    public DbSet<AccessUserRoleRow> AccessUserRoles => Set<AccessUserRoleRow>();
    public DbSet<AccessSessionRow> AccessSessions => Set<AccessSessionRow>();

    // Изменения и расторжения контрактов — своя схема: у них другой жизненный
    // цикл, чем у пакетов ЕИС и справочников, и свой аудит.
    public DbSet<ContractChangeRow> ContractChanges => Set<ContractChangeRow>();
    public DbSet<ContractChangeDocumentRow> ContractChangeDocuments => Set<ContractChangeDocumentRow>();
    public DbSet<ContractChangeEventRow> ContractChangeEvents => Set<ContractChangeEventRow>();

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

        ConfigureAccess(builder);
        ConfigureNpa(builder);
        ConfigureContractChanges(builder);
    }

    /// <summary>
    /// Изменения и расторжения контрактов (ст. 95 44-ФЗ): снимок контракта,
    /// предлагаемые условия, документы и журнал переходов.
    ///
    /// Снимок хранится вместе с изменением, а не подтягивается из json-server:
    /// документ должен читаться и после того, как контракт изменят или удалят.
    /// </summary>
    private static void ConfigureContractChanges(ModelBuilder builder)
    {
        builder.Entity<ContractChangeRow>(entity =>
        {
            entity.ToTable("contract_changes", "contract");
            entity.HasKey(row => row.Id);

            entity.Property(row => row.OrganizationId).HasMaxLength(64).IsRequired();
            entity.Property(row => row.ContractId).HasMaxLength(64).IsRequired();
            entity.Property(row => row.ContractNumber).HasMaxLength(128).IsRequired();
            entity.Property(row => row.ContractName).HasMaxLength(2000).IsRequired();
            entity.Property(row => row.ContractPrice).HasPrecision(18, 2);
            entity.Property(row => row.ContractStatus).HasMaxLength(32).IsRequired();
            entity.Property(row => row.CustomerName).HasMaxLength(512);
            entity.Property(row => row.SupplierName).HasMaxLength(512);
            entity.Property(row => row.Ikz).HasMaxLength(64);
            entity.Property(row => row.EisRegistryNumber).HasMaxLength(32);
            entity.Property(row => row.ContractQuantity).HasPrecision(18, 4);

            entity.Property(row => row.Kind).HasMaxLength(32).IsRequired();
            entity.Property(row => row.BasisCode).HasMaxLength(64).IsRequired();
            entity.Property(row => row.Reason).HasMaxLength(2000).IsRequired();
            entity.Property(row => row.Description).HasMaxLength(4000);
            entity.Property(row => row.NewPrice).HasPrecision(18, 2);
            entity.Property(row => row.NewQuantity).HasPrecision(18, 4);
            entity.Property(row => row.QuantityUnit).HasMaxLength(64);
            entity.Property(row => row.NewSubject).HasMaxLength(4000);
            entity.Property(row => row.NewSupplier).HasMaxLength(512);
            entity.Property(row => row.NoticeNumber).HasMaxLength(64);
            entity.Property(row => row.CourtDecisionNumber).HasMaxLength(64);
            entity.Property(row => row.GovernmentActNumber).HasMaxLength(64);
            entity.Property(row => row.FormulaText).HasMaxLength(4000);
            entity.Property(row => row.UnitPrice).HasPrecision(18, 2);
            entity.Property(row => row.InitialMaxPrice).HasPrecision(18, 2);
            entity.Property(row => row.CreatedBy).HasMaxLength(256).IsRequired();

            entity.Property(row => row.Status).HasMaxLength(32).IsRequired();
            entity.Property(row => row.SignedBy).HasMaxLength(256);
            entity.Property(row => row.RegisteredBy).HasMaxLength(256);
            entity.Property(row => row.EisRegistrationNumber).HasMaxLength(64);
            entity.Property(row => row.RejectedReason).HasMaxLength(2000);
            entity.Property(row => row.CancelledReason).HasMaxLength(2000);

            // Ревизия записи — рабочий токен блокировки: её значение известно
            // документу с момента чтения, поэтому устаревшая правка получает
            // конфликт, а не перезаписывает чужую.
            entity.Property(row => row.Revision).IsConcurrencyToken();

            // xmin остаётся дополнительной защитой на уровне PostgreSQL: EF
            // сверяет его в WHERE, если строку изменили между чтением и записью.
            entity.Property(row => row.Xmin)
                .ValueGeneratedOnAddOrUpdate()
                .HasColumnName("xmin");
            entity.Property(row => row.Xmin).IsConcurrencyToken();

            entity.HasIndex(row => new { row.OrganizationId, row.ContractId });
            entity.HasIndex(row => row.ContractId);
            entity.HasIndex(row => row.Status);

            entity.HasMany(row => row.Documents)
                .WithOne()
                .HasForeignKey(document => document.ChangeId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(row => row.Events)
                .WithOne()
                .HasForeignKey(item => item.ChangeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ContractChangeDocumentRow>(entity =>
        {
            entity.ToTable("contract_change_documents", "contract");
            entity.HasKey(row => row.Id);

            entity.Property(row => row.Type).HasMaxLength(32).IsRequired();
            entity.Property(row => row.Title).HasMaxLength(512).IsRequired();
            entity.Property(row => row.Content).IsRequired();
            entity.Property(row => row.Sha256).HasMaxLength(64).IsRequired();
            entity.Property(row => row.CreatedBy).HasMaxLength(256).IsRequired();

            entity.HasIndex(row => new { row.ChangeId, row.OrderIndex });
        });

        builder.Entity<ContractChangeEventRow>(entity =>
        {
            entity.ToTable("contract_change_events", "contract");
            entity.HasKey(row => row.Id);

            entity.Property(row => row.From).HasMaxLength(32).IsRequired();
            entity.Property(row => row.To).HasMaxLength(32).IsRequired();
            entity.Property(row => row.Action).HasMaxLength(128).IsRequired();
            entity.Property(row => row.Comment).HasMaxLength(2000).IsRequired();
            entity.Property(row => row.Actor).HasMaxLength(256).IsRequired();

            entity.HasIndex(row => new { row.ChangeId, row.OrderIndex });
        });
    }

    /// <summary>
    /// Правила НПА: версия акта и правила по кодам ОКПД2. Активная версия одна
    /// на акт — тот же приём, что и у справочников: фильтрованный уникальный
    /// индекс не даёт двум прогонам агента включить две версии сразу.
    /// </summary>
    private static void ConfigureNpa(ModelBuilder builder)
    {
        builder.Entity<NpaDocumentRow>(entity =>
        {
            entity.ToTable("npa_documents", "ref");
            entity.HasKey(row => row.Id);

            entity.Property(row => row.Code).HasMaxLength(64).IsRequired();
            entity.Property(row => row.Version).HasMaxLength(128).IsRequired();
            entity.Property(row => row.EoNumber).HasMaxLength(32);
            entity.Property(row => row.Number).HasMaxLength(32);
            entity.Property(row => row.Title).HasMaxLength(1024).IsRequired();
            entity.Property(row => row.SourceUrl).HasMaxLength(2048);
            entity.Property(row => row.PdfSha256).HasMaxLength(64);
            entity.Property(row => row.OcrEngine).HasMaxLength(128);
            entity.Property(row => row.LastCheckStatus).HasMaxLength(24);
            entity.Property(row => row.LastCheckMessage).HasMaxLength(1024);
            entity.Property(row => row.UnresolvedJson).HasColumnType("jsonb");

            entity.HasIndex(row => new { row.Code, row.Version }).IsUnique();
            entity.HasIndex(row => row.Code)
                .IsUnique()
                .HasFilter("\"IsActive\" = TRUE");
        });

        builder.Entity<NpaRuleRow>(entity =>
        {
            entity.ToTable("npa_rules", "ref");
            entity.HasKey(row => row.Id);

            entity.Property(row => row.Okpd2Code).HasMaxLength(32).IsRequired();
            entity.Property(row => row.RuleType).HasMaxLength(16).IsRequired();
            entity.Property(row => row.ConditionText).HasMaxLength(2048);
            entity.Property(row => row.Quote).HasMaxLength(4096);
            entity.Property(row => row.InheritedFrom).HasMaxLength(32);
            entity.Property(row => row.Status).HasMaxLength(24).IsRequired();
            entity.Property(row => row.Note).HasMaxLength(1024);

            entity.HasIndex(row => new { row.DocumentId, row.Okpd2Code, row.IsInherited }).IsUnique();
            // Поиск действующего правила идёт по префиксу кода (26.20%).
            entity.HasIndex(row => new { row.DocumentId, row.Okpd2Code })
                .HasDatabaseName("ix_npa_rules_document_code");

            entity.HasOne(row => row.Document)
                .WithMany(document => document.Rules)
                .HasForeignKey(row => row.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    /// <summary>
    /// Исходные документы фронта хранятся jsonb: при переходе с json-server
    /// нельзя терять поля, которые пока не участвуют в проверке прав. Ключи
    /// (email, organizationKey, code) вынесены в колонки для поиска.
    /// </summary>
    private static void ConfigureAccess(ModelBuilder builder)
    {
        builder.Entity<AccessOrganizationRow>(entity =>
        {
            entity.ToTable("organizations", "access");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.ExternalKey).HasMaxLength(64).IsRequired();
            entity.Property(row => row.Inn).HasMaxLength(32);
            entity.Property(row => row.Data).HasColumnType("jsonb").IsRequired();
            entity.HasIndex(row => row.ExternalKey).IsUnique();
        });

        builder.Entity<AccessUserRow>(entity =>
        {
            entity.ToTable("users", "access");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.ExternalKey).HasMaxLength(64).IsRequired();
            entity.Property(row => row.Email).HasMaxLength(256).IsRequired();
            entity.Property(row => row.PasswordHash).HasMaxLength(256).IsRequired();
            entity.Property(row => row.OrganizationKey).HasMaxLength(64).IsRequired();
            entity.Property(row => row.EmployeeKey).HasMaxLength(64);
            entity.Property(row => row.Data).HasColumnType("jsonb").IsRequired();
            entity.HasIndex(row => row.ExternalKey).IsUnique();
            entity.HasIndex(row => row.Email).IsUnique();
            entity.HasIndex(row => row.OrganizationKey);
        });

        builder.Entity<AccessEmployeeRow>(entity =>
        {
            entity.ToTable("employees", "access");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.ExternalKey).HasMaxLength(64).IsRequired();
            entity.Property(row => row.OrganizationKey).HasMaxLength(64).IsRequired();
            entity.Property(row => row.UserKey).HasMaxLength(64);
            entity.Property(row => row.Data).HasColumnType("jsonb").IsRequired();
            entity.HasIndex(row => row.ExternalKey).IsUnique();
            entity.HasIndex(row => row.OrganizationKey);
            entity.HasIndex(row => row.UserKey);
        });

        builder.Entity<AccessContractServiceRow>(entity =>
        {
            entity.ToTable("contract_services", "access");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.ExternalKey).HasMaxLength(64).IsRequired();
            entity.Property(row => row.OrganizationKey).HasMaxLength(64).IsRequired();
            entity.Property(row => row.Data).HasColumnType("jsonb").IsRequired();
            entity.HasIndex(row => row.ExternalKey).IsUnique();
            entity.HasIndex(row => row.OrganizationKey).IsUnique();
        });

        builder.Entity<AccessFunctionRow>(entity =>
        {
            entity.ToTable("functions", "access");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Code).HasMaxLength(64).IsRequired();
            entity.Property(row => row.Data).HasColumnType("jsonb").IsRequired();
            entity.HasIndex(row => row.Code).IsUnique();
        });

        builder.Entity<AccessRequestRow>(entity =>
        {
            entity.ToTable("requests", "access");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.ExternalKey).HasMaxLength(64).IsRequired();
            entity.Property(row => row.OrganizationKey).HasMaxLength(64).IsRequired();
            entity.Property(row => row.EmployeeKey).HasMaxLength(64).IsRequired();
            entity.Property(row => row.Status).HasMaxLength(32).IsRequired();
            entity.Property(row => row.Data).HasColumnType("jsonb").IsRequired();
            entity.HasIndex(row => row.ExternalKey).IsUnique();
        });

        builder.Entity<AccessRoleRow>(entity =>
        {
            entity.ToTable("roles", "access");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Code).HasMaxLength(64).IsRequired();
            entity.Property(row => row.Name).HasMaxLength(256).IsRequired();
            entity.HasIndex(row => row.Code).IsUnique();
        });

        builder.Entity<AccessPermissionRow>(entity =>
        {
            entity.ToTable("permissions", "access");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Code).HasMaxLength(64).IsRequired();
            entity.Property(row => row.Name).HasMaxLength(256).IsRequired();
            entity.HasIndex(row => row.Code).IsUnique();
        });

        builder.Entity<AccessRolePermissionRow>(entity =>
        {
            entity.ToTable("role_permissions", "access");
            entity.HasKey(row => new { row.RoleCode, row.PermissionCode });
            entity.Property(row => row.RoleCode).HasMaxLength(64);
            entity.Property(row => row.PermissionCode).HasMaxLength(64);
        });

        builder.Entity<AccessUserRoleRow>(entity =>
        {
            entity.ToTable("user_roles", "access");
            entity.HasKey(row => new { row.UserKey, row.RoleCode });
            entity.Property(row => row.UserKey).HasMaxLength(64);
            entity.Property(row => row.RoleCode).HasMaxLength(64);
        });

        builder.Entity<AccessSessionRow>(entity =>
        {
            entity.ToTable("sessions", "access");
            entity.HasKey(row => row.Token);
            entity.Property(row => row.Token).HasMaxLength(64);
            entity.Property(row => row.UserKey).HasMaxLength(64).IsRequired();
            entity.HasIndex(row => row.UserKey);
            entity.HasIndex(row => row.ExpiresAt);
        });
    }
}
