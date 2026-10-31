# One-time, run as admin: makes this PC trust Glance builds.
# Creates a self-signed *code-signing-only* certificate (it can't vouch for websites), keeps its private key
# non-exportable in your user store, and trusts its public half machine-wide, so UAC shows a verified publisher.
# Undo everything: powershell -File tools\trust.ps1 -Remove   (as admin)
#Requires -RunAsAdministrator
param([switch]$Remove)

$subject = 'CN=Glance'
$mine = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object Subject -eq $subject

if ($Remove) {
    foreach ($c in $mine) {
        foreach ($store in 'Cert:\LocalMachine\Root', 'Cert:\LocalMachine\TrustedPublisher', 'Cert:\CurrentUser\My') {
            Get-ChildItem $store | Where-Object Thumbprint -eq $c.Thumbprint | Remove-Item
        }
    }
    'Glance certificate removed.'
    return
}

$cert = $mine | Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -FriendlyName 'Glance local code signing' `
        -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy NonExportable -KeyAlgorithm RSA -KeyLength 3072 `
        -NotAfter (Get-Date).AddYears(10)
}

$tmp = Join-Path $env:TEMP 'glance-signing.cer'
Export-Certificate -Cert $cert -FilePath $tmp | Out-Null
Import-Certificate -FilePath $tmp -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Import-Certificate -FilePath $tmp -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
Remove-Item $tmp
"Trusted: $($cert.Subject) $($cert.Thumbprint), valid until $($cert.NotAfter.ToString('yyyy-MM-dd'))"
