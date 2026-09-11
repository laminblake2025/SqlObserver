[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Path,
    [Parameter(Mandatory)][string[]] $ServiceIdentities
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Windows DPAPI is required.' }
if ($ServiceIdentities.Count -lt 1 -or $ServiceIdentities.Count -gt 2) { throw 'Provide the exact server and collector service identities.' }
$keyPath = [IO.Path]::GetFullPath($Path)
if (Test-Path -LiteralPath $keyPath) { throw 'An existing protection key must not be replaced. Retain it for captured history.' }
$keyDirectory = [IO.Path]::GetDirectoryName($keyPath)
if (-not (Test-Path -LiteralPath $keyDirectory)) { New-Item -ItemType Directory -Path $keyDirectory | Out-Null }
if ((Get-Item -LiteralPath $keyDirectory).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'A reparse-point key directory is not supported.' }
$identities = @([Security.Principal.SecurityIdentifier]::new('S-1-5-18'), [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
foreach ($name in $ServiceIdentities) {
    $sid = ([Security.Principal.NTAccount]::new($name)).Translate([Security.Principal.SecurityIdentifier])
    if ($sid.Value -in @('S-1-1-0','S-1-5-11','S-1-5-32-545','S-1-5-4')) { throw 'A broad user group is not a service identity.' }
    $identities += $sid
}
$directoryAcl = [Security.AccessControl.DirectorySecurity]::new()
$directoryAcl.SetAccessRuleProtection($true,$false)
foreach ($sid in $identities) {
    $rights = if ($sid.Value -in @('S-1-5-18','S-1-5-32-544')) { 'FullControl' } else { 'ReadAndExecute' }
    $directoryAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,$rights,'ContainerInherit,ObjectInherit','None','Allow'))
}
Set-Acl -LiteralPath $keyDirectory -AclObject $directoryAcl
Add-Type -AssemblyName System.Security
$keyBytes = New-Object byte[] 32
$random = [Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $random.GetBytes($keyBytes)
    $protected = [Security.Cryptography.ProtectedData]::Protect($keyBytes,$null,[Security.Cryptography.DataProtectionScope]::LocalMachine)
    $stream = [IO.File]::Open($keyPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $stream.Write($protected,0,$protected.Length) } finally { $stream.Dispose() }
    $fileAcl = Get-Acl -LiteralPath $keyPath
    $fileAcl.SetAccessRuleProtection($true,$true)
    Set-Acl -LiteralPath $keyPath -AclObject $fileAcl
} finally { [Array]::Clear($keyBytes,0,$keyBytes.Length); $random.Dispose() }
Write-Output 'Machine-protected live-activity key provisioned with restricted service access.'
