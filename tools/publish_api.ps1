# 通过 GitHub API 发布（绕开 git 协议，适配本机 TLS 不稳定的环境）
param(
    [Parameter(Mandatory=$true)][string]$Token,
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

function PostJson($uri, $obj) {
    $json = $obj | ConvertTo-Json -Depth 12 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    return Invoke-RestMethod -Uri $uri -Method Post -Headers $h -Body $bytes `
        -ContentType 'application/json; charset=utf-8' -Proxy $Proxy -TimeoutSec 60
}

# ---- 1. 收集文件（排除 .git / obj / dist / data 音频）----
# GitHub 对“完全空的仓库”拒绝 Git Data API（409 Git Repository is empty），
# 先用 Contents API 建一个占位 README，让仓库有初始 commit。
$needInit = $false
try { Invoke-RestMethod -Uri "$api/git/ref/heads/main" -Headers $h -Proxy $Proxy -TimeoutSec 30 | Out-Null }
catch { $needInit = $true }
if ($needInit) {
    $seed = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('# UnknownCasualtiesMusicBox'))
    $body = @{ message = 'chore: init'; content = $seed } | ConvertTo-Json -Compress
    try {
        Invoke-RestMethod -Uri "$api/contents/README.md" -Method Put -Headers $h `
            -Body ([Text.Encoding]::UTF8.GetBytes($body)) -ContentType 'application/json; charset=utf-8' `
            -Proxy $Proxy -TimeoutSec 60 | Out-Null
        Write-Host '已创建占位 README（初始 commit）'
    } catch { Write-Host ('初始化失败：' + $_.Exception.Message) }
}

$all = Get-ChildItem -Path $Root -Recurse -File | Where-Object {
    $_.FullName -notmatch '\\\.git\\' -and
    $_.FullName -notmatch '\\obj\\' -and
    $_.FullName -notmatch '\\dist\\' -and
    $_.FullName -notmatch "\\data\\[^\\]+\\"
}
Write-Host ("待上传文件数 = " + $all.Count)

# ---- 2. 创建 blob ----
$treeItems = @()
foreach ($f in $all) {
    $rel = $f.FullName.Substring($Root.Length + 1).Replace('\','/')
    $b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($f.FullName))
    $blob = PostJson "$api/git/blobs" @{ content = $b64; encoding = 'base64' }
    $treeItems += @{ path = $rel; mode = '100644'; type = 'blob'; sha = $blob.sha }
    Write-Host ("  blob " + $rel)
}

# ---- 3. tree / commit / ref ----
$tree = PostJson "$api/git/trees" @{ tree = $treeItems }
Write-Host ("tree = " + $tree.sha)

$msg = @"
feat: 未知伤亡音乐盒 v1.3.0

CS 风格音乐盒模组：一个音乐盒文件夹提供全部事件音乐，设置并入原版音频页。

- 12 项设置：音乐盒选择面板 + 11 个事件音量滑条（1% 步进）
- 事件覆盖大厅/角色选择/交易/加载/进入/战斗/濒死/暂停/死亡/结局
- 1.5s 淡入淡出交叉过渡，原版音乐同步压制
- 对话/交易跟随原版 lowCutoff 低通闷音
- 击杀长老接管为 final_result_2

此内容（包含代码本体）含 AI 生成。
"@
$author = @{ name = $Owner; email = "$Owner@users.noreply.github.com" }
$commit = PostJson "$api/git/commits" @{
    message = $msg
    tree    = $tree.sha
    parents = @()
    author  = $author
    committer = $author
}
Write-Host ("commit = " + $commit.sha)

try {
    PostJson "$api/git/refs" @{ ref = 'refs/heads/main'; sha = $commit.sha } | Out-Null
    Write-Host "ref refs/heads/main 已创建"
} catch {
    # 已存在则强制更新
    $body = @{ sha = $commit.sha; force = $true }
    Invoke-RestMethod -Uri "$api/git/refs/heads/main" -Method Patch -Headers $h `
        -Body ([Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json))) `
        -ContentType 'application/json; charset=utf-8' -Proxy $Proxy -TimeoutSec 60 | Out-Null
    Write-Host "ref refs/heads/main 已更新"
}

# ---- 4. tag ----
try {
    PostJson "$api/git/refs" @{ ref = 'refs/tags/v1.3.0'; sha = $commit.sha } | Out-Null
    Write-Host "tag v1.3.0 已创建"
} catch { Write-Host ("tag 已存在或失败：" + $_.Exception.Message) }

Write-Host ("COMMIT_SHA=" + $commit.sha)