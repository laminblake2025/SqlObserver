[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
if($env:COMPUTERNAME -ne 'WIN-QNGOV5GDM24'){throw 'Restricted to the disposable lab VM.'}
# Install the Replication feature from the existing SQL 2025 media first if absent.
# After installation restart SQLSERVERAGENT to load its replication subsystems.
$snapshotPath='C:\Program Files\Microsoft SQL Server\MSSQL17.MSSQLSERVER\MSSQL\ReplData'
New-Item -ItemType Directory -Path $snapshotPath -Force | Out-Null
$acl=Get-Acl -LiteralPath $snapshotPath
$rule=[System.Security.AccessControl.FileSystemAccessRule]::new('NT SERVICE\SQLSERVERAGENT','Modify','ContainerInherit,ObjectInherit','None','Allow')
$acl.SetAccessRule($rule)
Set-Acl -LiteralPath $snapshotPath -AclObject $acl
Start-Service SQLSERVERAGENT
$sqlcmd='C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\180\Tools\Binn\SQLCMD.EXE'
& $sqlcmd -S tcp:WIN-QNGOV5GDM24,1433 -E -N -b -t 180 -W -i (Join-Path $PSScriptRoot 'setup-replication.sql')
if($LASTEXITCODE -ne 0){throw 'Replication setup failed; inspect SQL output.'}
