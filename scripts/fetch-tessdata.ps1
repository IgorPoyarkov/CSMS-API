#region Лицензия и назначение
# Скачивает официальный языковой файл Tesseract для русского языка.
# Бинарник не хранится в git: он большой и легко восстанавливается.
# Источник: https://github.com/tesseract-ocr/tessdata (Apache-2.0).
#endregion

param(
    [string]$Language = 'rus',
    [string]$Url = "https://github.com/tesseract-ocr/tessdata/raw/main/$Language.traineddata",
    [string]$ExpectedSha256 = '681BE2C2BEAD1BC7BD235DF88C44E8E60AE73AE866840C0AD4E3B4C247BD37C2',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tessdata = Join-Path $root 'ocr/tessdata'
$target = Join-Path $tessdata "$Language.traineddata"

New-Item -ItemType Directory -Path (Join-Path $tessdata 'configs') -Force | Out-Null

# Конфиг TSV нужен агенту разметки НПА: он читает координаты слов из OCR.
$tsvConfig = Join-Path $tessdata 'configs/tsv'
if (-not (Test-Path $tsvConfig)) {
    Set-Content -Path $tsvConfig -Value 'tessedit_create_tsv 1' -Encoding ascii
}

if ((Test-Path $target) -and -not $Force) {
    Write-Host "Файл уже есть: $target"
} else {
    Write-Host "Скачивание $Url ..."
    Invoke-WebRequest -Uri $Url -OutFile $target -UseBasicParsing
}

$hash = (Get-FileHash -Path $target -Algorithm SHA256).Hash
if ($hash -ine $ExpectedSha256) {
    Write-Warning "SHA-256 файла $target отличается от ожидаемого. Проверьте источник: $hash"
} else {
    Write-Host "SHA-256 совпадает: $hash"
}
Write-Host "Tessdata готов: $tessdata"
