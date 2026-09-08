# 自签 MSIX 代码签名证书：导出 dist/LumaMusic.pfx（签名用）+ dist/LumaMusic.cer（发给用户导入信任）。
# 证书存储里没有历史 Luma 证书时可随时重跑；换证书后老用户需卸载重装 MSIX（签名不匹配）。
# 硬规则：Subject 必须逐字符等于 AppxManifest 的 Publisher（CN=LumaMusic，不能带 O=/C= 等后缀），
#         否则 signtool 对 MSIX 报 SignerSign failed 0x8007000b（无任何进一步提示）。
param([Parameter(Mandatory=$true)][string]$PfxPassword)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$pfx=Join-Path $root 'dist/LumaMusic.pfx'
$cer=Join-Path $root 'dist/LumaMusic.cer'
$notAfter=(Get-Date).AddYears(5)
$cert=New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=LumaMusic' `
    -FriendlyName 'LumaMusic MSIX Signing' `
    -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter $notAfter `
    -KeyAlgorithm RSA -KeyLength 2048 `
    -KeyExportPolicy Exportable -KeySpec Signature `
    -Provider 'Microsoft Enhanced Cryptographic Provider v1.0'
$pwd2=ConvertTo-SecureString -String $PfxPassword -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $pwd2 | Out-Null
Export-Certificate -Cert $cert -FilePath $cer -Type CERT | Out-Null
Write-Output "cert ready: thumbprint=$($cert.Thumbprint) pfx=$pfx cer=$cer"
