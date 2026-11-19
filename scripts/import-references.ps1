# Импорт справочников НСИ прямо из официального сайта ЕИС.
# Сервис должен быть поднят (для постоянства данных — с EIS_STORE=postgres).
#
#   powershell -NoProfile -File scripts/import-references.ps1
#   powershell -NoProfile -File scripts/import-references.ps1 -DryRun
#   powershell -NoProfile -File scripts/import-references.ps1 -Dataset nsiOKPD2

param(
    [string]$BaseUrl = "http://localhost:5099",
    [string[]]$Dataset = @("nsiOKPD2"),
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

foreach ($code in $Dataset) {
    $url = "$BaseUrl/api/references/import/eis/$code"
    if ($DryRun) { $url += "?dryRun=true" }

    try {
        $result = Invoke-RestMethod -Method Post -Uri $url -TimeoutSec 1800
        Write-Host "$code : $($result.version) : $($result.itemCount) записей"
        if ($result.dataset) { Write-Host "   активная версия: $($result.toVersion)" }
        if ($result.sourceSha256) { Write-Host "   SHA-256: $($result.sourceSha256)" }
    }
    catch {
        if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 400) {
            Write-Host "$code : уже импортирован (400), пропускаю"
            continue
        }

        throw
    }
}
