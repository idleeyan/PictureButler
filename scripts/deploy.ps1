# PictureButler deploy script
# Usage: .\scripts\deploy.ps1 [-SkipStart] [-KeepBackups 3]
# Flow: version check -> deep clean -> publish -> backup old exe -> copy delivery -> sync doc -> start + health
param(
    [switch]$SkipStart,
    [int]$KeepBackups = 3,
    [string]$DeliveryDir = "D:\Tool\PictureButler"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$srcNative = Join-Path $root "src-native"

function Fail([string]$msg) {
    Write-Host "[ABORT] $msg" -ForegroundColor Red
    exit 1
}
function Ok([string]$msg) {
    Write-Host "[OK] $msg" -ForegroundColor Green
}

# ---- 0. local env (NuGet path1 null workaround) ----
if (-not $env:ProgramFiles) { $env:ProgramFiles = "C:\Program Files" }
if (-not ${env:ProgramFiles(x86)}) { ${env:ProgramFiles(x86)} = "C:\Program Files (x86)" }
if (-not $env:ProgramW6432) { $env:ProgramW6432 = "C:\Program Files" }
if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = "E:\NuGet\packages" }

# ---- 1. version sync (4 places must match) ----
Write-Host ""
Write-Host "=== 1. version check ===" -ForegroundColor Cyan
$csproj = Get-Content (Join-Path $srcNative "PictureButler.csproj") -Raw
if ($csproj -notmatch '<Version>([\d.]+)</Version>') { Fail "csproj: no <Version>" }
$ver = $Matches[1]
Ok "csproj version = $ver"

$http = Get-Content (Join-Path $srcNative "LocalHttpServer.cs") -Raw
$httpHits = [regex]::Matches($http, [regex]::Escape($ver)).Count
if ($httpHits -lt 2) {
    Fail "LocalHttpServer.cs has $httpHits hit(s) for '$ver' (need >=2: comment + value)"
}
Ok "LocalHttpServer.cs literals = $ver"

$srcDoc = Join-Path $srcNative "yuan-sheng-ban-shuo-ming.txt"
$srcDocCn = Join-Path $srcNative ([char]0x539F + [char]0x751F + [char]0x7248 + [char]0x8BF4 + [char]0x660E + ".txt")
if (Test-Path $srcDocCn) { $srcDoc = $srcDocCn }
$docLine = (Get-Content $srcDoc -TotalCount 1)
if ($docLine -notmatch [regex]::Escape($ver)) {
    Fail "source doc first line missing $ver : $docLine"
}
Ok "source doc = $ver"

$dstDocName = "PictureButler-" + [char]0x8BF4 + [char]0x660E + ".txt"
$dstDoc = Join-Path $DeliveryDir $dstDocName

# ---- 2. deep clean ----
Write-Host ""
Write-Host "=== 2. deep clean ===" -ForegroundColor Cyan
Push-Location $srcNative
try {
    & dotnet clean -c Release
    if ($LASTEXITCODE -ne 0) { Fail "dotnet clean failed exit=$LASTEXITCODE" }
    foreach ($d in @("obj", "bin\Release")) {
        $p = Join-Path $srcNative $d
        if (Test-Path $p) {
            try { Remove-Item $p -Recurse -Force -ErrorAction Stop; Ok "removed $d" }
            catch { Write-Host "[WARN] cannot remove $d : $($_.Exception.Message)" -ForegroundColor Yellow }
        }
    }
}
finally { Pop-Location }

# ---- 3. publish (exit-code guard) ----
Write-Host ""
Write-Host "=== 3. publish ===" -ForegroundColor Cyan
$outDir = Join-Path $srcNative "bin\publish-out"
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue }

Push-Location $srcNative
try {
    & dotnet publish -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        --output $outDir
    $pubExit = $LASTEXITCODE
}
finally { Pop-Location }

if ($pubExit -ne 0) {
    Fail "publish failed exit=$pubExit - never copy artifacts on failure"
}
Ok "publish ok exit=0"

$exe = Join-Path $outDir "PictureButler.exe"
if (-not (Test-Path $exe)) { Fail "missing artifact $exe" }
$exeInfo = Get-Item $exe
Ok ("artifact {0} bytes  {1}" -f $exeInfo.Length, $exeInfo.LastWriteTime)

# ---- 4. backup old delivery exe ----
Write-Host ""
Write-Host "=== 4. backup old exe ===" -ForegroundColor Cyan
$dstExe = Join-Path $DeliveryDir "PictureButler.exe"
if (Test-Path $dstExe) {
    $old = Get-Item $dstExe
    $bakVer = "unknown"
    try {
        $fv = $old.VersionInfo.FileVersion
        if ($fv) { $bakVer = ($fv -split '\s')[0] }
    } catch {}
    $bakName = "PictureButler.exe.$bakVer.bak"
    Copy-Item $dstExe (Join-Path $DeliveryDir $bakName) -Force
    Ok "backup -> $bakName"

    $baks = Get-ChildItem $DeliveryDir -Filter "PictureButler.exe.*.bak" | Sort-Object LastWriteTime -Descending
    if ($baks.Count -gt $KeepBackups) {
        $baks | Select-Object -Skip $KeepBackups | ForEach-Object {
            try { Remove-Item $_.FullName -Force; Ok ("removed old backup " + $_.Name) } catch {}
        }
    }
}
else {
    Write-Host "[INFO] no old exe, skip backup" -ForegroundColor DarkGray
}

# ---- 5. stop running instance and overwrite ----
Write-Host ""
Write-Host "=== 5. overwrite delivery ===" -ForegroundColor Cyan
Get-Process | Where-Object { $_.ProcessName -like 'PictureButler*' } | ForEach-Object {
    Write-Host ("stop pid " + $_.Id + " " + $_.ProcessName)
    try { Stop-Process -Id $_.Id -Force } catch { Write-Host ("[WARN] " + $_.Exception.Message) -ForegroundColor Yellow }
}
Start-Sleep -Milliseconds 800

Copy-Item $exe $dstExe -Force
$dstInfo = Get-Item $dstExe
if ($dstInfo.Length -ne $exeInfo.Length) {
    Fail ("size mismatch src=" + $exeInfo.Length + " dst=" + $dstInfo.Length)
}
Ok ("copied " + $dstExe + " (" + $dstInfo.Length + " bytes)")

Copy-Item $srcDoc $dstDoc -Force
Ok "synced delivery doc"

# ---- 6. start and verify health ----
if (-not $SkipStart) {
    Write-Host ""
    Write-Host "=== 6. start + health ===" -ForegroundColor Cyan
    Start-Process -FilePath $dstExe
    $ok = $false
    for ($i = 0; $i -lt 15; $i++) {
        Start-Sleep -Seconds 1
        try {
            $h = Invoke-RestMethod -Uri "http://127.0.0.1:8189/api/health" -TimeoutSec 2
            if ($h.version -eq $ver) {
                Ok ("health = " + ($h | ConvertTo-Json -Compress))
                $ok = $true
                break
            }
            else {
                Write-Host ("[WARN] health version " + $h.version + " != expected " + $ver) -ForegroundColor Yellow
            }
        }
        catch { }
    }
    if (-not $ok) { Fail ("health check failed (expect version=" + $ver + ")") }
}

Write-Host ""
Write-Host ("===== DEPLOYED " + $ver + " =====") -ForegroundColor Green
Write-Host ("delivery: " + $dstExe)
Write-Host ("backups:  " + (Join-Path $DeliveryDir "PictureButler.exe.*.bak") + " (keep " + $KeepBackups + ")")