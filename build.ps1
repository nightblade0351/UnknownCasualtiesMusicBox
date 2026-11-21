param(
    [string]$GameDir = '',
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $GameDir) {
    # 自动在 D:\Application 下寻找 Casualties Unknown V1.0.2（避免脚本内出现中文路径导致编码问题）
    $hit = Get-ChildItem 'D:\Application' -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName 'Casualties Unknown V1.0.2' } |
        Where-Object { Test-Path (Join-Path $_ 'CasualtiesUnknown_Data\Managed') } |
        Select-Object -First 1
    if (-not $hit) { throw "game folder not found; pass -GameDir explicitly" }
    $GameDir = $hit
}
Write-Host ("GameDir = " + $GameDir)

$csc     = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$core    = Join-Path $GameDir 'BepInEx\core'
$managed = Join-Path $GameDir 'CasualtiesUnknown_Data\Managed'
$plugins = Join-Path $GameDir 'BepInEx\plugins'
$dist    = Join-Path $root 'dist'
$obj     = Join-Path $root 'obj'

if (-not (Test-Path $csc))     { throw "csc.exe not found: $csc" }
if (-not (Test-Path $managed)) { throw "game Managed folder not found: $managed" }

New-Item -ItemType Directory -Force -Path $dist | Out-Null
New-Item -ItemType Directory -Force -Path $obj  | Out-Null

$refs = @(
    (Join-Path $core    'BepInEx.dll'),
    (Join-Path $core    '0Harmony.dll'),
    (Join-Path $plugins 'CUCoreLib.dll'),
    (Join-Path $managed 'Assembly-CSharp.dll'),
    (Join-Path $managed 'UnityEngine.dll'),
    (Join-Path $managed 'UnityEngine.CoreModule.dll'),
    (Join-Path $managed 'UnityEngine.AudioModule.dll'),
    (Join-Path $managed 'UnityEngine.ScreenCaptureModule.dll'),
    (Join-Path $managed 'UnityEngine.UI.dll'),
    (Join-Path $managed 'UnityEngine.UIModule.dll'),
    (Join-Path $managed 'UnityEngine.IMGUIModule.dll'),
    (Join-Path $managed 'Unity.TextMeshPro.dll'),
    (Join-Path $managed 'netstandard.dll')
)

foreach ($r in $refs) {
    if (-not (Test-Path $r)) { throw "missing reference assembly: $r" }
}

$sources = Get-ChildItem -Path (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
if (-not $sources) { throw "no .cs files under src" }

$outDll = Join-Path $dist 'UnknownCasualtiesMusicBox.dll'
$argList = @(
    '/nologo', '/target:library', '/langversion:5', '/optimize+', '/debug-',
    '/codepage:65001',
    "/out:$outDll"
)
if ($SelfTest) { $argList += '/define:SELFTEST'; Write-Host 'SELFTEST build' -ForegroundColor Yellow }
foreach ($r in $refs) { $argList += "/reference:$r" }
$argList += $sources

Write-Host ("Compiling {0} source files -> {1}" -f $sources.Count, $outDll) -ForegroundColor Cyan
& $csc $argList
if ($LASTEXITCODE -ne 0) { throw ("compile failed (exit {0})" -f $LASTEXITCODE) }

Get-Item $outDll | Select-Object FullName, Length, LastWriteTime | Format-List
Write-Host "Build OK." -ForegroundColor Green