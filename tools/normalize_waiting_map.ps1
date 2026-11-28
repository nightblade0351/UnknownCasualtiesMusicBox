<#
    把 data 下各音乐盒里「双数字」的加载地图音乐重整成新命名规则：

        waiting_map_1           第一层（固定一首）
        waiting_map_2 … _12     第二层及以后的随机池（编号连续、不跳号）

    处理的输入：
        waiting_map_1_1 / _1_2 / _1_3 …    → 依次变成 waiting_map_1 / _2 / _3
        waiting_map_2_1 / _2_2 / _2_3 …    → 接着往后编号
        waiting_map_3 / _4 …（已是新规则） 保持相对顺序

    **不会创建子目录，也不会删除任何文件**：所有曲目都留在音乐盒根目录，
    只是重新编号成 waiting_map_1 … waiting_map_N。

    用法：
        powershell -File normalize_waiting_map.ps1 -DryRun     # 只看计划
        powershell -File normalize_waiting_map.ps1             # 实际执行
#>
param(
    [string]$Root = 'D:\Application\游戏\Casualties Unknown V1.0.2\BepInEx\plugins\UnknownCasualtiesMusicBox\data',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Root)) { throw "data 目录不存在: $Root" }

$totalMoved = 0

foreach ($boxDir in Get-ChildItem -LiteralPath $Root -Directory | Sort-Object Name) {
    $files = Get-ChildItem -LiteralPath $boxDir.FullName -File
    if ($files.Count -eq 0) { continue }

    # 基础名 -> 文件列表（同一基础名的 mp3/wav/ogg 一起处理）
    $byBase = @{}
    foreach ($f in $files) {
        $b = [IO.Path]::GetFileNameWithoutExtension($f.Name)
        if (-not $byBase.ContainsKey($b)) { $byBase[$b] = New-Object System.Collections.Generic.List[object] }
        $byBase[$b].Add($f)
    }

    # 收集所有 waiting_map* 曲目，分成「第一层组」和「后续组」
    $l1 = @()
    $rest = @()
    foreach ($b in $byBase.Keys) {
        if ($b -match '^waiting_map_1$') {
            $l1 += [pscustomobject]@{ Base = $b; Seg = 0; Files = $byBase[$b] }
        }
        elseif ($b -match '^waiting_map_1_(\d+)$') {
            $l1 += [pscustomobject]@{ Base = $b; Seg = [int]$Matches[1]; Files = $byBase[$b] }
        }
        elseif ($b -match '^waiting_map_(\d+)$') {
            $rest += [pscustomobject]@{ Base = $b; N = [int]$Matches[1]; Seg = 0; Files = $byBase[$b] }
        }
        elseif ($b -match '^waiting_map_(\d+)_(\d+)$') {
            $rest += [pscustomobject]@{ Base = $b; N = [int]$Matches[1]; Seg = [int]$Matches[2]; Files = $byBase[$b] }
        }
    }
    if (($l1.Count + $rest.Count) -eq 0) { continue }

    # 目标顺序：第一层组（无后缀优先，再按段号）→ 后续组（按原编号，再按段号）
    $ordered = @()
    $ordered += ($l1 | Sort-Object Seg)
    $ordered += ($rest | Sort-Object N, Seg)

    $slot = 1
    $plan = @()
    foreach ($t in $ordered) {
        $dst = 'waiting_map_' + $slot
        if ($t.Base -ne $dst) {
            $plan += [pscustomobject]@{ Base = $t.Base; Dst = $dst; Files = $t.Files }
        }
        $slot++
    }
    if ($plan.Count -eq 0) { continue }

    Write-Host ("### " + $boxDir.Name)
    foreach ($p in $plan) {
        Write-Host ("    " + $p.Base + "  ->  " + $p.Dst)
    }

    if (-not $DryRun) {
        # 两阶段改名：先全部改成临时名，避免相互覆盖
        $staged = @()
        $i = 0
        foreach ($p in $plan) {
            foreach ($f in $p.Files) {
                $i++
                $tmp = Join-Path $boxDir.FullName ('__ucmb_tmp_' + $i + $f.Extension)
                Move-Item -LiteralPath $f.FullName -Destination $tmp
                $staged += [pscustomobject]@{ Tmp = $tmp; Dest = (Join-Path $boxDir.FullName ($p.Dst + $f.Extension)) }
            }
        }
        foreach ($s in $staged) {
            if (Test-Path -LiteralPath $s.Dest) {
                Write-Host ("    [跳过] 目标已存在: " + (Split-Path $s.Dest -Leaf)) -ForegroundColor DarkYellow
                continue
            }
            Move-Item -LiteralPath $s.Tmp -Destination $s.Dest
            $script:totalMoved++
        }
    }
    else {
        $totalMoved += ($plan | ForEach-Object { $_.Files.Count } | Measure-Object -Sum).Sum
    }
}

Write-Host ""
Write-Host ("重新编号 " + $totalMoved + " 个文件。")
if ($DryRun) { Write-Host "（DryRun：未改动任何文件）" }
