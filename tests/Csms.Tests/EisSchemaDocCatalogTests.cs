using Csms.Api;
using EisDocGenerator;

namespace Csms.Tests;

/// <summary>
/// Проверки того, что бэкенд правильно объясняет фронту, что именно изменилось.
/// </summary>
public sealed class EisSchemaDocCatalogTests
{
    [Fact]
    public void Diff_Различает_добавленные_удалённые_и_изменённые_типы()
    {
        var before = Catalog(Definition("TypeA", "string"), Definition("TypeB"));
        var after = Catalog(Definition("TypeA", "number"), Definition("TypeC"));

        var change = EisSchemaUpdateEvent.Diff(before, after, "old-hash", "new-hash");

        Assert.Equal("old-hash", change.FromHash);
        Assert.Equal("new-hash", change.ToHash);
        Assert.Equal(1, change.TypesAdded);
        Assert.Equal(["TypeC"], change.AddedTypes);
        Assert.Equal(1, change.TypesRemoved);
        Assert.Equal(["TypeB"], change.RemovedTypes);
        Assert.Equal(1, change.TypesModified);
        Assert.Equal(["TypeA"], change.ModifiedTypes);
    }

    [Fact]
    public async Task Broker_Доставляет_одно_событие_всем_подписчикам()
    {
        var broker = new EisSchemaEventBroker();
        await using var first = broker.Subscribe();
        await using var second = broker.Subscribe();
        var change = EisSchemaUpdateEvent.Diff(
            null, Catalog(Definition("TypeA")), "unknown", "new-hash");

        broker.Publish(change);

        Assert.Same(change, await first.Reader.ReadAsync());
        Assert.Same(change, await second.Reader.ReadAsync());
    }

    private static SchemaCatalog Catalog(params SchemaDefinition[] types) => new(
        "17.0",
        "44668",
        "archive-sha",
        DateTimeOffset.UtcNow,
        1,
        [],
        [.. types]);

    private static SchemaDefinition Definition(string name, string fieldType = "string") => new(
        name,
        "urn:test",
        "complex",
        null,
        null,
        [new SchemaField("field", fieldType, false, false, null)],
        []);
}
