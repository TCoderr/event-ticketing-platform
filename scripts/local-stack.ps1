<#
.SYNOPSIS
  Loca yerel stack'i (PostgreSQL + API/SPA, HTTPS). Windows PowerShell 5.1 ve PowerShell 7 ile çalışır.

.DESCRIPTION
  init    Durum dizinini ve secret dosyalarını (yoksa) üretir, imajı derler, stack'i başlatır,
          migration'ı uygular ve demo verisini ekler. Tekrar çalıştırmak güvenlidir (idempotent).
  up      Var olan stack'i başlatır (veri korunur).
  down    Stack'i durdurur. Volume ve secret dosyalarını SİLMEZ.
  status  Servis durumunu ve hazır olma (ready) sonucunu gösterir.
  logs    Servis loglarını gösterir.
  seed    Demo verisini yeniden uygular (idempotent).
  reset   YIKICI: bu stack'in volume'larını ve durum dizinini siler. -ConfirmProject ile açık onay
          ve sahiplik doğrulaması (manifest + volume etiketi) ister.

.EXAMPLE
  .\scripts\local-stack.ps1 init
  .\scripts\local-stack.ps1 down
  .\scripts\local-stack.ps1 reset -ConfirmProject loca
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('init', 'up', 'down', 'status', 'logs', 'seed', 'reset')]
    [string]$Command,

    [ValidatePattern('^[a-z][a-z0-9-]{1,40}$')]
    [string]$ProjectName = 'loca',

    [ValidateRange(1024, 65535)]
    [int]$HttpsPort = 8643,

    [string]$ConfirmProject = '',

    [switch]$NoBuild,

    [string]$Service = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$StateDir = Join-Path $RepoRoot (Join-Path '.local' $ProjectName)
$ManifestPath = Join-Path $StateDir 'stack.manifest.json'
$SecretsDir = Join-Path $StateDir 'secrets'
$UsersDir = Join-Path $StateDir 'users'
$CertsDir = Join-Path $StateDir 'certs'

$DemoUsers = @(
    'platform-admin@loca.demo',
    'organizer-kumru@loca.demo',
    'organizer-marti@loca.demo',
    'gate-kumru@loca.demo',
    'customer-ayse@loca.demo',
    'customer-mehmet@loca.demo'
)
$SecretFiles = @('db-superuser.password.txt', 'db-owner.password.txt', 'db-app.password.txt', 'psp-api-key.secret.txt', 'psp-webhook.secret.txt')

function Write-Step([string]$Message) { Write-Host "==> $Message" }

function New-RandomSecret([int]$Bytes = 24) {
    $buffer = New-Object byte[] $Bytes
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($buffer) } finally { $rng.Dispose() }
    return ([System.BitConverter]::ToString($buffer)).Replace('-', '').ToLowerInvariant()
}

function New-DemoPassword {
    # Parola politikasını (>=10, harf + rakam) karşılayan, yalnız yerel demo için rastgele değer.
    return 'Demo-' + (New-RandomSecret 9) + '-7a'
}

function Write-SecretFile([string]$Path, [string]$Value) {
    # BOM'suz, satır sonu olmadan yazılır; dosya var ise asla üzerine yazılmaz.
    if (Test-Path -LiteralPath $Path) { return $false }
    [System.IO.File]::WriteAllText($Path, $Value, (New-Object System.Text.UTF8Encoding($false)))
    return $true
}

function Read-Manifest {
    if (-not (Test-Path -LiteralPath $ManifestPath)) { return $null }
    return Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
}

function Invoke-NativeQuiet([scriptblock]$Block) {
    # Windows PowerShell 5.1'de yönlendirilen stderr, ErrorActionPreference=Stop iken sonlandırıcı hataya
    # dönüşür. Sessiz çağrılar bu yüzden yerel olarak 'Continue' ile yapılır; sonuç $LASTEXITCODE'dadır.
    $ErrorActionPreference = 'Continue'
    return (& $Block 2>$null)
}

function Assert-Docker {
    $null = Invoke-NativeQuiet { docker info --format '{{.ServerVersion}}' }
    if ($LASTEXITCODE -ne 0) { throw 'Docker Engine çalışmıyor veya erişilemiyor. Docker Desktop''ı başlatıp yeniden deneyin.' }
}

