namespace Csms.Infrastructure.Documents;

/// <summary>
/// Версии промптов. Любое изменение текста инструкции должно менять версию,
/// иначе нельзя объяснить, почему один и тот же файл дал разные наборы функций.
/// </summary>
public static class DocumentFunctionPromptVersions
{
    public const string Map = "map-v1";
    public const string Reduce = "deterministic-v1";
}
