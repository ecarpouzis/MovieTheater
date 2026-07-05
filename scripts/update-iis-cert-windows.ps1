#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Pulls the current Let's Encrypt cert from MicroK8s and updates the IIS HTTPS binding.
    Designed to run as a weekly Windows Scheduled Task (see register-cert-renewal-task.ps1).

.DESCRIPTION
    Tries three methods to read the cert from the cluster, in order:
      1. wsl.exe microk8s kubectl  (MicroK8s running in WSL on this machine)
      2. wsl.exe kubectl           (any WSL distro with kubectl configured)
      3. kubectl.exe               (kubectl installed directly on Windows)

    Tries three methods to create the PFX, in order:
      1. .NET X509Certificate2.CreateFromPem  (PowerShell 7 / .NET 5+)
      2. openssl via WSL
      3. openssl.exe in PATH

.EXAMPLE
    # Run manually to test:
    powershell.exe -ExecutionPolicy Bypass -File update-iis-cert-windows.ps1

    # Run and write a log:
    powershell.exe -ExecutionPolicy Bypass -File update-iis-cert-windows.ps1 `
        -LogFile "C:\ProgramData\MovieTheater\logs\cert-renewal.log"
#>
param(
    [string]$Namespace   = "movietheater",
    [string]$SecretName  = "movietheater-tls",
    [string]$SiteName    = "MovieTheater",
    [string]$HostHeader  = "theater.carpouzis.com",
    [string]$PfxPassword = "",   # transient PFX password; a fresh random one is generated below if unset (never hardcode)
    [string]$TempDir     = "$env:TEMP\movietheater-cert",
    [string]$LogFile     = ""
)

$ErrorActionPreference = "Stop"
# The PFX is an ephemeral export→import within this one run, so a random per-run password beats a
# committed constant (which would be a public credential leak). Override -PfxPassword only if needed.
if (-not $PfxPassword) { $PfxPassword = [System.Guid]::NewGuid().ToString('N') + [System.Guid]::NewGuid().ToString('N') }
New-Item -ItemType Directory -Force -Path $TempDir | Out-Null

function Write-Log {
    param([string]$Message)
    $line = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $Message"
    Write-Host $line
    if ($LogFile) {
        $line | Out-File -FilePath $LogFile -Append -Encoding utf8
    }
}

# ── Step 1: Read TLS secret from Kubernetes ──────────────────────────────────

# $Args is a PowerShell automatic variable - always use a distinct name here.
function Invoke-Kubectl {
    param([string[]]$KubectlArgs)
    $prefixes = @(
        @("wsl.exe", "-e", "microk8s", "kubectl"),
        @("wsl.exe", "-e", "kubectl"),
        @("kubectl.exe")
    )
    foreach ($prefix in $prefixes) {
        try {
            $exe     = $prefix[0]
            $preArgs = if ($prefix.Count -gt 1) { $prefix[1..($prefix.Count - 1)] } else { @() }
            $allArgs = $preArgs + $KubectlArgs
            $out     = & $exe @allArgs 2>$null
            if ($LASTEXITCODE -eq 0 -and $out) { return $out }
        } catch {}
    }
    return $null
}

Write-Log "Reading TLS secret from Kubernetes..."
$crtB64 = Invoke-Kubectl @("get","secret",$SecretName,"-n",$Namespace,"-o","jsonpath={.data.tls\.crt}")
$keyB64 = Invoke-Kubectl @("get","secret",$SecretName,"-n",$Namespace,"-o","jsonpath={.data.tls\.key}")

if (-not $crtB64 -or -not $keyB64) {
    Write-Log @"
ERROR: Failed to read TLS secret '$SecretName' from Kubernetes.
Ensure one of the following is available and configured:
  - WSL with MicroK8s: wsl.exe -e microk8s kubectl get secret ...
  - WSL with kubectl:  wsl.exe -e kubectl get secret ...
  - kubectl.exe in PATH with valid kubeconfig for the cluster
"@
    exit 1
}

$crtBytes = [Convert]::FromBase64String($crtB64.Trim())
$keyBytes = [Convert]::FromBase64String($keyB64.Trim())

$crtPath = Join-Path $TempDir "tls.crt"
$keyPath = Join-Path $TempDir "tls.key"
$pfxPath = Join-Path $TempDir "cert.pfx"

[System.IO.File]::WriteAllBytes($crtPath, $crtBytes)
[System.IO.File]::WriteAllBytes($keyPath, $keyBytes)
Write-Log "Cert and key written to $TempDir"

# ── Step 2: Check if the cert is actually newer than what IIS already has ────

try {
    $pemText    = [System.Text.Encoding]::ASCII.GetString($crtBytes)
    # Parse NotBefore to detect if K8s cert differs from the one already in the store
    $clusterCert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPem($pemText)
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store("My","LocalMachine")
    $store.Open("ReadOnly")
    $existing = $store.Certificates | Where-Object { $_.Subject -like "*$HostHeader*" } |
                Sort-Object NotBefore -Descending | Select-Object -First 1
    $store.Close()

    if ($existing -and $existing.Thumbprint -eq $clusterCert.Thumbprint) {
        Write-Log "Certificate is already up to date (thumbprint: $($existing.Thumbprint)). Nothing to do."
        Remove-Item $TempDir -Recurse -Force -ErrorAction SilentlyContinue
        exit 0
    }
    if ($existing) {
        Write-Log "New certificate detected (cluster thumbprint: $($clusterCert.Thumbprint), current: $($existing.Thumbprint)). Updating..."
    }
} catch {
    Write-Log "Could not compare certs pre-import (continuing): $_"
}

# ── Step 3: Convert PEM -> PFX ───────────────────────────────────────────────

$pfxCreated = $false

# Method A: .NET 5+ / PowerShell 7+
if (-not $pfxCreated) {
    try {
        $crtText = [System.Text.Encoding]::ASCII.GetString($crtBytes)
        $keyText = [System.Text.Encoding]::ASCII.GetString($keyBytes)
        $x509    = [System.Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPem($crtText, $keyText)
        $pfxData = $x509.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $PfxPassword)
        [System.IO.File]::WriteAllBytes($pfxPath, $pfxData)
        $pfxCreated = $true
        Write-Log "PFX created via .NET CreateFromPem"
    } catch {}
}

# Method B: openssl via WSL (converts Windows paths to WSL mount paths)
if (-not $pfxCreated) {
    try {
        $wslCrt = (wsl.exe wslpath $crtPath.Replace('\', '/')) 2>$null
        $wslKey = (wsl.exe wslpath $keyPath.Replace('\', '/')) 2>$null
        $wslPfx = (wsl.exe wslpath $pfxPath.Replace('\', '/')) 2>$null
        if ($wslCrt -and $wslKey -and $wslPfx) {
            wsl.exe -e openssl pkcs12 -export -out $wslPfx.Trim() `
                -inkey $wslKey.Trim() -in $wslCrt.Trim() "-passout" "pass:$PfxPassword" 2>$null
            $pfxCreated = ($LASTEXITCODE -eq 0) -and (Test-Path $pfxPath)
            if ($pfxCreated) { Write-Log "PFX created via WSL openssl" }
        }
    } catch {}
}

