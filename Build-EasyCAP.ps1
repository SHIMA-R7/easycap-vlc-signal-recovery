param()
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The 64-bit .NET Framework C# compiler was not found.' }
Push-Location $PSScriptRoot
try {
    & $compiler /nologo /target:exe /platform:x64 /reference:System.Web.Extensions.dll /out:EasyCAP-Bridge.exe EasyCAP-Bridge.cs
    if ($LASTEXITCODE -ne 0) { throw 'Could not build EasyCAP-Bridge.exe. Close EasyCAP before building.' }
    & $compiler /nologo /target:winexe /platform:x64 /reference:System.Windows.Forms.dll /out:Open-EasyCAP.exe Open-EasyCAP.cs
    if ($LASTEXITCODE -ne 0) { throw 'Could not build Open-EasyCAP.exe. Close EasyCAP before building.' }
    Copy-Item -LiteralPath 'Open-EasyCAP.exe' -Destination 'Record-EasyCAP.exe' -Force
    Write-Host 'Built Open-EasyCAP.exe, Record-EasyCAP.exe, and EasyCAP-Bridge.exe.'
} finally { Pop-Location }
