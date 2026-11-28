<#
    生成发布用的压缩包（结构：BepInEx\plugins\UnknownCasualtiesMusicBox\...）。
      - <ver>_noaudio.zip        程序本体 + 安装说明 + 命名规则 + data/README
      - <ver>_musicpacks_partN.zip  音乐包分卷（沿用上一版的分组）
    用法：powershell -File pack_release.ps1 -Version 1.3.3
#>
param(
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$Root   = 'D:\PCUser\git\UnknownCasualtiesMusicBox',
    [string]$GameData = 'D:\Application\游戏\Casualties Unknown V1.0.2\BepInEx\plugins\UnknownCasualtiesMusicBox\data',
    [int]$Parts = 6
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$dist = Join-Path $Root 'dist'
$obj  = Join-Path $Root 'obj'
$dll  = Join-Path $dist 'UnknownCasualtiesMusicBox.dll'
$notes = Join-Path $Root 'docs\音乐盒命名规则.md'
if (-not (Test-Path $dll)) { throw "缺少 dist\UnknownCasualtiesMusicBox.dll（先跑 build.ps1）" }

# ---------- 通用：按 Windows 风格条目名打包 ----------
function New-Zip([string]$zipPath, [System.Collections.Generic.List[object]]$items) {
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    $fs = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew)
    $zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($it in $items) {
            $entry = $zip.CreateEntry($it.Name, [System.IO.Compression.CompressionLevel]::Optimal)
            $es = $entry.Open()
            $bytes = [IO.File]::ReadAllBytes($it.Path)
            $es.Write($bytes, 0, $bytes.Length)
            $es.Dispose()
        }
    }
    finally { $zip.Dispose(); $fs.Dispose() }
}

$base = 'BepInEx\plugins\UnknownCasualtiesMusicBox'

# ---------- 1. 无音频包 ----------
$installTxt = Join-Path $obj 'install_133.txt'
Set-Content -LiteralPath $installTxt -Encoding UTF8 -Value @"
未知伤亡音乐盒 v$Version（不含音频）
=================================

【安装】
把 BepInEx 文件夹拖到游戏根目录（与 winhttp.dll、doorstop_config.ini 同级）覆盖。

【必须自备音频】
本包不附带任何音乐盒音频。请按下面结构放入自己的音乐：

  $base\data\<音乐盒名>\
      hall.mp3 / choose_part.mp3 / waiting_map_1.mp3 / waiting_map_2.mp3 /
      in_map.mp3 / attack_hurt.mp3 / articulos.mp3 / suspend.mp3 /
      die.mp3 / final_result_1.mp3 / final_result_2.mp3 / final_result_3.mp3

命名规则（v1.3.3 起）：
  · 加载地图音乐用槽位编号：waiting_map_1 = 第一层；
    waiting_map_2 … waiting_map_12 = 第二层及以后的随机池，每次加载随机抽一首。
    不要再写 waiting_map_1_1 这种「两个数字」的名字。
  · 其他事件要多首随机时用 _2 / _3，例如 final_result_3_1、attack_hurt_2。
支持 mp3 / wav / ogg（不支持 flac）。详见同目录下的「音乐盒命名规则.md」。

【使用】
游戏内：设置 → 音频 → 音乐盒 → 点开弹出选择面板 → 选中即生效。
下方 11 个滑条分别是各事件音量。

【依赖】
BepInEx 5.4.23.5+、CUCoreLib（游戏已装）。

【声明】
本内容（包含代码本体）含 AI 生成。详见仓库 AI_DISCLOSURE.md。
"@

$dataReadme = Join-Path $Root 'data\README.md'
$items = New-Object System.Collections.Generic.List[object]
$items.Add([pscustomobject]@{ Name = "$base\UnknownCasualtiesMusicBox.dll"; Path = $dll })
$items.Add([pscustomobject]@{ Name = "$base\安装说明.txt"; Path = $installTxt })
$items.Add([pscustomobject]@{ Name = "$base\音乐盒命名规则.md"; Path = $notes })
$items.Add([pscustomobject]@{ Name = "$base\data\README.md"; Path = $dataReadme })
$noaudio = Join-Path $dist ("UnknownCasualtiesMusicBox_v" + $Version + "_noaudio.zip")
New-Zip $noaudio $items
Write-Host ("noaudio -> " + $noaudio + "  " + (Get-Item $noaudio).Length)

# ---------- 2. 音乐包分卷 ----------
# 沿用上一版分组（从 dist 里已有的旧分卷读出来）；读不到就按名字排序平均分。
$groups = @{}
$oldParts = Get-ChildItem -Path $dist -Filter '*_musicpacks_part*.zip' | Sort-Object Name
if ($oldParts.Count -gt 0 -and $oldParts[0].Name -notlike "*$Version*") {
    $n = 0
    foreach ($z in $oldParts) {
        $n++
        $zip = [IO.Compression.ZipFile]::OpenRead($z.FullName)
        $boxes = $zip.Entries | ForEach-Object { ($_.FullName -split '\\')[4] } | Sort-Object -Unique | Where-Object { $_ }
        $zip.Dispose()
        $groups[$n] = @($boxes)
    }
    Write-Host ("沿用 v1.3.2 分组，共 " + $groups.Count + " 卷")
}
else {
    $boxes = Get-ChildItem -Path $GameData -Directory | Sort-Object Name | ForEach-Object { $_.Name }
    $i = 0
    foreach ($b in $boxes) {
        $n = [int][Math]::Floor($i * $Parts / $boxes.Count) + 1
        if (-not $groups.ContainsKey($n)) { $groups[$n] = @() }
        $groups[$n] += $b
        $i++
    }
    Write-Host "自动分组"
}

$packNote = Join-Path $obj 'musicpack_133.txt'
Set-Content -LiteralPath $packNote -Encoding UTF8 -Value @"
音乐包说明
==========

所有音乐包内容均从 B 站搜集，仅供学习参考。
All music pack content is collected from Bilibili, for study and reference only.

版权归各原权利人所有（CS / CS2 音乐盒作者与版权方）。
版权所有者如要求删除，请联系仓库作者，会立即移除。

使用方法：把本压缩包内的 BepInEx 文件夹拖到游戏根目录覆盖即可
（与 winhttp.dll 同级），路径为：

  $base\data\<音乐盒名>\*.mp3

v$Version 起加载地图音乐改为槽位编号：
  waiting_map_1         第一层
  waiting_map_2 … _12   第二层及以后的随机池（每次随机抽一首）
详见「音乐盒命名规则.md」。
"@

foreach ($n in ($groups.Keys | Sort-Object)) {
    $list = New-Object System.Collections.Generic.List[object]
    $list.Add([pscustomobject]@{ Name = "$base\data\音乐包说明.txt"; Path = $packNote })
    foreach ($box in $groups[$n]) {
        $dir = Join-Path $GameData $box
        if (-not (Test-Path -LiteralPath $dir)) { Write-Host ("  [跳过] 缺少音乐盒目录: " + $box); continue }
        Get-ChildItem -LiteralPath $dir -File | ForEach-Object {
            $list.Add([pscustomobject]@{ Name = "$base\data\$box\" + $_.Name; Path = $_.FullName })
        }
    }
    $out = Join-Path $dist ("UnknownCasualtiesMusicBox_v" + $Version + "_musicpacks_part" + $n + ".zip")
    New-Zip $out $list
    Write-Host ("part" + $n + " -> " + (Split-Path $out -Leaf) + "  " + (Get-Item $out).Length + "  (" + ($groups[$n] -join ', ') + ")")
}
Write-Host "打包完成。"