# Method C: openssl.exe in PATH (e.g. Git for Windows, standalone OpenSSL)
if (-not $pfxCreated) {
    $openssl = Get-Command openssl.exe -ErrorAction SilentlyContinue
    if ($openssl) {
        & openssl.exe pkcs12 -export -out $pfxPath -inkey $keyPath -in $crtPath -passout pass:$PfxPassword
        $pfxCreated = ($LASTEXITCODE -eq 0)
        if ($pfxCreated) { Write-Log "PFX created via openssl.exe" }
    }
}

if (-not $pfxCreated) {
    Write-Log @"
ERROR: Could not create PFX file. Install one of:
  - PowerShell 7 (https://aka.ms/powershell) - uses built-in .NET crypto
  - WSL with openssl: wsl --install, then: sudo apt install openssl
  - OpenSSL for Windows (https://slproweb.com/products/Win32OpenSSL.html)
"@
    exit 1
}

# ── Step 4: Import certificate into Windows cert store ───────────────────────

$secPass = ConvertTo-SecureString $PfxPassword -AsPlainText -Force
$cert    = Import-PfxCertificate -FilePath $pfxPath -CertStoreLocation Cert:\LocalMachine\My -Password $secPass -Exportable

Write-Log "Certificate imported:"
Write-Log "  Thumbprint : $($cert.Thumbprint)"
Write-Log "  Subject    : $($cert.Subject)"
Write-Log "  Issuer     : $($cert.Issuer)"
Write-Log "  Expires    : $($cert.NotAfter)"

# ── Step 5: Update IIS HTTPS binding ─────────────────────────────────────────

$iis = Get-Service W3SVC -ErrorAction SilentlyContinue
if (-not $iis) {
    Write-Log "IIS not found - certificate imported to Windows store only"
} else {
    Import-Module WebAdministration -ErrorAction Stop

    $site = Get-Website -Name $SiteName -ErrorAction SilentlyContinue
    if (-not $site) {
        Write-Log "Creating IIS site: $SiteName"
        New-Website -Name $SiteName -PhysicalPath "C:\inetpub\wwwroot" -Port 80 -HostHeader $HostHeader
    }

    $existing = Get-WebBinding -Name $SiteName -Protocol https -Port 443 -ErrorAction SilentlyContinue
    if ($existing) {
        Remove-WebBinding -Name $SiteName -Protocol https -Port 443 -ErrorAction SilentlyContinue
    }

    New-WebBinding -Name $SiteName -Protocol https -Port 443 -HostHeader $HostHeader -SslFlags 1
    (Get-WebBinding -Name $SiteName -Protocol https -Port 443).AddSslCertificate($cert.Thumbprint, "my")
    Write-Log "IIS HTTPS binding updated"
}

# ── Step 6: Remove stale certs for this domain ───────────────────────────────

Get-ChildItem Cert:\LocalMachine\My |
    Where-Object { $_.Subject -like "*$HostHeader*" -and $_.Thumbprint -ne $cert.Thumbprint } |
    ForEach-Object {
        Write-Log "Removing old cert: $($_.Thumbprint) (expired $($_.NotAfter))"
        Remove-Item "Cert:\LocalMachine\My\$($_.Thumbprint)" -Force
    }

Remove-Item $TempDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Log ""
Write-Log "IIS certificate renewed successfully."