function Initialize-State {
    $manifest = Read-Manifest
    if ($null -eq $manifest) {
        foreach ($dir in @($StateDir, $SecretsDir, $UsersDir, $CertsDir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        $manifest = [pscustomobject]@{
            projectName = $ProjectName
            httpsPort   = $HttpsPort
            stackId     = [guid]::NewGuid().ToString('N')
            createdAt   = (Get-Date).ToUniversalTime().ToString('o')
        }
        [System.IO.File]::WriteAllText($ManifestPath, ($manifest | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
        Write-Step "Durum dizini oluşturuldu: $StateDir"
    }
    else {
        if ($manifest.projectName -ne $ProjectName) { throw "Manifest başka bir projeye ait: $($manifest.projectName)" }
        foreach ($dir in @($SecretsDir, $UsersDir, $CertsDir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    }

    $created = 0
    foreach ($file in $SecretFiles) {
        if (Write-SecretFile (Join-Path $SecretsDir $file) (New-RandomSecret 24)) { $created++ }
    }
    foreach ($user in $DemoUsers) {
        if (Write-SecretFile (Join-Path $UsersDir "$user.password.txt") (New-DemoPassword)) { $created++ }
    }
    if ($created -gt 0) { Write-Step "$created yeni secret/parola dosyası üretildi (değerler ekrana yazılmaz)." }
    return $manifest
}

function Invoke-Compose {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$ComposeArgs)
    $manifest = Read-Manifest
    if ($null -eq $manifest) { throw "Bu proje için durum dizini yok ($StateDir). Önce 'init' çalıştırın." }
    $env:LOCA_STATE_DIR = ($StateDir -replace '\\', '/')
    $env:LOCA_HTTPS_PORT = [string]$manifest.httpsPort
    $env:LOCA_STACK_ID = [string]$manifest.stackId
    # docker compose ilerleme çıktısını stderr'e yazar; çağıran stderr'i yönlendirmişse Windows PowerShell 5.1
    # bunu hata kaydına çevirir. Başarı yalnız çıkış koduyla değerlendirilir.
    $ErrorActionPreference = 'Continue'
    & docker compose --project-name $ProjectName --project-directory $RepoRoot --file (Join-Path $RepoRoot 'compose.yaml') @ComposeArgs
    if ($LASTEXITCODE -ne 0) { throw "docker compose $($ComposeArgs -join ' ') başarısız oldu (çıkış kodu $LASTEXITCODE)." }
}

function Export-Certificate {
    $target = Join-Path $CertsDir 'loca-local.crt'
    $composeFile = Join-Path $RepoRoot 'compose.yaml'
    $null = Invoke-NativeQuiet { docker compose --project-name $ProjectName --project-directory $RepoRoot --file $composeFile cp 'api:/var/lib/loca/keys/loca-local.crt' $target }
    if ($LASTEXITCODE -eq 0) { Write-Step "Sunucu sertifikası dışa aktarıldı: $target" }
}

function Show-Summary($manifest) {
    Write-Host ''
    Write-Host "Loca çalışıyor:  https://localhost:$($manifest.httpsPort)"
    Write-Host '  Sertifika self-signed''dır; tarayıcı ilk açılışta uyarı gösterir.'
    Write-Host "  Demo kullanıcı parolaları: $UsersDir\<e-posta>.password.txt"
    foreach ($user in $DemoUsers) { Write-Host "    $user" }
}

Assert-Docker

switch ($Command) {
    'init' {
        $manifest = Initialize-State
        if (-not $NoBuild) {
            Write-Step 'İmaj derleniyor'
            Invoke-Compose build migrate
        }
        Write-Step 'Stack başlatılıyor (migration dahil)'
        Invoke-Compose up --detach --wait --wait-timeout 180
        Write-Step 'Demo verisi uygulanıyor (idempotent)'
        Invoke-Compose run --rm --no-deps api seed-demo
        Export-Certificate
        Show-Summary $manifest
    }
    'up' {
        Invoke-Compose up --detach --wait --wait-timeout 180
        Export-Certificate
        Show-Summary (Read-Manifest)
    }
    'down' {
        # Volume'lar ve secret dosyaları korunur.
        Invoke-Compose down
        Write-Step 'Stack durduruldu. Veri korunuyor; yeniden açmak için: up'
    }
    'status' {
        Invoke-Compose ps --all
        $manifest = Read-Manifest
        $cert = Join-Path $CertsDir 'loca-local.crt'
        if (Test-Path -LiteralPath $cert) {
            $code = & curl.exe --silent --output NUL --write-out '%{http_code}' --cacert $cert --max-time 5 "https://localhost:$($manifest.httpsPort)/health/ready"
            Write-Host "health/ready: HTTP $code"
        }
    }
    'logs' {
        if ($Service) { Invoke-Compose logs --tail 200 $Service } else { Invoke-Compose logs --tail 200 }
    }
    'seed' {
        Invoke-Compose run --rm --no-deps api seed-demo
    }
    'reset' {
        $manifest = Read-Manifest
        if ($null -eq $manifest) { throw "Silinecek stack bulunamadı: $StateDir" }
        if ($ConfirmProject -ne $ProjectName) {
            throw "reset yıkıcıdır. Onaylamak için: -ConfirmProject $ProjectName"
        }
        # Sahiplik doğrulaması: volume etiketi bu manifestin stackId'si ile eşleşmeli.
        foreach ($volume in @("${ProjectName}_pgdata", "${ProjectName}_keys")) {
            $labelsJson = Invoke-NativeQuiet { docker volume inspect $volume --format '{{json .Labels}}' }
            if ($LASTEXITCODE -ne 0) { continue } # Volume yok: silinecek bir şey de yok.
            $labels = $labelsJson | ConvertFrom-Json
            $stackLabel = $null
            if ($null -ne $labels -and ($labels.PSObject.Properties.Name -contains 'loca.stack-id')) { $stackLabel = $labels.'loca.stack-id' }
            if ($stackLabel -ne $manifest.stackId) {
                throw "Volume '$volume' bu stack'e ait görünmüyor (etiket uyuşmuyor). Hiçbir şey silinmedi."
            }
        }
        Write-Step "Stack '$ProjectName' ve volume'ları siliniyor"
        Invoke-Compose down --volumes --remove-orphans
        $resolved = (Resolve-Path -LiteralPath $StateDir).Path
        $expectedParent = (Resolve-Path -LiteralPath (Join-Path $RepoRoot '.local')).Path
        if (-not $resolved.StartsWith($expectedParent + [System.IO.Path]::DirectorySeparatorChar)) { throw 'Durum dizini beklenen konumda değil; silinmedi.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
        Write-Step 'Silindi.'
    }
}
