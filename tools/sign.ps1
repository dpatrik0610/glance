# Signs a file with the local Glance certificate. Runs after every build; does nothing if tools\trust.ps1 hasn't been run.
param([Parameter(Mandatory)][string]$Path)

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object { $_.Subject -eq 'CN=Glance' -and $_.NotAfter -gt (Get-Date) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) { 'sign: no Glance certificate, skipping (run tools\trust.ps1 as admin once)'; exit 0 }

# No timestamp server: keeps builds offline-friendly. The cert is valid for 10 years.
$r = Set-AuthenticodeSignature -FilePath $Path -Certificate $cert -HashAlgorithm SHA256
if ($r.Status -ne 'Valid') { "sign: $($r.Status) $($r.StatusMessage)"; exit 1 }
"sign: $Path signed ($($cert.Thumbprint.Substring(0, 8)))"
