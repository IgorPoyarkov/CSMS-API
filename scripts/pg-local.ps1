# Локальный кластер PostgreSQL для шлюза ЕИС.
# Это НЕ системная установка: отдельный кластер под текущим пользователем,
# база csms_eis, порт 5433, аутентификация trust только на localhost.
# Запуск: powershell -NoProfile -ExecutionPolicy Bypass -File scripts/pg-local.ps1
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = "Stop"

$bin = "C:\Program Files\PostgreSQL\17\bin"
$data = Join-Path $env:USERPROFILE "csms-eis\pgdata"
$log = Join-Path $env:USERPROFILE "csms-eis\pg.log"
$socket = Join-Path $data "socket"

if (-not (Test-Path (Join-Path $data "postgresql.conf"))) {
    Write-Host "Кластер не инициализирован: $data" -ForegroundColor Yellow
    Write-Host "Инициализируйте его заново (initdb -D ... -U postgres -A trust) и создайте базу csms_eis."
    exit 1
}

$status = & "$bin\pg_ctl.exe" -D $data status
if ($status -match "server is running") {
    Write-Host "Кластер уже запущен (порт 5433)."
} else {
    New-Item -ItemType Directory -Force -Path $socket | Out-Null
    & "$bin\pg_ctl.exe" -D $data -l $log -o "-p 5433 -k `"$socket`"" -w start
}

$ok = Test-NetConnection -ComputerName localhost -Port 5433 -InformationLevel Quiet -WarningAction SilentlyContinue
Write-Host "Порт 5433: $ok"
Write-Host "Подключение: Host=localhost;Port=5433;Database=csms_eis;Username=postgres"
