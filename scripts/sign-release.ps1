param(
    [Parameter(Mandatory = $true)]
    [string]$ReleaseDirectory,
    [string]$CertificatePath
)

$ErrorActionPreference = 'Stop'
$release = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$exe = Join-Path $release 'CodexBackup.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "缺少 CodexBackup.exe：$exe" }
if ([string]::IsNullOrWhiteSpace($CertificatePath)) {
    Write-Output '未提供证书路径；签名步骤按预期跳过，发布包保持未签名。'
    exit 0
}
$certificate = Get-Item -LiteralPath $CertificatePath
$result = Set-AuthenticodeSignature -FilePath $exe -Certificate $certificate
if ($result.Status -ne 'Valid') { throw "EXE 签名失败：$($result.Status) $($result.StatusMessage)" }
Write-Output "已使用证书签名：$($certificate.Subject)"
