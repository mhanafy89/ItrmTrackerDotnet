<#
  Installs / removes the ITRM Tracker (ASP.NET Core) as a Windows Service. Run from an elevated PowerShell
  after publishing:  dotnet publish Web\ItrmTracker.Web.csproj -c Release -o C:\Apps\itrm-tracker

  Usage:  .\deploy\install-windows-service.ps1 -AppPath C:\Apps\itrm-tracker -Urls http://0.0.0.0:5000
          .\deploy\install-windows-service.ps1 -Remove
  Configuration (connection string, seed admin password) is read from environment variables or
  appsettings.Production.json next to the executable – see Docs\CONFIGURATION.md.
#>
param(
  [string]$AppPath = "C:\Apps\itrm-tracker",
  [string]$ServiceName = "ITRM-Tracker",
  [string]$Urls = "http://0.0.0.0:5000",
  [string]$Account = "NT AUTHORITY\NetworkService",
  [switch]$Remove
)
$ErrorActionPreference = "Stop"

if ($Remove) {
  if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service $ServiceName -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
  }
  Write-Host "Removed service $ServiceName"
  return
}

$exe = Join-Path (Resolve-Path $AppPath).Path "ItrmTracker.Web.exe"
if (-not (Test-Path $exe)) { throw "ItrmTracker.Web.exe not found in $AppPath (publish the Web project first)." }

New-Service -Name $ServiceName -BinaryPathName "`"$exe`" --urls $Urls --contentRoot `"$AppPath`"" `
  -DisplayName "ITRM Tracker" -Description "ITRM Business Request Tracker (ASP.NET Core)" -StartupType Automatic | Out-Null
sc.exe config $ServiceName obj= $Account | Out-Null
# Restart automatically after a crash (1 minute delay, like the legacy scheduled task).
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
Start-Service $ServiceName
Start-Sleep -Seconds 5
Write-Host "Service '$ServiceName' started. Check: curl.exe $($Urls.Replace('0.0.0.0','127.0.0.1'))/healthz"
