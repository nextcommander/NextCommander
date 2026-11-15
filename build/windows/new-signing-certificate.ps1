<#
.SYNOPSIS
  Creates the self-signed certificate that signs the setup program (personal use; not a publicly trusted signature).

.DESCRIPTION
  The certificate (subject CN=NextCommander, code signing) goes into the user's certificate store
  (Cert:\CurrentUser\My): no password, no .pfx file. build/publish.ps1 finds it there and signs the -setup.exe with it
  (another subject: $env:SIGN_PUBLISHER). Its public part is written to dist/NextCommander-signing.cer.

  -Trust adds the certificate to the machine's "Trusted People" store once (administrator rights: a UAC prompt), so
  that Windows shows the signature as known on this machine. Without -Trust that can be done later by double-clicking
  the .cer file (Install Certificate > Local Machine > Trusted People).

.EXAMPLE
  ./build/windows/new-signing-certificate.ps1           # create (or reuse) the certificate
  ./build/windows/new-signing-certificate.ps1 -Trust    # and trust it on this machine
#>
param(
    [string]$Subject = 'CN=NextCommander',
    [switch]$Trust
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

$certificate = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -eq $Subject -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if ($certificate) {
    Write-Host "Existing certificate: $($certificate.Thumbprint) (valid until $($certificate.NotAfter.ToString('d')))"
}
else {
    # code signing (EKU 1.3.6.1.5.5.7.3.3), not a CA (basic constraints empty), 5 years
    $certificate = New-SelfSignedCertificate -Type Custom -Subject $Subject -FriendlyName 'NextCommander code signing' `
        -KeyUsage DigitalSignature -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') `
        -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5)
    Write-Host "Created certificate: $($certificate.Thumbprint) (valid until $($certificate.NotAfter.ToString('d')))"
}

$cer = Join-Path $root 'dist/NextCommander-signing.cer'
New-Item -ItemType Directory -Force (Split-Path $cer) | Out-Null
Export-Certificate -Cert $certificate -FilePath $cer -Type CERT | Out-Null
Write-Host "Public certificate: $cer"

if ($Trust) {
    $trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $certificate.Thumbprint
    if ($trusted) {
        Write-Host 'Already trusted on this machine.'
    }
    else {
        # the machine store needs administrator rights: an elevated PowerShell (UAC prompt) imports the .cer
        $command = "Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"
        $process = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList '-NoProfile', '-Command', $command
        if ($process.ExitCode -eq 0) { Write-Host 'Trusted on this machine (Trusted People).' } else { throw "Import failed ($($process.ExitCode))" }
    }
}
