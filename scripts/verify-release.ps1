param(
    [Parameter(Mandatory = $true)]
    [string]$ReleaseDirectory
)

$ErrorActionPreference = 'Stop'
$release = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$exe = Join-Path $release 'CodexBackup.exe'
$releaseJson = Join-Path $release 'RELEASE.json'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "缺少 CodexBackup.exe：$exe" }
if (-not (Test-Path -LiteralPath $releaseJson -PathType Leaf)) { throw "缺少 RELEASE.json：$releaseJson" }

$metadata = Get-Content -Raw -Encoding UTF8 $releaseJson | ConvertFrom-Json
if ($metadata.version -ne '0.3.1-preview') { throw "发布版本不是 0.3.1-preview：$($metadata.version)" }
$hashFile = Join-Path $release 'SHA256SUMS.txt'
$actualHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToUpperInvariant()
if (Test-Path -LiteralPath $hashFile) {
    $hashLine = (Get-Content -Raw -Encoding ASCII $hashFile).Trim().Split([Environment]::NewLine)[0]
    if ($hashLine -notmatch $actualHash) { throw "EXE 哈希与 SHA256SUMS.txt 不一致。" }
}

$checkRoot = Join-Path $env:TEMP ('CodexBackup-release-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $checkRoot | Out-Null
$selfTest = Join-Path $checkRoot 'self-test.json'
$selfProcess = Start-Process -FilePath $exe -ArgumentList @('--self-test', $selfTest) -Wait -PassThru -WindowStyle Hidden
if ($selfProcess.ExitCode -ne 0) { throw "EXE self-test 失败，退出码 $($selfProcess.ExitCode)。" }
$smoke = Join-Path $checkRoot 'smoke-test.json'
$smokeProcess = Start-Process -FilePath $exe -ArgumentList @('--smoke-test', $smoke) -Wait -PassThru -WindowStyle Hidden
if ($smokeProcess.ExitCode -ne 0) { throw "WPF smoke-test 失败，退出码 $($smokeProcess.ExitCode)。" }

foreach ($file in Get-ChildItem -LiteralPath $release -Recurse -File | Where-Object { $_.Extension -in '.md', '.json', '.txt' }) {
    $null = Get-Content -Raw -Encoding UTF8 $file.FullName
    $badMarkers = @('?' + '?' + '?', '锟' + '斤' + '拷')
    if ($badMarkers | Where-Object { (Get-Content -Raw -Encoding UTF8 $file.FullName).Contains($_) }) { throw "发现疑似乱码：$($file.FullName)" }
}
$userName = [Environment]::UserName
foreach ($file in Get-ChildItem -LiteralPath $release -Recurse -File | Where-Object { $_.Extension -in '.md', '.json', '.txt' }) {
    if ((Get-Content -Raw -Encoding UTF8 $file.FullName) -match "C:\\Users\\$([regex]::Escape($userName))\\") { throw "发布材料包含当前用户私有路径：$($file.FullName)" }
}
Write-Output "release verified: version=$($metadata.version) sha256=$actualHash"
