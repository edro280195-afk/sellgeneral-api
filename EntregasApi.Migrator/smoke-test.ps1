# Smoke test post-migracion - Ejecutar DESPUES de apuntar la app al nuevo destino.
# Valida que los 3 endpoints publicos (links vivos de clientas/repartidores/tandas) y la
# autenticacion + endpoints autenticados funcionan contra la base multi-tenant nueva.
#
# Uso:
#   .\smoke-test.ps1
#   .\smoke-test.ps1 -ApiBaseUrl "https://api.regibazar.com"
#
# Variables de entorno (opcional, para la parte autenticada):
#   $env:SMOKE_OWNER_EMAIL    = email del Owner (ej. yazmin_vara@hotmail.com)
#   $env:SMOKE_OWNER_PASSWORD = password real del Owner (lo que Regi usa para entrar al panel)
#
# Si no se pasan email/password, los tests autenticados se reportan como SKIPPED.

[CmdletBinding()]
param(
    [string]$ApiBaseUrl = "http://localhost:5050",
    [string]$OrderToken = "f11f432173ea2c9922ae4166c69c8b5fce4133f58c2900642c903ac7f58baaf7",
    [string]$DriverToken = "280cbd56d1733f74bd83c7220e960948a3717bc75bcfa4e114f867f7b3fa038c",
    [string]$TandaToken = "a055b9ec1e2149598f10eff9c7376f3407533309b1ba4db88a4f7623b172538e",
    [string]$OwnerEmail = $env:SMOKE_OWNER_EMAIL,
    [string]$OwnerPassword = $env:SMOKE_OWNER_PASSWORD
)

$ErrorActionPreference = "Stop"
$script:results = @()

function Test-Step {
    param(
        [string]$Name,
        [string]$Expected,
        [scriptblock]$Check
    )
    try {
        $detail = & $Check
        $script:results += [pscustomobject]@{
            Step = $Name
            Status = "PASS"
            Detail = $detail
        }
        Write-Host "  [PASS] $Name" -ForegroundColor Green
        if ($detail) { Write-Host "         $detail" -ForegroundColor DarkGray }
    } catch {
        $script:results += [pscustomobject]@{
            Step = $Name
            Status = "FAIL"
            Detail = $_.Exception.Message
        }
        Write-Host "  [FAIL] $Name" -ForegroundColor Red
        Write-Host "         $($_.Exception.Message)" -ForegroundColor DarkGray
    }
}

function Get-Json {
    param([string]$Url)
    $resp = Invoke-WebRequest -Uri $Url -Method GET -TimeoutSec 15 -UseBasicParsing
    if ($resp.StatusCode -ne 200) { throw "HTTP $($resp.StatusCode)" }
    return ($resp.Content | ConvertFrom-Json)
}

Write-Host ""
Write-Host "==============================================================" -ForegroundColor Cyan
Write-Host "  SMOKE TEST - Migracion Regi Bazar (post-cutover)" -ForegroundColor Cyan
Write-Host "  API: $ApiBaseUrl" -ForegroundColor Cyan
Write-Host "==============================================================" -ForegroundColor Cyan
Write-Host ""

# ---------------------------------------------------------------------------
# 1. Endpoint publico: pedido por accessToken
# ---------------------------------------------------------------------------
Test-Step "1. GET /api/pedido/{token} (publico, sin auth)" "200 + Order.Id + Items" {
    $json = Get-Json "$ApiBaseUrl/api/pedido/$OrderToken"
    if (-not $json.clientId) { throw "Respuesta sin clientId" }
    $itemsCount = ($json.items | Measure-Object).Count
    if ($itemsCount -lt 1) { throw "Sin items" }
    "ClientId=$($json.clientId) Items=$itemsCount Total=$($json.total)"
}

# ---------------------------------------------------------------------------
# 2. Endpoint publico: ruta de repartidor por driverToken
# ---------------------------------------------------------------------------
Test-Step "2. GET /api/driver/{token} (publico, sin auth)" "200 + Route + Deliveries" {
    $json = Get-Json "$ApiBaseUrl/api/driver/$DriverToken"
    if (-not $json.id) { throw "Respuesta sin id" }
    $deliveries = ($json.deliveries | Measure-Object).Count
    "RouteId=$($json.id) Status=$($json.status) Deliveries=$deliveries"
}

# ---------------------------------------------------------------------------
# 3. Endpoint publico: tanda por access_token
# ---------------------------------------------------------------------------
Test-Step "3. GET /api/public-tanda/{token} (publico, sin auth)" "200 + Tanda.Id + Participants" {
    $json = Get-Json "$ApiBaseUrl/api/public-tanda/$TandaToken"
    if (-not $json.id) { throw "Respuesta sin id" }
    "TandaId=$($json.id) Status=$($json.status)"
}

