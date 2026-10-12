[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Path,
    [Parameter(Mandatory)][string[]] $ServiceIdentities,
    [Security.SecureString] $SharedKey
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Windows DPAPI is required.' }
if ($ServiceIdentities.Count -lt 1 -or $ServiceIdentities.Count -gt 2) { throw 'Provide the exact server and collector service identities.' }
if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw 'An absolute protected key path is required.' }
$webhookKeyPath = [IO.Path]::GetFullPath($Path)
$webhookKeyDirectory = [IO.Path]::GetDirectoryName($webhookKeyPath)
if (Test-Path -LiteralPath $webhookKeyDirectory) { throw 'Choose a new dedicated key directory; existing directories and keys are never replaced.' }
$webhookParent = [IO.DirectoryInfo]::new([IO.Path]::GetDirectoryName($webhookKeyDirectory))
if (-not $webhookParent.Exists) { throw 'The parent directory must already exist.' }
for ($webhookAncestor = $webhookParent; $null -ne $webhookAncestor; $webhookAncestor = $webhookAncestor.Parent) {
    if ($webhookAncestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse-point key paths are not supported.' }
}
$webhookOwner = [Security.Principal.WindowsIdentity]::GetCurrent().User
$webhookAdministrators = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$webhookSystem = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$webhookReaders = @()
foreach ($webhookName in $ServiceIdentities) {
    $webhookSid = ([Security.Principal.NTAccount]::new($webhookName)).Translate([Security.Principal.SecurityIdentifier])
    if ($webhookSid.Value -in @('S-1-1-0','S-1-5-11','S-1-5-32-545','S-1-5-4')) { throw 'A broad user group is not a service identity.' }
    $webhookReaders += $webhookSid
}
if ($null -eq $SharedKey) { $SharedKey = Read-Host 'Shared 32-byte webhook key as Base64 (hidden)' -AsSecureString }
if ($SharedKey.Length -ne 44) { throw 'The shared key must be Base64 for exactly 32 random bytes.' }
$webhookCharacters = New-Object char[] 44
$webhookPointer = [IntPtr]::Zero
$webhookBytes = $null
try {
    $webhookPointer = [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($SharedKey)
    for ($webhookIndex = 0; $webhookIndex -lt 44; $webhookIndex++) {
        $webhookCharacters[$webhookIndex] = [char][Runtime.InteropServices.Marshal]::ReadInt16($webhookPointer, $webhookIndex * 2)
    }
    try { $webhookBytes = [Convert]::FromBase64CharArray($webhookCharacters, 0, 44) }
    catch { throw 'The shared key must be valid Base64 for exactly 32 random bytes.' }
    if ($webhookBytes.Length -ne 32) { throw 'The shared key must decode to exactly 32 bytes.' }
    New-Item -ItemType Directory -Path $webhookKeyDirectory -ErrorAction Stop | Out-Null
    $webhookDirectoryAcl = [Security.AccessControl.DirectorySecurity]::new()
    $webhookDirectoryAcl.SetAccessRuleProtection($true, $false)
    foreach ($webhookSid in @($webhookOwner, $webhookAdministrators, $webhookSystem)) {
        $webhookDirectoryAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($webhookSid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    foreach ($webhookSid in $webhookReaders) {
        $webhookDirectoryAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($webhookSid, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    Set-Acl -LiteralPath $webhookKeyDirectory -AclObject $webhookDirectoryAcl
    $webhookProtected = [Security.Cryptography.ProtectedData]::Protect($webhookBytes, $null, [Security.Cryptography.DataProtectionScope]::LocalMachine)
    try {
        $webhookStream = [IO.File]::Open($webhookKeyPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $webhookStream.Write($webhookProtected, 0, $webhookProtected.Length) } finally { $webhookStream.Dispose() }
        $webhookFileAcl = Get-Acl -LiteralPath $webhookKeyPath
        $webhookFileAcl.SetAccessRuleProtection($true, $true)
        Set-Acl -LiteralPath $webhookKeyPath -AclObject $webhookFileAcl
    } finally { [Array]::Clear($webhookProtected, 0, $webhookProtected.Length) }
} finally {
    if ($null -ne $webhookBytes) { [Array]::Clear($webhookBytes, 0, $webhookBytes.Length) }
    [Array]::Clear($webhookCharacters, 0, $webhookCharacters.Length)
    if ($webhookPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($webhookPointer) }
}
Write-Output 'Webhook signing key protected with restricted service and provisioning-owner access.'
