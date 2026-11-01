<#
.SYNOPSIS
  Kiểm thử nghiệm thu: tải lên tệp video lớn (mặc định 2 GB) bằng giao thức tus có tạm dừng/tiếp tục, rồi kiểm tra tua (HTTP Range).
.EXAMPLE
  pwsh -File scripts/test-large-upload.ps1 -BaseUrl http://localhost:8088 -Username admin -Password '...'
#>
param(
    [string]$BaseUrl = 'http://localhost',
    [Parameter(Mandatory = $true)][string]$Username,
    [Parameter(Mandatory = $true)][string]$Password,
    [long]$SizeMb = 2048,
    [int]$ChunkMb = 50
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromHours(2)

$login = Invoke-RestMethod -Method Post "$BaseUrl/api/v1/auth/login" -ContentType 'application/json' -Body (@{ username = $Username; password = $Password } | ConvertTo-Json)
$token = $login.accessToken
$ws = $login.user.personalWorkspaceId
$client.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $token)
$client.DefaultRequestHeaders.Add('Tus-Resumable', '1.0.0')

# Test file: a real MP4 header (valid magic bytes) followed by sparse padding up to the requested size.
$path = Join-Path ([IO.Path]::GetTempPath()) "bluedrive-large-$SizeMb.mp4"
$sample = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/BlueDrive.Infrastructure/Persistence/Seed/Samples/video-mau.mp4'
$size = $SizeMb * 1MB
if (-not (Test-Path $path) -or (Get-Item $path).Length -ne $size) {
    $fs = [IO.File]::Create($path)
    $head = [IO.File]::ReadAllBytes($sample)
    $fs.Write($head, 0, $head.Length)
    $fs.SetLength($size)
    $fs.Dispose()
}
Write-Host "Tệp kiểm thử: $path ($SizeMb MB)"

function B64([string]$s) { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($s)) }
$create = [System.Net.Http.HttpRequestMessage]::new('POST', "$BaseUrl/tus")
$create.Headers.Add('Upload-Length', "$size")
$create.Headers.Add('Upload-Metadata', "filename $(B64 'Video kiểm thử 2GB.mp4'),workspaceId $(B64 $ws)")
$create.Content = [System.Net.Http.ByteArrayContent]::new([byte[]]@())
$res = $client.SendAsync($create).Result
if ([int]$res.StatusCode -ne 201) { throw "Tạo phiên tus thất bại: $([int]$res.StatusCode) $($res.Content.ReadAsStringAsync().Result)" }
$location = $res.Headers.Location.ToString()
if (-not $location.StartsWith('http')) { $location = "$BaseUrl$location" }
$tusId = $location.TrimEnd('/').Split('/')[-1]

$sw = [Diagnostics.Stopwatch]::StartNew()
$stream = [IO.File]::OpenRead($path)
$buffer = [byte[]]::new($ChunkMb * 1MB)
$offset = 0L
$paused = $false
while ($offset -lt $size) {
    # Simulate an interruption at ~40%: ask the server where to resume (HEAD) and continue from there.
    if (-not $paused -and $offset -ge $size * 0.4) {
        $paused = $true
        $head = [System.Net.Http.HttpRequestMessage]::new('HEAD', $location)
        $serverOffset = [long]($client.SendAsync($head).Result.Headers.GetValues('Upload-Offset') | Select-Object -First 1)
        Write-Host "Tạm dừng tại $([math]::Round($offset / 1MB)) MB, máy chủ đã nhận $([math]::Round($serverOffset / 1MB)) MB → tiếp tục"
        $offset = $serverOffset
    }
    $stream.Position = $offset
    $n = $stream.Read($buffer, 0, $buffer.Length)
    $patch = [System.Net.Http.HttpRequestMessage]::new('PATCH', $location)
    $patch.Headers.Add('Upload-Offset', "$offset")
    $patch.Content = [System.Net.Http.ByteArrayContent]::new($buffer, 0, $n)
    $patch.Content.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new('application/offset+octet-stream')
    $r = $client.SendAsync($patch).Result
    if ([int]$r.StatusCode -ne 204) { throw "PATCH thất bại tại $offset : $([int]$r.StatusCode)" }
    $offset += $n
    Write-Progress -Activity 'Tải lên' -Status "$([math]::Round($offset / 1MB)) / $SizeMb MB" -PercentComplete ($offset * 100 / $size)
}
$stream.Dispose()
Write-Host "Đã gửi xong $SizeMb MB trong $([math]::Round($sw.Elapsed.TotalSeconds)) giây"

$complete = Invoke-RestMethod -Method Post "$BaseUrl/api/v1/uploads/$tusId/complete" -Headers @{ Authorization = "Bearer $token" } -ContentType 'application/json' `
    -Body (@{ fileName = 'Video kiểm thử 2GB.mp4'; workspaceId = $ws } | ConvertTo-Json)
Write-Host "Đã tạo tệp $($complete.name) (id $($complete.fileId)) sau $([math]::Round($sw.Elapsed.TotalSeconds)) giây"

$url = (Invoke-RestMethod "$BaseUrl/api/v1/files/$($complete.fileId)/download-url?inline=true" -Headers @{ Authorization = "Bearer $token" }).url
$seekAt = [long]($size * 0.75)
$req = [System.Net.Http.HttpRequestMessage]::new('GET', "$BaseUrl$url")
$req.Headers.Range = [System.Net.Http.Headers.RangeHeaderValue]::new($seekAt, $seekAt + 1MB - 1)
$t = [Diagnostics.Stopwatch]::StartNew()
$r = $client.SendAsync($req).Result
$bytes = $r.Content.ReadAsByteArrayAsync().Result
Write-Host "Tua tới $([math]::Round($seekAt / 1MB)) MB: HTTP $([int]$r.StatusCode), nhận $($bytes.Length) byte trong $($t.ElapsedMilliseconds) ms"
if ([int]$r.StatusCode -ne 206 -or $bytes.Length -ne 1MB) { throw 'Kiểm tra tua (Range) thất bại' }
Write-Host 'ĐẠT: tải lên tệp lớn resumable và tua video (HTTP Range) hoạt động.' -ForegroundColor Green