# ---------------------------------------------------------------------------
# 4. Token invalido -> 404 (no se rompa la validacion)
# ---------------------------------------------------------------------------
Test-Step "4. Token invalido -> 404 (no 500)" "404" {
    $code = 0
    try {
        $resp = Invoke-WebRequest -Uri "$ApiBaseUrl/api/pedido/token-que-no-existe-12345" -Method GET -TimeoutSec 10 -UseBasicParsing
        $code = [int]$resp.StatusCode
    } catch {
        $ex = $_.Exception
        # HttpRequestException / WebException: el status code esta en $ex.Response.StatusCode
        if ($ex.Response) {
            $code = [int]$ex.Response.StatusCode
        }
    }
    if ($code -ne 404) {
        throw "Devolvio $code, esperaba 404"
    }
    "404 OK (token invalido)"
}

# ---------------------------------------------------------------------------
# 5. Login (autenticado) - si email/password proporcionados
# ---------------------------------------------------------------------------
$jwt = $null
$businessId = 1
if ($OwnerEmail -and $OwnerPassword) {
    Test-Step "5. POST /api/auth/login (autenticado)" "200 + JWT" {
        $body = @{ email = $OwnerEmail; password = $OwnerPassword } | ConvertTo-Json
        $resp = Invoke-WebRequest -Uri "$ApiBaseUrl/api/auth/login" -Method POST -Body $body -ContentType "application/json" -TimeoutSec 15 -UseBasicParsing
        if ($resp.StatusCode -ne 200) { throw "HTTP $($resp.StatusCode)" }
        $json = $resp.Content | ConvertFrom-Json
        if (-not $json.token) { throw "Respuesta sin token" }
        $script:jwt = $json.token
        "Token len=$($json.token.Length) (${OwnerEmail})"
    }
} else {
    Write-Host "  [SKIP] 5. POST /api/auth/login (no se proporciono email/password via env vars SMOKE_OWNER_*)" -ForegroundColor Yellow
    $script:results += [pscustomobject]@{ Step = "5. POST /api/auth/login"; Status = "SKIP"; Detail = "Set SMOKE_OWNER_EMAIL y SMOKE_OWNER_PASSWORD para correrlo" }
}

# ---------------------------------------------------------------------------
# 6. GET /api/business/me (autenticado) - verifica que la app lee el Business correcto
# ---------------------------------------------------------------------------
if ($jwt) {
    $headers = @{ "Authorization" = "Bearer $jwt"; "X-Business-Id" = "1" }
    Test-Step "6. GET /api/business/me (autenticado, X-Business-Id=1)" "200 + BusinessId=1" {
        $resp = Invoke-WebRequest -Uri "$ApiBaseUrl/api/business/me" -Method GET -Headers $headers -TimeoutSec 15 -UseBasicParsing
        if ($resp.StatusCode -ne 200) { throw "HTTP $($resp.StatusCode)" }
        $json = $resp.Content | ConvertFrom-Json
        if ($json.id -ne 1) { throw "BusinessId=$($json.id), esperaba 1" }
        if ($json.name -ne "Regi Bazar") { throw "Name='$($json.name)', esperaba 'Regi Bazar'" }
        "Name='$($json.name)' Slug='$($json.slug)' Plan='$($json.subscription.planTier)'"
    }

    Test-Step "7. GET /api/orders/paged (autenticado, lista de pedidos)" "200 + total > 700" {
        $resp = Invoke-WebRequest -Uri "$ApiBaseUrl/api/orders/paged?page=1&pageSize=20" -Method GET -Headers $headers -TimeoutSec 15 -UseBasicParsing
        if ($resp.StatusCode -ne 200) { throw "HTTP $($resp.StatusCode)" }
        $json = $resp.Content | ConvertFrom-Json
        if ($json.total -lt 700) { throw "Total=$($json.total), esperaba >= 700" }
        "Page=$($json.page) Total=$($json.total) Items=$($json.items.Count)"
    }
} else {
    Write-Host "  [SKIP] 6-7. Endpoints autenticados (no hay JWT del paso 5)" -ForegroundColor Yellow
    $script:results += [pscustomobject]@{ Step = "6-7. Endpoints autenticados"; Status = "SKIP"; Detail = "Sin JWT" }
}

# ---------------------------------------------------------------------------
# Resumen
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "==============================================================" -ForegroundColor Cyan
Write-Host "  RESUMEN" -ForegroundColor Cyan
Write-Host "==============================================================" -ForegroundColor Cyan

$passCount = ($results | Where-Object Status -eq "PASS").Count
$failCount = ($results | Where-Object Status -eq "FAIL").Count
$skipCount = ($results | Where-Object Status -eq "SKIP").Count

$results | Format-Table -AutoSize Step, Status, Detail

Write-Host ""
Write-Host "Total: $passCount PASS, $failCount FAIL, $skipCount SKIP" -ForegroundColor $(if ($failCount -gt 0) { "Red" } else { "Green" })

if ($failCount -gt 0) {
    exit 1
}
exit 0
