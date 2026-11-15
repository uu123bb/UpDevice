<#
.SYNOPSIS
    生成用于 UpDevice 的自签名代码签名证书，并导出可放进 CI 的 pfx。

.DESCRIPTION
    自签名证书解决两件事：
      · UAC 弹窗不再显示「未知发布者」，而是显示证书里的发布者名
      · 配合 SmartScreen，签名过的程序至少会给出「仍要运行」的入口，而不是直接拦掉

    但它换不来「信誉」—— SmartScreen 对陌生发布者的首次运行仍可能提示。
    真正的信誉只有正式（OV/EV）证书才有。

    -Install 会把证书装进当前用户的「受信任的根证书颁发机构」与「受信任的发布者」，
    这样本机才会信任用它签出来的程序。装根证书属于安全敏感操作，默认不执行。

.EXAMPLE
    # 只生成，不安装（生成后用 -Install 再跑一次，或用 certmgr 手动导入）
    .\New-CodeSigningCert.ps1

.EXAMPLE
    # 生成并安装到当前用户，同时签名，输出 CI 需要的 base64
    .\New-CodeSigningCert.ps1 -Install -Sign
#>
[CmdletBinding()]
param(
    [string]$Subject = "CN=UpDevice, O=uu123bb, OU=Self-Signed",
    [int]$Years = 3,
    [string]$OutDir = (Join-Path $PSScriptRoot "dist"),

    # 把证书装进当前用户的受信任根与受信任发布者；不装的话本机不会信任签名
    [switch]$Install,

    # 生成后立刻给 src 下的产物签名（用于本地出包）
    [switch]$Sign,

    # pfx 密码。留空则随机生成并打印出来，务必自己存好
    [string]$Password
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Password)) {
    $bytes = New-Object byte[] 24
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $Password = [Convert]::ToBase64String($bytes)
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Write-Host "生成自签名代码签名证书…"
$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -KeyUsage DigitalSignature `
    -KeyExportPolicy Exportable `
    -KeyLength 3072 `
    -HashAlgorithm SHA256 `
    -NotAfter (Get-Date).AddYears($Years) `
    -CertStoreLocation "Cert:\CurrentUser\My"

$pfxPath = Join-Path $OutDir "codesign.pfx"
$cerPath = Join-Path $OutDir "codesign.cer"
$secure = ConvertTo-SecureString -String $Password -AsPlainText -Force

Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $secure | Out-Null
Export-Certificate -Cert $cert -FilePath $cerPath -Type CERT | Out-Null

Write-Host ""
Write-Host "已生成：" -ForegroundColor Green
Write-Host "  证书  : $cerPath"
Write-Host "  私钥  : $pfxPath"
Write-Host "  密码  : $Password" -ForegroundColor Yellow
Write-Host "  指纹  : $($cert.Thumbprint)"
Write-Host ""

if ($Install) {
    Write-Host "安装到当前用户信任存储（受信任的根 + 受信任的发布者）…"

    # Import-Certificate 写 Root 时会弹 Windows 的安全确认框，
    # 在非交互会话（脚本/CI/远程）里会直接失败，所以退回 certutil。
    foreach ($storeName in @("Root", "TrustedPublisher")) {
        $added = $false
        try {
            Import-Certificate -FilePath $cerPath -CertStoreLocation "Cert:\CurrentUser\$storeName" -ErrorAction Stop | Out-Null
            $added = $true
        }
        catch {
            $output = certutil -user -addstore $storeName $cerPath 2>&1
            $added = $LASTEXITCODE -eq 0
            if (-not $added) {
                Write-Host ("写入 {0} 失败：{1}" -f $storeName, ($output | Select-Object -Last 1)) -ForegroundColor Red
            }
        }

        if ($added) {
            Write-Host "  已写入 $storeName"
        }
    }

    Write-Host "已安装。之后用这个证书签名的程序在本机不会再报「未知发布者」。" -ForegroundColor Green
}
else {
    Write-Host "未安装到信任存储。要让本机信任该证书，重新执行并加 -Install，或手动双击 $cerPath 导入到「受信任的根证书颁发机构」。" -ForegroundColor Yellow
}

if ($Sign) {
    $targets = Get-ChildItem -Path (Join-Path $PSScriptRoot "..\..\src") -Recurse -Include "UpDevice.Launcher.exe", "UpDevice.Service.exe", "UpDevice.Tray.exe" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\publish\\|\\bin\\Release\\" }

    if (-not $targets) {
        Write-Host "没有找到可签名的产物（先 dotnet publish 到 publish/）" -ForegroundColor Yellow
    }

    foreach ($file in $targets) {
        try {
            $result = Set-AuthenticodeSignature -FilePath $file.FullName -Certificate $cert `
                -HashAlgorithm SHA256 -TimestampServer "http://timestamp.digicert.com"
            Write-Host ("签名 {0}: {1}" -f $file.Name, $result.Status)
        }
        catch {
            Write-Host ("签名失败 {0}: {1}" -f $file.Name, $_.Exception.Message) -ForegroundColor Red
        }
    }
}

Write-Host ""
$b64Path = Join-Path $OutDir "codesign.pfx.base64.txt"
[IO.File]::WriteAllText($b64Path, [Convert]::ToBase64String([IO.File]::ReadAllBytes($pfxPath)))
Write-Host "给 CI 用的两个 secret（GitHub 仓库 Settings → Secrets and variables → Actions）：" -ForegroundColor Cyan
Write-Host "  SIGNING_PFX_BASE64   = 把 $b64Path 的全部内容粘进去（长度 $((Get-Item $b64Path).Length) 字节，别手动换行）"
Write-Host "  SIGNING_PFX_PASSWORD = $Password" -ForegroundColor Cyan
Write-Host ""
Write-Host "注意：codesign.pfx / base64 文件等同私钥，不要提交到仓库（已加进 .gitignore）。" -ForegroundColor Yellow
