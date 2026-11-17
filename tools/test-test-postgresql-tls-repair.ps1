#Requires -Version 7.5
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'repair-test-postgresql-tls.ps1')
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixture = Join-Path $repo ('artifacts/postgresql-tls-repair-test/' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$leaf = $null; $issuer = $null; $chain = $null
try {
    New-TestPostgresCertificatePair $fixture
    $leaf = [Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPemFile((Join-Path $fixture 'repository.crt'), (Join-Path $fixture 'repository.key'))
    $issuer = [Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadCertificateFromFile((Join-Path $fixture 'repository-root.crt'))
    if (!$leaf.HasPrivateKey) { throw 'Generated certificate and private key do not match.' }
    $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
    $chain.ChainPolicy.TrustMode = [Security.Cryptography.X509Certificates.X509ChainTrustMode]::CustomRootTrust
    [void]$chain.ChainPolicy.CustomTrustStore.Add($issuer)
    $chain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
    if (!$chain.Build($leaf)) { throw 'Generated server certificate does not chain to the emitted public root.' }
    if (!$leaf.MatchesHostname('localhost', $false, $false)) { throw 'Generated certificate does not match localhost.' }
    $before = (Get-FileHash (Join-Path $fixture 'repository.key')).Hash
    $refused = $false
    try { New-TestPostgresCertificatePair $fixture } catch { $refused = $true }
    if (!$refused -or (Get-FileHash (Join-Path $fixture 'repository.key')).Hash -ne $before) { throw 'Existing TLS files were overwritten.' }
    if ($IsWindows) {
        $security = [Security.AccessControl.DirectorySecurity]::new()
        $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $security.SetOwner($sid)
        $security.SetAccessRuleProtection($true, $false)
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
        $protected = Join-Path $fixture 'protected'
        New-ProtectedTlsDirectory $protected $security
        $refusedExistingDirectory = $false
        try { New-ProtectedTlsDirectory $protected $security } catch { $refusedExistingDirectory = $true }
        if (!$refusedExistingDirectory) { throw 'A pre-existing TLS directory was adopted.' }
        if (!(Get-Acl -LiteralPath $protected).AreAccessRulesProtected) { throw 'TLS directory ACL inheritance was not protected.' }
    }
    Write-Host 'TLS repair certificate chain, hostname, matching key, overwrite refusal, and protected directory tests passed.'
} finally {
    if ($null -ne $chain) { $chain.Dispose() }
    if ($null -ne $issuer) { $issuer.Dispose() }
    if ($null -ne $leaf) { $leaf.Dispose() }
    # Remove only the generated disposable test key, never a server key.
    Remove-Item -LiteralPath (Join-Path $fixture 'repository.key') -ErrorAction SilentlyContinue
}
