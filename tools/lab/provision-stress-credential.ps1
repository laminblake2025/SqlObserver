# Run interactively as the same Windows operator that will run the harness.
# The repository bootstrap credential is used only by BEGIN READ ONLY evidence queries.
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
if($env:COMPUTERNAME -ne 'WIN-QNGOV5GDM24'){throw 'Restricted to the disposable lab VM.'}
$directory='C:\SqlObserverLab\ProtectedStress'
$path=Join-Path $directory 'repository.dpapi.json'
if(Test-Path $path){throw 'A protected credential already exists; this script does not replace it.'}
[void][IO.Directory]::CreateDirectory($directory)
if((Get-Item $directory).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Credential directory cannot be a reparse point.'}
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User
$acl=New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true,$false)
foreach($identity in @($sid,[Security.Principal.SecurityIdentifier]::new('S-1-5-18'))){
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
}
Set-Acl -LiteralPath $directory -AclObject $acl
$secret=Read-Host 'PostgreSQL bootstrap password (hidden)' -AsSecureString
Add-Type -AssemblyName System.Security
$pointer=[IntPtr]::Zero;$bytes=$null
try {
    $pointer=[Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($secret)
    $bytes=New-Object byte[] ($secret.Length*2)
    [Runtime.InteropServices.Marshal]::Copy($pointer,$bytes,0,$bytes.Length)
    $protected=[Security.Cryptography.ProtectedData]::Protect($bytes,$null,[Security.Cryptography.DataProtectionScope]::LocalMachine)
    $record=@{schemaVersion=1;userName='sqlobserver_bootstrap';operatorSid=$sid.Value;protection='WindowsDpapiLocalMachine';ciphertext=[Convert]::ToBase64String($protected)}
    [IO.File]::WriteAllText($path,($record|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
} finally {
    if($bytes){[Array]::Clear($bytes,0,$bytes.Length)}
    if($pointer -ne [IntPtr]::Zero){[Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($pointer)}
    $secret.Dispose()
}
Write-Output "Protected credential provisioned at $path for $sid."
