# Сквозная проверка API пакетов ЕИС: создание -> подпись -> отправка -> ФЛК.
#
# Запуск (сервис должен быть уже поднят):
#   powershell -NoProfile -File scripts/smoke.ps1
#   powershell -NoProfile -File scripts/smoke.ps1 -BaseUrl http://localhost:5000
#
# Скрипт ничего не меняет в данных, кроме создания двух пакетов в памяти
# процесса: после перезапуска сервиса они исчезнут.

param([string]$BaseUrl = "http://localhost:5099")

$ErrorActionPreference = "Stop"

# Консоль Windows по умолчанию не в UTF-8: без этого русский текст в выводе
# превращается в мусор, хотя сам файл читается правильно
$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$script:step = 0

function Step($text) {
    $script:step++
    Write-Host "$($script:step). $text"
}

function Post-Json([string]$path, $body) {
    Invoke-RestMethod -Method Post -Uri "$BaseUrl$path" `
        -ContentType "application/json; charset=utf-8" `
        -Body ($body | ConvertTo-Json -Depth 6)
}

function Assert-Equal($actual, $expected, [string]$what) {
    if ($actual -ne $expected) {
        throw "$what : ожидалось '$expected', получено '$actual'"
    }
}

# Проверка, что действие отклонено правилами домена (409 + текст для человека)
function Expect-Conflict([string]$method, [string]$path, $body, [string]$what) {
    try {
        Invoke-RestMethod -Method $method -Uri "$BaseUrl$path" `
            -ContentType "application/json; charset=utf-8" `
            -Body ($body | ConvertTo-Json -Depth 6) | Out-Null
    }
    catch {
        $response = $_.Exception.Response
        if ($null -eq $response) { throw "$what : запрос упал без HTTP-ответа ($_)" }
        if ([int]$response.StatusCode -ne 409) { throw "$what : ожидался 409, получен $([int]$response.StatusCode)" }

        return $true
    }

    throw "$what : действие НЕ отклонено, хотя должно было"
}

$health = Invoke-RestMethod -Uri "$BaseUrl/health"
Assert-Equal $health.status "ok" "health"

# --- Сценарий приёма ---------------------------------------------------------

Step "Создаю пакет"
$package = Post-Json "/api/eis/packages" @{
    documentType       = "SmpAttraction"
    contractEisNumber  = "0373100000123000001"
    ikz                = "2612345678901234567890"
}
Assert-Equal $package.status "Draft" "статус нового пакета"
Assert-Equal $package.isPlaceholderFormat $true "признак чернового формата"
Write-Host "   пакет $($package.id), формат $($package.payloadSchemaVersion)"

Step "Отправка без подписи отклонена"
Expect-Conflict "Post" "/api/eis/packages/$($package.id)/send" @{} "отправка неподписанного" | Out-Null
Write-Host "   409, как и должно быть"

Step "Получаю содержимое для подписания"
$task = Post-Json "/api/eis/packages/$($package.id)/signing" @{}
Assert-Equal $task.payloadSha256 $package.payloadSha256 "хеш подписываемого"
Write-Host "   SHA-256 $($task.payloadSha256.Substring(0, 16))..."

Step "Подпись под другим содержимым отклонена"
Expect-Conflict "Put" "/api/eis/packages/$($package.id)/signature" @{
    signatureBase64     = "MIIC0TCCAbkCAQAwCgYIKoUDBwEBAgEwHjEcMBoGA1UEAxMT"
    signerThumbprint    = "0123456789abcdef"
    signedContentSha256 = ("0" * 64)
} "подпись под чужим содержимым" | Out-Null
Write-Host "   409: пакет изменился после подписания — именно этот случай обычно всплывает в ЕИС"

