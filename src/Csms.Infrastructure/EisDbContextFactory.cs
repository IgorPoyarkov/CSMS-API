using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Csms.Infrastructure;

/// <summary>
/// Контекст для миграций (`dotnet ef ...`).
///
/// Нужен потому, что в приложении контекст регистрируется только при
/// `EIS_STORE=postgres`, а миграции надо уметь создавать и без поднятой базы.
/// Строка подключения — проектный кластер на 5433 (trust на localhost), без
/// пароля; в приложении строка приходит из конфигурации и сюда не попадает.
/// </summary>
public sealed class EisDbContextFactory : IDesignTimeDbContextFactory<EisDbContext>
{
    public EisDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<EisDbContext>()
            .UseNpgsql("Host=localhost;Port=5433;Database=csms_eis;Username=postgres")
            .Options;

        return new EisDbContext(options);
    }
}
