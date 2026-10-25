param([switch]$TestsOnly)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$build = Join-Path $root 'build'
New-Item -ItemType Directory -Path $build -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler was not found.' }
$references = @('/r:System.Core.dll','/r:System.Runtime.Serialization.dll','/r:Microsoft.CSharp.dll','/r:System.Windows.Forms.dll','/r:System.Drawing.dll')
& $compiler /nologo /target:exe /utf8output @references ('/out:' + (Join-Path $build 'CoreTests.exe')) (Join-Path $root 'src\Core.cs') (Join-Path $root 'tests\CoreTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Core test compilation failed.' }
& (Join-Path $build 'CoreTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
& $compiler /nologo /target:exe /utf8output @references ('/out:' + (Join-Path $build 'InstallationTests.exe')) (Join-Path $root 'src\Core.cs') (Join-Path $root 'src\Installation.cs') (Join-Path $root 'tests\InstallationTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Installation test compilation failed.' }
& (Join-Path $build 'InstallationTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Installation tests failed.' }
& $compiler /nologo /target:exe /utf8output @references ('/out:' + (Join-Path $build 'ReconnectTests.exe')) (Join-Path $root 'src\Core.cs') (Join-Path $root 'src\Installation.cs') (Join-Path $root 'src\Reconnect.cs') (Join-Path $root 'src\CliProcess.cs') (Join-Path $root 'tests\ReconnectTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Reconnect test compilation failed.' }
& (Join-Path $build 'ReconnectTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Reconnect tests failed.' }
& $compiler /nologo /target:exe /utf8output @references ('/out:' + (Join-Path $build 'DiagnosticsTests.exe')) (Join-Path $root 'src\Core.cs') (Join-Path $root 'src\Installation.cs') (Join-Path $root 'src\Network.cs') (Join-Path $root 'src\WindowsSystem.cs') (Join-Path $root 'src\LegacyRemoval.cs') (Join-Path $root 'src\Reconnect.cs') (Join-Path $root 'src\CliProcess.cs') (Join-Path $root 'src\Diagnostics.cs') (Join-Path $root 'tests\DiagnosticsTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Diagnostics test compilation failed.' }
& (Join-Path $build 'DiagnosticsTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Diagnostics tests failed.' }
& $compiler /nologo /target:exe /utf8output @references ('/out:' + (Join-Path $build 'LegacyRemovalTests.exe')) (Join-Path $root 'src\Core.cs') (Join-Path $root 'src\Installation.cs') (Join-Path $root 'src\Network.cs') (Join-Path $root 'src\WindowsSystem.cs') (Join-Path $root 'src\LegacyRemoval.cs') (Join-Path $root 'tests\LegacyRemovalTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Removal test compilation failed.' }
& (Join-Path $build 'LegacyRemovalTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Removal tests failed.' }
& $compiler /nologo /target:exe /utf8output @references ('/out:' + (Join-Path $build 'PrivilegeProbe.exe')) (Join-Path $root 'tests\PrivilegeProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Privilege probe compilation failed.' }
& $compiler /nologo /target:exe /utf8output @references ('/out:' + (Join-Path $build 'PrivilegeTests.exe')) (Join-Path $root 'src\Core.cs') (Join-Path $root 'src\Installation.cs') (Join-Path $root 'src\Reconnect.cs') (Join-Path $root 'src\CliProcess.cs') (Join-Path $root 'tests\PrivilegeTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Privilege test compilation failed.' }
& (Join-Path $build 'PrivilegeTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Privilege tests failed.' }
& $compiler /nologo /target:winexe /utf8output @references ('/out:' + (Join-Path $build 'RelayHarness.exe')) (Join-Path $root 'src\Core.cs') (Join-Path $root 'src\Network.cs') (Join-Path $root 'tests\RelayHarness.cs')
if ($LASTEXITCODE -ne 0) { throw 'Relay harness compilation failed.' }
& $compiler /nologo /target:exe /utf8output @references ('/out:' + (Join-Path $build 'NetworkTests.exe')) (Join-Path $root 'src\Core.cs') (Join-Path $root 'src\Network.cs') (Join-Path $root 'tests\NetworkTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Network test compilation failed.' }
& (Join-Path $build 'NetworkTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Network tests failed.' }
if (-not $TestsOnly) {
    & $compiler /nologo /target:winexe /utf8output @references ('/out:' + (Join-Path $build 'LegacyRemovalProbe.exe')) (Join-Path $root 'tests\LegacyRemovalProbe.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Removal probe compilation failed.' }
    $sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' -File | ForEach-Object FullName)
    & $compiler /nologo /target:winexe /optimize+ /utf8output /main:RemoteHelper.Program @references ('/win32manifest:' + (Join-Path $root 'src\app.manifest')) ('/out:' + (Join-Path $build 'CodexConnectFix.exe')) @sources
    if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
    & $compiler /nologo /target:exe /utf8output @references ('/win32manifest:' + (Join-Path $root 'src\app.manifest')) ('/r:' + (Join-Path $build 'CodexConnectFix.exe')) ('/out:' + (Join-Path $build 'UiSnapshot.exe')) (Join-Path $root 'tests\UiSnapshot.cs')
    if ($LASTEXITCODE -ne 0) { throw 'UI check compilation failed.' }
    & $compiler /nologo /target:exe /utf8output @references ('/r:' + (Join-Path $build 'CodexConnectFix.exe')) ('/out:' + (Join-Path $build 'LiveChecks.exe')) (Join-Path $root 'tests\LiveChecks.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Optional live-check compilation failed.' }
}
