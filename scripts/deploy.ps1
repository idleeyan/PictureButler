# PictureButler deploy script
# Usage: .\scripts\deploy.ps1 [-SkipStart] [-KeepBackups 3]
# Flow: version check -> deep clean -> publish -> backup old exe -> copy delivery + extension -> sync doc -> start + health
param(
    [switch]$SkipStart,
    [int]$KeepBackups = 3,
    [string]$DeliveryDir = "D:\Tool\PictureButler"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$srcNative = Join-Path $root "src-native"
$srcExt = Join-Path $root "extension"

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
$csprojPath = Join-Path $srcNative "PictureButler.csproj"
if (-not (Test-Path $csprojPath)) { Fail "missing $csprojPath" }
$csproj = Get-Content $csprojPath -Raw
if ($csproj -notmatch '<Version>([\d.]+)</Version>') { Fail "csproj: no <Version>" }
$ver = $Matches[1]
Ok "csproj version = $ver"

$httpPath = Join-Path $srcNative "LocalHttpServer.cs"
$http = Get-Content $httpPath -Raw
$httpHits = [regex]::Matches($http, [regex]::Escape($ver)).Count
if ($httpHits -lt 2) {
    Fail "LocalHttpServer.cs has $httpHits hit(s) for '$ver' (need >=2: comment + value)"
}
Ok "LocalHttpServer.cs literals = $ver"

$srcDoc = $null
foreach ($name in @(
        ([char]0x539F + [char]0x751F + [char]0x7248 + [char]0x8BF4 + [char]0x660E + ".txt"),
        "yuan-sheng-ban-shuo-ming.txt")) {
    $p = Join-Path $srcNative $name
    if (Test-Path $p) { $srcDoc = $p; break }
}
if (-not $srcDoc) { Fail "source doc not found in $srcNative" }
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
$stillRunning = $false
Get-Process | Where-Object { $_.ProcessName -like 'PictureButler*' } | ForEach-Object {
    Write-Host ("stop pid " + $_.Id + " " + $_.ProcessName)
    try { Stop-Process -Id $_.Id -Force -ErrorAction Stop }
    catch {
        Write-Host ("[WARN] Stop-Process failed: " + $_.Exception.Message) -ForegroundColor Yellow
        $script:stillRunning = $true
    }
}
Start-Sleep -Milliseconds 800

# 若进程仍占着 exe（僵尸/拒绝访问），走「改名腾位」：旧文件挪开，新文件写入原名
if ($stillRunning -or (Test-Path $dstExe)) {
    try {
        $probe = [System.IO.File]::Open($dstExe, 'Open', 'Read', 'None')
        $probe.Close()
    }
    catch {
        Write-Host "[WARN] exe still locked, rename-away fallback" -ForegroundColor Yellow
        $park = Join-Path $DeliveryDir ("PictureButler.exe.locked-" + (Get-Date -Format "yyyyMMddHHmmss") + ".old")
        try { Move-Item $dstExe $park -Force; Ok ("parked locked exe -> " + (Split-Path $park -Leaf)) }
        catch { Fail ("cannot overwrite locked exe and rename failed: " + $_.Exception.Message) }
    }
}

Copy-Item $exe $dstExe -Force
$dstInfo = Get-Item $dstExe
if ($dstInfo.Length -ne $exeInfo.Length) {
    Fail ("size mismatch src=" + $exeInfo.Length + " dst=" + $dstInfo.Length)
}
Ok ("copied " + $dstExe + " (" + $dstInfo.Length + " bytes)")

Copy-Item $srcDoc $dstDoc -Force
Ok "synced delivery doc"

# ---- 5b. sync extension (icons + js/html/manifest) ----
# 历史事故：deploy 只拷 exe，交付目录扩展图标一直是旧版，与程序本体不同源。
$dstExt = Join-Path $DeliveryDir "extension"
if (Test-Path $srcExt) {
    if (-not (Test-Path $dstExt)) { New-Item -ItemType Directory -Path $dstExt | Out-Null }
    # 排除 _backup_original* —— 那是仓库里的历史图标备份，不需要进交付
    $robolog = Join-Path $env:TEMP "pb_deploy_ext.log"
    & robocopy $srcExt $dstExt /MIR /NFL /NDL /NJH /NJS /NP /XD "_backup_original" "_backup_original_real" /LOG:$robolog | Out-Null
    $rc = $LASTEXITCODE
    # robocopy: 0-7 success, >=8 failure
    if ($rc -ge 8) { Fail "robocopy extension failed rc=$rc (see $robolog)" }
    Ok ("synced extension -> " + $dstExt + "  (robocopy rc=" + $rc + ")")

    # 哈希复核图标（真·同源证明）
    foreach ($icon in @("icon16.png", "icon48.png", "icon128.png")) {
        $s = Join-Path (Join-Path $srcExt "icons") $icon
        $d = Join-Path (Join-Path $dstExt "icons") $icon
        if ((Test-Path $s) -and (Test-Path $d)) {
            $hs = (Get-FileHash $s).Hash
            $hd = (Get-FileHash $d).Hash
            if ($hs -ne $hd) { Fail ("icon hash mismatch: " + $icon) }
        }
    }
    Ok "extension icons hash-match"
}
else {
    Write-Host "[WARN] no source extension folder, skip" -ForegroundColor Yellow
}

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
Write-Host ("extension: " + (Join-Path $DeliveryDir "extension"))
Write-Host ("backups:  " + (Join-Path $DeliveryDir "PictureButler.exe.*.bak") + " (keep " + $KeepBackups + ")")