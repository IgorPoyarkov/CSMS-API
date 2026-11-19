param(
    [string]$DbJson = "..\csms\db.json",
    [string]$ApiUrl = "http://localhost:5099",
    [string]$AdministratorEmail = ""
)

$ErrorActionPreference = "Stop"

# Импорт сидов фронта в контур доступа csms-api. Пароли из db.json попадают
# только по HTTP в работающий API и там сразу хешируются: в базу и в логи
# исходный пароль не пишется. Скрипт не меняет db.json.

if (-not (Test-Path -LiteralPath $DbJson)) {
    throw "Не найден db.json: $DbJson"
}

$db = Get-Content -LiteralPath $DbJson -Raw -Encoding UTF8 | ConvertFrom-Json

function ConvertTo-JsonText($value) {
    if ($null -eq $value) { return "{}" }
    return ($value | ConvertTo-Json -Compress -Depth 15)
}

function To-Str($value) {
    if ($null -eq $value) { return "" }
    return [string]$value
}

$employees = @($db.employees)
$organizations = @($db.organizations)
$users = @($db.users)
$services = @($db.contractServices)
$functions = @($db.contractFunctions)
$requests = @($db.accessRequests)

$organizationPayload = @(
    foreach ($item in $organizations) {
        @{
            id   = (To-Str $item.id)
            data = (ConvertTo-JsonText $item)
        }
    }
)

$employeePayload = @(
    foreach ($item in $employees) {
        @{
            id             = (To-Str $item.id)
            organizationId = (To-Str $item.organizationId)
            userId         = if ($null -eq $item.userId) { $null } else { (To-Str $item.userId) }
            data           = (ConvertTo-JsonText $item)
        }
    }
)

$userPayload = @(
    foreach ($item in $users) {
        $employeeId = ""
        if ($null -ne $item.employeeId -and (To-Str $item.employeeId) -ne "") {
            $employeeId = To-Str $item.employeeId
        }
        else {
            $linked = $employees | Where-Object { (To-Str $_.userId) -eq (To-Str $item.id) } | Select-Object -First 1
            if ($null -eq $linked) {
                $linked = $employees | Where-Object { $_.email -eq $item.email } | Select-Object -First 1
            }
            if ($null -ne $linked) { $employeeId = To-Str $linked.id }
        }

        $roles = @()
        if ($AdministratorEmail -and $item.email -eq $AdministratorEmail) {
            $roles += "administrator"
        }

        @{
            id             = (To-Str $item.id)
            email          = (To-Str $item.email)
            password       = (To-Str $item.password)
            organizationId = (To-Str $item.organizationId)
            employeeId     = $employeeId
            data           = (ConvertTo-JsonText $item)
            roleCodes      = $roles
        }
    }
)

$servicePayload = @(
    foreach ($item in $services) {
        @{
            id             = (To-Str $item.id)
            organizationId = (To-Str $item.organizationId)
            data           = (ConvertTo-JsonText $item)
        }
    }
)

$functionPayload = @(
    foreach ($item in $functions) {
        @{
            id   = (To-Str $item.id)
            code = (To-Str $item.code)
            data = (ConvertTo-JsonText $item)
        }
    }
)

$requestPayload = @(
    foreach ($item in $requests) {
        @{
            id             = (To-Str $item.id)
            organizationId = (To-Str $item.organizationId)
            employeeId     = (To-Str $item.employeeId)
            status         = (To-Str $item.status)
            data           = (ConvertTo-JsonText $item)
        }
    }
)

$payload = @{
    organizations    = $organizationPayload
    users            = $userPayload
    employees        = $employeePayload
    contractServices = $servicePayload
    functions        = $functionPayload
    requests         = $requestPayload
}

$json = $payload | ConvertTo-Json -Depth 20
$body = [Text.Encoding]::UTF8.GetBytes($json)
$url = $ApiUrl.TrimEnd("/") + "/api/access/import"

Write-Host "Импорт доступа: $($users.Count) пользователей, $($employees.Count) сотрудников, $($organizations.Count) организаций, $($services.Count) КС -> $url"
$response = Invoke-RestMethod -Method Post -Uri $url -ContentType "application/json; charset=utf-8" -Body $body
$response | ConvertTo-Json -Compress
