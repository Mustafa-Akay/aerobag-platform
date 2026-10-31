<#
.SYNOPSIS
    AeroBag yerel demosunu tek komutla açar ya da kapatır.

.DESCRIPTION
    Açarken sırayla: ön kontrol (araçlar, Docker motoru, ayrılabilir bellek, portlar) → Postgres ve ML servisi
    (docker compose) → derleme → şema göçü → API → arayüz → simülatör. API, arayüz ve simülatör kendi
    pencerelerinde açılır; günlükleri orada görünür. Açılan pencereler .demo\pids.txt dosyasına yazılır ve
    -Stop ile alt süreçleriyle birlikte kapatılır (arkada süreç kalmasın diye).

    Windows PowerShell 5.1 ve PowerShell 7 ile çalışır. En kolayı: scripts\demo  (demo.cmd bu betiği çağırır).

.EXAMPLE
    scripts\demo                       # tam kapsam, hız 5, tohum 42
.EXAMPLE
    scripts\demo -Coverage typical     # bant ağzı ve banttan alma okuyucusu olmayan kurulum
.EXAMPLE
    scripts\demo -Stop                 # pencereleri ve Docker servislerini kapatır (veri korunur)
#>
[CmdletBinding()]
param(
    # Demoyu kapatır.
    [switch]$Stop,
    # -Stop ile: Docker servislerini açık bırakır.
    [switch]$KeepDocker,
    # Okuma kapsamı profili; API ve simülatör aynı profille açılır.
    [ValidateSet('full', 'typical', 'minimal')]
    [string]$Coverage = 'full',
    # Simülatör hızı (sanal saniye / gerçek saniye).
    [double]$Speed = 5,
    # Simülatör tohumu; aynı tohum aynı uçuşları ve PNR'ları üretir.
    [int]$Seed = 42,
    # Simülatörü başlatmaz (yalnızca API ve arayüz).
    [switch]$NoSimulator,
    # Derlemeyi atlar (kod değişmediyse hızlı açılış).
    [switch]$NoBuild,
    # Tarayıcıyı açmaz.
    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Root = Split-Path -Parent $PSScriptRoot
$StateDir = Join-Path $Root '.demo'
$PidFile = Join-Path $StateDir 'pids.txt'
$UiDir = Join-Path $Root 'src\aerobag-ui'
$ApiUrl = 'http://localhost:5080'
$UiUrl = 'http://localhost:5173'
$MlUrl = 'http://127.0.0.1:8000'   # localhost değil: Windows önce IPv6'yı dener, ~2 sn kaybeder
$Ports = @(5080, 5173)

function Write-Step([string]$Message) { Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Write-Ok([string]$Message) { Write-Host "    $Message" -ForegroundColor Green }
function Write-Note([string]$Message) { Write-Host "    UYARI: $Message" -ForegroundColor Yellow }
function Stop-WithError([string]$Message) {
    Write-Host "`nHATA: $Message" -ForegroundColor Red
    exit 1
}

# Yerel bir komutu çıktısız çalıştırır ve başarılı olup olmadığını döndürür. Windows PowerShell 5.1'de yerel
# komutun hata akışını yönlendirmek, ErrorActionPreference = Stop iken betiği durdurur; bu yüzden geçici olarak gevşetilir.
function Test-Native([scriptblock]$Command) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Command *> $null
        return ($LASTEXITCODE -eq 0)
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

# Yerel komutu çıktısı görünür şekilde çalıştırır; başarısızsa betiği durdurur.
function Invoke-Checked([string]$What, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) {
        Stop-WithError "$What başarısız oldu (çıkış kodu $LASTEXITCODE). Yukarıdaki çıktıya bakın."
    }
}

function Get-PortOwner([int]$Port) {
    $listener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $listener) { return $null }
    $process = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue
    $name = if ($process) { $process.ProcessName } else { 'bilinmiyor' }
    return [pscustomobject]@{ Id = $listener.OwningProcess; Name = $name }
}

