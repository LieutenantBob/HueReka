$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot 'dist') -Force | Out-Null
$references = '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Web.Extensions.dll', '/reference:System.Security.dll'
$sources = 'HueReka.cs', 'GlassUi.cs', 'MainWindow.cs', 'MainWindow.Actions.cs', 'Tests.cs' | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /target:winexe /optimize+ /win32icon:"$PSScriptRoot\app.ico" /out:"$PSScriptRoot\dist\HueReka!.exe" $references $sources
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Write-Host "Built $PSScriptRoot\dist\HueReka!.exe"
& $compiler /nologo /target:exe /main:HueReka.Cli /optimize+ /out:"$PSScriptRoot\dist\huereka.exe" $references $sources "$PSScriptRoot\Cli.cs"
if ($LASTEXITCODE -ne 0) { throw 'CLI compilation failed.' }
Write-Host "Built $PSScriptRoot\dist\huereka.exe"
