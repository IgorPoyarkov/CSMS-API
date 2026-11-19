# Генерация документации по установленным XSD ЕИС:
#   JSON-каталог для обозревателя схем и TypeScript-объявления для фронтенда.
#
# Запуск из любого места:
#   powershell -NoProfile -File scripts/generate-schema-docs.ps1
#   powershell -NoProfile -File scripts/generate-schema-docs.ps1 -FrontendRoot D:\...\csms
#
# Ничего не скачивает: берёт уже установленную папку schemas/<версия>,
# которую обновляет EisSchemaDownloader.

param(
    [string]$FrontendRoot,
    [string]$Version,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $FrontendRoot) {
    $FrontendRoot = Join-Path (Join-Path $repoRoot "..") "csms"
}
if (-not (Test-Path $FrontendRoot)) {
    throw "Папка фронтенда не найдена: $FrontendRoot"
}

$schemasRoot = Join-Path $repoRoot "schemas"
if (-not $Version) {
    $Version = Get-ChildItem $schemasRoot -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName ".manifest.json") } |
        Sort-Object Name -Descending |
        Select-Object -First 1 -ExpandProperty Name
}
if (-not $Version) {
    throw "В $schemasRoot нет установленных версий схем с .manifest.json"
}

$xsdDir = Join-Path $schemasRoot $Version
$manifest = Get-Content (Join-Path $xsdDir ".manifest.json") -Raw | ConvertFrom-Json
$sourceId = [string]$manifest.SourceId
$sha256 = [string]$manifest.Sha256

$catalogPath = Join-Path $FrontendRoot "public\eis-schemas\catalog.json"
$typesPath = Join-Path $FrontendRoot "src\services\Eis\generated\eisSchemas.generated.d.ts"

$generator = Join-Path $repoRoot "tools\EisDocGenerator\EisDocGenerator.csproj"
if (-not $SkipBuild) {
    dotnet build $generator --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Не удалось собрать генератор документации" }
}

dotnet run --project $generator --no-build -- `
    $xsdDir $catalogPath $typesPath $Version $sourceId $sha256
if ($LASTEXITCODE -ne 0) { throw "Генератор документации завершился с ошибкой" }

Write-Host "Каталог: $catalogPath"
Write-Host "Типы:    $typesPath"