function Test-Prerequisites {
    Write-Step 'Ön kontrol'

    foreach ($tool in @('docker', 'dotnet', 'npm')) {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
            Stop-WithError "'$tool' bulunamadı. Kurulu olduğundan ve PATH'te olduğundan emin olun."
        }
    }
    Write-Ok 'docker, dotnet ve npm bulundu.'

    if (-not (Test-Native { docker info })) {
        Stop-WithError "Docker motoru çalışmıyor. Docker Desktop'ı açın, motor hazır olunca tekrar deneyin."
    }
    Write-Ok 'Docker motoru çalışıyor.'

    # Ayrılabilir bellek (Görev Yöneticisi > Bellek > "Tamamlanan" sınırına kalan). Dolunca Node
    # "Fatal process out of memory: Zone" hatasıyla çöker, Docker da takılabilir.
    $os = Get-CimInstance Win32_OperatingSystem
    $freeGb = [math]::Round($os.FreeVirtualMemory / 1MB, 1)   # KB -> GB
    if ($freeGb -lt 1.5) {
        Stop-WithError "Windows'un ayırabileceği bellek çok az ($freeGb GB). Açık programları kapatın ya da bilgisayarı yeniden başlatın."
    }
    elseif ($freeGb -lt 3) {
        Write-Note "Ayrılabilir bellek az ($freeGb GB). Açılışta sorun çıkarsa programları kapatıp tekrar deneyin."
    }
    else {
        Write-Ok "Ayrılabilir bellek: $freeGb GB."
    }

    $wslConfig = Join-Path $env:USERPROFILE '.wslconfig'
    $hasLimit = (Test-Path $wslConfig) -and (Select-String -Path $wslConfig -Pattern '^\s*memory\s*=' -Quiet)
    if (-not $hasLimit) {
        Write-Note "WSL için bellek sınırı yok ($wslConfig). Docker RAM'in yarısına kadar büyüyebilir; [wsl2] altına 'memory=4GB' önerilir."
    }

    foreach ($port in $Ports) {
        $owner = Get-PortOwner $port
        if ($owner) {
            Stop-WithError "Port $port kullanımda ($($owner.Name), PID $($owner.Id)). Önceki demo açık kalmış olabilir: scripts\demo -Stop"
        }
    }
    Write-Ok "Portlar boş ($($Ports -join ', '))."
}

