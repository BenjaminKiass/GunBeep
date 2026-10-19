# สร้าง GunBeep.exe ใหม่จากซอร์สใน GunBeepApp\ (ได้เป็นไฟล์โปรแกรม + ไฟล์ประกอบ วางไว้ในโฟลเดอร์นี้)
# ต้องมี .NET 10 SDK:  winget install Microsoft.DotNet.SDK.10
# วิธีรัน: คลิกขวาไฟล์นี้ > Run with PowerShell  หรือ  powershell -ExecutionPolicy Bypass -File build.ps1

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$proj = Join-Path $here 'GunBeepApp\GunBeep.csproj'
$out  = Join-Path $here 'GunBeepApp\out'

Get-Process GunBeep -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish $proj -c Release -o $out
Copy-Item (Join-Path $out '*') $here -Recurse -Force
Write-Host "เสร็จแล้ว: $(Join-Path $here 'GunBeep.exe')"
