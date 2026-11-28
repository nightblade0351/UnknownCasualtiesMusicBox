param(
    [Parameter(Mandatory=$true)][string]$Token,
    [Parameter(Mandatory=$true)][int]$ReleaseId,
    [Parameter(Mandatory=$true)][string]$ZipFile,
    [Parameter(Mandatory=$true)][string]$Name,
    [string]$Owner = 'nightblade0351',
    [string]$Repo  = 'UnknownCasualtiesMusicBox',
    [string]$Proxy = 'http://127.0.0.1:7897',
    [string]$LogPath = ''
)

[Console]::OutputEncoding = [Text.Encoding]::UTF8
if (-not $LogPath) { $LogPath = Join-Path $env:TEMP 'ucmb_upload.log' }
function W($m) { $m | Tee-Object -FilePath $LogPath -Append | Out-Null }

$url = "https://uploads.github.com/repos/$Owner/$Repo/releases/$ReleaseId/assets?name=" + [uri]::EscapeDataString($Name)
$bytes = [IO.File]::ReadAllBytes($ZipFile)
W ("file=" + $ZipFile + " size=" + $bytes.Length)

$ok = $false
for ($i = 1; $i -le 5 -and -not $ok; $i++) {
    W ("attempt " + $i + " @ " + (Get-Date -Format "HH:mm:ss"))
    $req = [System.Net.HttpWebRequest]::Create($url)
    $req.Method = 'POST'
    $req.Headers.Add('Authorization', "Bearer $Token")
    $req.UserAgent = 'ucmb'
    $req.Accept = 'application/vnd.github+json'
    $req.ContentType = 'application/zip'
    $req.ContentLength = $bytes.Length
    $req.Proxy = New-Object System.Net.WebProxy($Proxy)
    $req.Timeout = 1800000
    $req.ReadWriteTimeout = 1800000
    try {
        $s = $req.GetRequestStream()
        $chunk = 262144
        $off = 0
        while ($off -lt $bytes.Length) {
            $len = [Math]::Min($chunk, $bytes.Length - $off)
            $s.Write($bytes, $off, $len)
            $off += $len
            if (($off % (10 * 1024 * 1024)) -lt $chunk) { W ("  sent " + [math]::Round($off/1MB,1) + " MB") }
        }
        $s.Close()
        W "stream closed, waiting response..."
        $res = $req.GetResponse()
        $sr = New-Object IO.StreamReader($res.GetResponseStream())
        $t = $sr.ReadToEnd()
        $sr.Close(); $res.Close()
        $o = $t | ConvertFrom-Json
        W ("OK " + $o.name + " " + $o.size + " " + $o.browser_download_url)
        $ok = $true
    } catch {
        W ("FAIL " + $_.Exception.Message)
        Start-Sleep -Seconds 5
    }
}
if (-not $ok) { W "ALL ATTEMPTS FAILED" }