# Komutu yeni bir PowerShell penceresinde çalıştırır ve pencereyi .demo\pids.txt'ye yazar. Komut -EncodedCommand ile
# geçirilir; böylece tırnak ve Türkçe karakterler komut satırında bozulmaz.
function Start-DemoWindow([string]$Title, [string]$Command, [string]$WorkingDirectory) {
    $script = "`$Host.UI.RawUI.WindowTitle = 'AeroBag - $Title'; $Command"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
    $process = Start-Process -FilePath 'powershell.exe' -WorkingDirectory $WorkingDirectory -PassThru `
        -ArgumentList @('-NoExit', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded)
    Add-Content -Path $PidFile -Value "$($process.Id) $Title"
    Write-Ok "'AeroBag - $Title' penceresi açıldı (PID $($process.Id))."
}

function Wait-Http([string]$Url, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 3
            if ($response.StatusCode -eq 200) { return $true }
        }
        catch {
            # henüz hazır değil
        }
        Start-Sleep -Seconds 1
    }
    return $false
}

function Stop-Demo {
    Write-Step 'Demo kapatılıyor'

    if (Test-Path $PidFile) {
        foreach ($line in Get-Content $PidFile) {
            $parts = $line -split ' ', 2
            $id = [int]$parts[0]
            $title = if ($parts.Count -gt 1) { $parts[1] } else { 'pencere' }
            if (Get-Process -Id $id -ErrorAction SilentlyContinue) {
                # /T: pencerenin altındaki dotnet ve node süreçleri de kapanır.
                $null = Test-Native { taskkill.exe /PID $id /T /F }
                Write-Ok "$title kapatıldı (PID $id)."
            }
        }
        Remove-Item $PidFile
    }
    else {
        Write-Ok 'Kayıtlı demo penceresi yok.'
    }

    # Pencere elle kapatılıp altındaki süreç kaldıysa bildirilir; başka bir projenin süreci olabileceği için kapatılmaz.
    foreach ($port in $Ports) {
        $owner = Get-PortOwner $port
        if ($owner) {
            Write-Note "Port $port hâlâ kullanımda ($($owner.Name), PID $($owner.Id)). Bizimse: Stop-Process -Id $($owner.Id)"
        }
    }

    if ($KeepDocker) {
        Write-Ok 'Docker servisleri açık bırakıldı.'
    }
    elseif (Test-Native { docker info }) {
        Invoke-Checked 'docker compose stop' { docker compose stop }
        Write-Ok 'Docker servisleri durduruldu (veri korunur; silmek için: docker compose down -v).'
    }
}

Push-Location $Root
try {
    if ($Stop) {
        Stop-Demo
        return
    }

    Test-Prerequisites
    New-Item -ItemType Directory -Force -Path $StateDir | Out-Null
    if (Test-Path $PidFile) { Remove-Item $PidFile }

    Write-Step 'Postgres ve ML servisi (docker compose, ilk seferde ML imajı birkaç dakika sürebilir)'
    Invoke-Checked 'docker compose up' { docker compose up -d --build --wait }
    Write-Ok 'Postgres hazır, ML servisi çalışıyor.'

    if (-not $NoBuild) {
        Write-Step 'Derleme'
        # Projeler sırayla derlenir; aynı anda iki "dotnet run" ortak projeleri derlemeye çalışırsa dosya kilidi çıkar.
        foreach ($project in @('AeroBag.Migrator', 'AeroBag.Api', 'AeroBag.Simulator')) {
            Invoke-Checked "$project derlemesi" { dotnet build "src\AeroBag.Core\$project" -nologo -v q }
        }
        Write-Ok 'Derleme tamam.'
    }

    Write-Step 'Şema göçü'
    Invoke-Checked 'Göç aracı' { dotnet run --no-build --project src\AeroBag.Core\AeroBag.Migrator }

    Write-Step "API (okuma kapsamı: $Coverage)"
    Start-DemoWindow 'API' "`$env:Coverage__Profile = '$Coverage'; dotnet run --no-build --project src\AeroBag.Core\AeroBag.Api --urls $ApiUrl" $Root
    if (-not (Wait-Http "$ApiUrl/health" 90)) {
        Stop-WithError "API 90 sn içinde açılmadı. 'AeroBag - API' penceresindeki hatayı okuyun; kapatmak için: scripts\demo -Stop"
    }
    Write-Ok "API hazır: $ApiUrl"

    if (Wait-Http "$MlUrl/health" 20) {
        Write-Ok "ML servisi yanıt veriyor: $MlUrl"
    }
    else {
        Write-Note 'ML servisi yanıt vermiyor; tahminler ampirik yönteme düşer (docker compose logs ml).'
    }

    Write-Step 'Arayüz'
    if (-not (Test-Path (Join-Path $UiDir 'node_modules'))) {
        Push-Location $UiDir
        try { Invoke-Checked 'npm install' { npm install } } finally { Pop-Location }
    }
    Start-DemoWindow 'Arayüz' 'npm run dev' $UiDir
    if (-not (Wait-Http $UiUrl 60)) {
        Stop-WithError "Arayüz 60 sn içinde açılmadı. 'AeroBag - Arayüz' penceresindeki hatayı okuyun; kapatmak için: scripts\demo -Stop"
    }
    Write-Ok "Arayüz hazır: $UiUrl"

    if (-not $NoSimulator) {
        Write-Step "Simülatör (hız $Speed, tohum $Seed)"
        # Ondalık ayırıcı Türkçe ayarda virgül olur; simülatör nokta bekler.
        $speedText = $Speed.ToString([Globalization.CultureInfo]::InvariantCulture)
        Start-DemoWindow 'Simülatör' "dotnet run --no-build --project src\AeroBag.Core\AeroBag.Simulator -- --mode live --speed $speedText --seed $Seed --coverage $Coverage" $Root
    }

    if (-not $NoBrowser) {
        Start-Process $UiUrl
    }

    Write-Step 'Demo açık'
    Write-Host @"
    Yolcu sayfası : $UiUrl/#/
                    PNR ve soyadlar 'AeroBag - Simülatör' penceresinin başında yazar.
    Personel      : $UiUrl/#/ops
                    ops / ops-demo-2026, security / security-demo-2026 (yalnızca yerel demo)
    Kapatmak için : scripts\demo -Stop
"@
}
finally {
    Pop-Location
}
