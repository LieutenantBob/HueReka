$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot 'dist') -Force | Out-Null
& $compiler /nologo /target:winexe /optimize+ /out:"$PSScriptRoot\dist\HueReka!.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Security.dll "$PSScriptRoot\HueReka.cs"
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Write-Host "Built $PSScriptRoot\dist\HueReka!.exe"
& $compiler /nologo /target:exe /main:HueReka.Cli /optimize+ /out:"$PSScriptRoot\dist\huereka.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Security.dll "$PSScriptRoot\HueReka.cs" "$PSScriptRoot\Cli.cs"
if ($LASTEXITCODE -ne 0) { throw 'CLI compilation failed.' }
Write-Host "Built $PSScriptRoot\dist\huereka.exe"