Step "Прикладываю подпись с верным хешем"
$signed = Invoke-RestMethod -Method Put -Uri "$BaseUrl/api/eis/packages/$($package.id)/signature" `
    -ContentType "application/json; charset=utf-8" `
    -Body (@{
        signatureBase64     = "MIIC0TCCAbkCAQAwCgYIKoUDBwEBAgEwHjEcMBoGA1UEAxMT"
        signerThumbprint    = "0123456789abcdef"
        signedContentSha256 = $task.payloadSha256
    } | ConvertTo-Json -Depth 6)
Assert-Equal $signed.status "Signed" "статус после подписи"

Step "Отправляю в ЕИС (шлюз-заглушка)"
$sent = Post-Json "/api/eis/packages/$($package.id)/send" @{}
Assert-Equal $sent.status "SentToEis" "статус после отправки"
Write-Host "   eisGuid $($sent.eisGuid)"

Step "Опрашиваю результат ФЛК"
foreach ($attempt in 1..3) {
    $result = Post-Json "/api/eis/packages/$($package.id)/refresh" @{}
    Write-Host "   попытка $attempt -> $($result.status)"
}
Assert-Equal $result.status "AcceptedInLk" "итоговый статус"
Write-Host "   принято, ошибок ФЛК: $($result.flkErrors.Count)"

# --- Сценарий отказа ФЛК -----------------------------------------------------

Step "Пакет, который ФЛК отклонит (номер оканчивается на 0000)"
$bad = Post-Json "/api/eis/packages" @{
    documentType      = "SmpAttraction"
    contractEisNumber = "0373100000123000000"
}
Post-Json "/api/eis/packages/$($bad.id)/signing" @{} | Out-Null
Invoke-RestMethod -Method Put -Uri "$BaseUrl/api/eis/packages/$($bad.id)/signature" `
    -ContentType "application/json; charset=utf-8" `
    -Body (@{ signatureBase64 = "MIIC0TCCAbkCAQAwCgYIKoUDBwEBAgEwHjEcMBoGA1UEAxMT"; signerThumbprint = "0123" } | ConvertTo-Json) | Out-Null
Post-Json "/api/eis/packages/$($bad.id)/send" @{} | Out-Null
$rejected = Post-Json "/api/eis/packages/$($bad.id)/refresh" @{}
Assert-Equal $rejected.status "RejectedByFlk" "статус после отказа ФЛК"
foreach ($flk in $rejected.flkErrors) {
    Write-Host "   $($flk.code) [$($flk.field)] $($flk.message)"
}

Step "Возвращаю отклонённый пакет в работу"
$revised = Post-Json "/api/eis/packages/$($bad.id)/revise" @{}
Assert-Equal $revised.status "Draft" "статус после возврата в работу"
Assert-Equal $revised.flkErrors.Count 0 "ошибки после возврата"
Write-Host "   подпись снята, ошибки очищены — можно исправлять и отправлять заново"

Step "Транспортная ошибка не роняет запрос (номер оканчивается на 9999)"
$down = Post-Json "/api/eis/packages" @{
    documentType      = "SmpAttraction"
    contractEisNumber = "0373100000123009999"
}
Post-Json "/api/eis/packages/$($down.id)/signing" @{} | Out-Null
Invoke-RestMethod -Method Put -Uri "$BaseUrl/api/eis/packages/$($down.id)/signature" `
    -ContentType "application/json; charset=utf-8" `
    -Body (@{ signatureBase64 = "MIIC0TCCAbkCAQAwCgYIKoUDBwEBAgEwHjEcMBoGA1UEAxMT"; signerThumbprint = "0123" } | ConvertTo-Json) | Out-Null
$failed = Post-Json "/api/eis/packages/$($down.id)/send" @{}
Assert-Equal $failed.status "SendFailed" "статус при недоступном шлюзе"
Write-Host "   $($failed.lastError)"

Step "Контракт для фронта"
$openapi = Invoke-WebRequest -Uri "$BaseUrl/openapi/v1.json"
Write-Host "   /openapi/v1.json — $($openapi.Content.Length) символов"

Write-Host ""
Write-Host "OK: все проверки прошли"
