param(
    [Parameter(Mandatory=$true)][string]$Token,
    [int]$From = 0,
    [int]$Count = 6,
    [string]$Owner = 'nightblade0351',
    [string]$Repo  = 'UnknownCasualtiesMusicBox',
    [string]$Proxy = 'http://127.0.0.1:7897',
    [string]$Root  = 'D:\PCUser\git\UnknownCasualtiesMusicBox'
)

[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'
$api = "https://api.github.com/repos/$Owner/$Repo"
$h = @{
    'User-Agent'    = 'ucmb-publish'
    'Authorization' = "Bearer $Token"
    'Accept'        = 'application/vnd.github+json'
}

$all = @(Get-ChildItem -Path $Root -Recurse -File | Where-Object {
    $_.FullName -notmatch '\\\.git\\' -and
    $_.FullName -notmatch '\\obj\\' -and
    $_.FullName -notmatch '\\dist\\' -and
    $_.FullName -notmatch "\\data\\[^\\]+\\"
} | Sort-Object FullName)

Write-Host ("总文件 " + $all.Count + "，本批 " + $From + ".." + [Math]::Min($From + $Count - 1, $all.Count - 1))

for ($i = $From; $i -lt [Math]::Min($From + $Count, $all.Count); $i++) {
    $f = $all[$i]
    $rel = $f.FullName.Substring($Root.Length + 1).Replace('\','/')
    $b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($f.FullName))

    # 已存在则带上 sha 做更新
    $sha = $null
    try {
        $exist = Invoke-RestMethod -Uri "$api/contents/$rel" -Headers $h -Proxy $Proxy -TimeoutSec 20
        if ($exist -and $exist.sha) { $sha = $exist.sha }
    } catch { }

    $body = @{ message = "chore: add $rel"; content = $b64 }
    if ($sha) { $body.sha = $sha }
    $json = $body | ConvertTo-Json -Compress
    try {
        Invoke-RestMethod -Uri "$api/contents/$rel" -Method Put -Headers $h `
            -Body ([Text.Encoding]::UTF8.GetBytes($json)) `
            -ContentType 'application/json; charset=utf-8' -Proxy $Proxy -TimeoutSec 30 | Out-Null
        Write-Host ("  OK   " + $rel)
    } catch {
        Write-Host ("  FAIL " + $rel + " -> " + $_.Exception.Message)
    }
}