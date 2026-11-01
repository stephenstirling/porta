<#
  Makes Porta's self-signed code-signing certificate. Run this ONCE, on your own PC.

  It creates, in %USERPROFILE%\PortaSigning (outside the project, never committed):
    Porta-signing.pfx   the PRIVATE key + certificate, protected by a random password
    Porta-signing.cer   the PUBLIC certificate (safe to share; goes in the releases)

  Then it walks you through copying the two GitHub secrets to the clipboard.
  The certificate is NOT installed on this PC or marked as trusted.
#>
$ErrorActionPreference = 'Stop'

$dir = Join-Path $env:USERPROFILE 'PortaSigning'
$pfx = Join-Path $dir 'Porta-signing.pfx'
$cer = Join-Path $dir 'Porta-signing.cer'
if (Test-Path $pfx) { throw "$pfx already exists. Keep it - making a new certificate changes Porta's signing identity. Move it away first if you really want a new one." }
New-Item -ItemType Directory -Force $dir | Out-Null

# A random 28-character password (letters and digits only, so it pastes cleanly).
$chars = [char[]]([char]'a'..[char]'z' + [char]'A'..[char]'Z' + [char]'0'..[char]'9')
$bytes = New-Object byte[] 28
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$password = -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
$secure = ConvertTo-SecureString $password -AsPlainText -Force

# Made in the current user's store only to get an exportable key; removed again below.
$cert = New-SelfSignedCertificate -Type CodeSigningCert `
    -Subject 'CN=Porta, O=Stephen Stirling' `
    -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
    -KeyExportPolicy Exportable -KeyUsage DigitalSignature `
    -NotAfter (Get-Date).AddYears(10) `
    -CertStoreLocation Cert:\CurrentUser\My
try {
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $secure | Out-Null
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
} finally {
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -DeleteKey
}

$sha256 = (Get-FileHash $cer -Algorithm SHA256).Hash
Write-Host ''
Write-Host 'Certificate made.' -ForegroundColor Green
Write-Host "  Files:      $dir"
Write-Host "  Thumbprint: $($cert.Thumbprint)    <- send this to Claude / put in the README (it is public)"
Write-Host "  .cer SHA256: $sha256"
Write-Host ''
Write-Host 'Now the two GitHub secrets. Open this page in your browser first:'
Write-Host '  https://github.com/stephenstirling/porta/settings/secrets/actions/new' -ForegroundColor Cyan
Write-Host ''

$b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($pfx))
Set-Clipboard -Value $b64
Write-Host 'SECRET 1 is on your clipboard.' -ForegroundColor Yellow
Write-Host '  Name:   PORTA_SIGNING_PFX_BASE64'
Write-Host '  Secret: press Ctrl+V   (a very long block of letters - that is right)'
Write-Host '  Then click "Add secret".'
Read-Host 'Press Enter here once SECRET 1 is saved on GitHub' | Out-Null

Set-Clipboard -Value $password
Write-Host ''
Write-Host 'SECRET 2 is on your clipboard.' -ForegroundColor Yellow
Write-Host '  Click "New repository secret" again.'
Write-Host '  Name:   PORTA_SIGNING_PFX_PASSWORD'
Write-Host '  Secret: press Ctrl+V'
Write-Host '  Then click "Add secret".'
Write-Host ''
Write-Host "  (The password is: $password" -NoNewline
Write-Host '  - save it in your password manager next to the .pfx file.)'
Read-Host 'Press Enter here once SECRET 2 is saved on GitHub' | Out-Null

Set-Clipboard -Value ' '   # don't leave the password sitting on the clipboard
Write-Host ''
Write-Host 'Done. Back up Porta-signing.pfx and its password somewhere safe and private' -ForegroundColor Green
Write-Host '(GitHub never shows a secret again, so this file is the only copy of the key).'
