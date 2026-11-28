<#
    把 <音乐盒>\_extra\ 里的 waiting_map_* 曲目并回主目录，
    接在现有最大编号后面（例如已有 waiting_map_3 → 变成 waiting_map_4、_5…），
    然后删掉空的 _extra 目录。

    用法：
        powershell -File merge_extra_back.ps1 -DryRun
        powershell -File merge_extra_back.ps1
#>
param(
    [string]$Root = 'D:\Application\游戏\Casualties Unknown V1.0.2\BepInEx\plugins\UnknownCasualtiesMusicBox\data',
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Root)) { throw "data 目录不存在: $Root" }

$moved = 0
foreach ($boxDir in Get-ChildItem -LiteralPath $Root -Directory | Sort-Object Name) {
    $extra = Join-Path $boxDir.FullName '_extra'
    if (-not (Test-Path -LiteralPath $extra)) { continue }

    # 现有最大编号
    $max = 1
    Get-ChildItem -LiteralPath $boxDir.FullName -File | ForEach-Object {
        if ($_.BaseName -match '^waiting_map_(\d+)$') {
            $n = [int]$Matches[1]
            if ($n -gt $max) { $max = $n }
        }
    }

    # _extra 里只处理 waiting_map_*，其他文件原样保留
    $targets = Get-ChildItem -LiteralPath $extra -File | Where-Object { $_.BaseName -like 'waiting_map*' } |
        Sort-Object { $_.BaseName }
    if (-not $targets) { continue }

    Write-Host ("### " + $boxDir.Name + "（当前最大 waiting_map_" + $max + "）")
    foreach ($f in $targets) {
        $max++
        $dst = Join-Path $boxDir.FullName ('waiting_map_' + $max + $f.Extension)
        if (Test-Path -LiteralPath $dst) {
            Write-Host ("    [跳过] 目标已存在: " + (Split-Path $dst -Leaf)) -ForegroundColor DarkYellow
            continue
        }
        Write-Host ("    " + $f.Name + "  ->  " + (Split-Path $dst -Leaf))
        if (-not $DryRun) { Move-Item -LiteralPath $f.FullName -Destination $dst }
        $moved++
    }

    if (-not $DryRun) {
        $left = Get-ChildItem -LiteralPath $extra -Force
        if ($left.Count -eq 0) { Remove-Item -LiteralPath $extra -Force }
        else { Write-Host ("    [保留] _extra 里还有 " + $left.Count + " 个非 waiting_map 文件") -ForegroundColor DarkYellow }
    }
}

Write-Host ""
Write-Host ("并回 " + $moved + " 个文件。")
if ($DryRun) { Write-Host "（DryRun：未改动任何文件）" }
