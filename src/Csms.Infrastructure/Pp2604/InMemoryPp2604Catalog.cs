using Csms.Application;

namespace Csms.Infrastructure.Pp2604;

/// <summary>
/// В memory-режиме нет схемы legal: каталог 2604 импортируется отдельным
/// инструментом в PostgreSQL. Эндпоинт честно вернёт 404, а не выдумает правила.
/// </summary>
public sealed class InMemoryPp2604Catalog : IPp2604Catalog
{
    public Task<Pp2604Mapping?> GetActiveAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<Pp2604Mapping?>(null);
}
