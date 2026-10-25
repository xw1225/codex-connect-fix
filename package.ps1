param([string]$Version = '')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
& (Join-Path $root 'build.ps1')
$exe = Join-Path $root 'build\CodexConnectFix.exe'
$builtVersion = [Reflection.AssemblyName]::GetAssemblyName($exe).Version.ToString(3)
if ($Version -and $Version -ne $builtVersion) { throw 'Release tag and executable version do not match.' }
$Version = $builtVersion
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem

# Explicit allowlists: never package a working directory, logs or private config.
$sourceFiles = @(
    '.gitignore','.gitattributes','README.md','README.en.md','LICENSE','CONTRIBUTING.md','SECURITY.md','QUICKSTART.zh-CN.txt','build.ps1','package.ps1',
    '.github\workflows\windows.yml','.github\ISSUE_TEMPLATE\bug_report.yml',
    'src\AssemblyInfo.cs','src\Core.cs','src\Installation.cs','src\LegacyRemoval.cs','src\Network.cs','src\Program.cs','src\MainWindow.cs','src\Diagnostics.cs','src\CliProcess.cs','src\Reconnect.cs','src\WindowsSystem.cs','src\app.manifest',
    'tests\CoreTests.cs','tests\InstallationTests.cs','tests\LegacyRemovalTests.cs','tests\LegacyRemovalProbe.cs','tests\ReconnectTests.cs','tests\DiagnosticsTests.cs','tests\PrivilegeProbe.cs','tests\PrivilegeTests.cs','tests\NetworkTests.cs','tests\RelayHarness.cs','tests\LiveChecks.cs','tests\UiSnapshot.cs',
    'docs\design.md','docs\interface.png','docs\releasing.md','docs\verification.md'
)
$sourceMap = [ordered]@{}
foreach ($file in $sourceFiles) { $sourceMap[$file.Replace('\','/')] = Join-Path $root $file }
$runtimeMap = [ordered]@{ 'CodexConnectFix.exe' = $exe; '使用说明.txt' = (Join-Path $root 'QUICKSTART.zh-CN.txt') }
foreach ($file in @('README.md','README.en.md','LICENSE','SECURITY.md','CONTRIBUTING.md','docs\design.md','docs\interface.png','docs\releasing.md','docs\verification.md')) {
    $runtimeMap[$file.Replace('\','/')] = Join-Path $root $file
}
$exeHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
$internalHashes = Join-Path $dist 'EXE-SHA256SUMS.txt'
[IO.File]::WriteAllText($internalHashes,"$exeHash  CodexConnectFix.exe`n",[Text.UTF8Encoding]::new($false))
$runtimeMap['SHA256SUMS.txt'] = $internalHashes

function Write-Package([string]$target, [Collections.IDictionary]$entries) {
    foreach ($entry in $entries.GetEnumerator()) {
        if (-not (Test-Path -LiteralPath $entry.Value -PathType Leaf)) { throw "Missing package file: $($entry.Key)" }
    }
    $temporary = $target + '.new-' + [Guid]::NewGuid().ToString('N')
    try {
        $zip = [IO.Compression.ZipFile]::Open($temporary,[IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($entry in $entries.GetEnumerator()) {
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$entry.Value,$entry.Key,[IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        } finally { $zip.Dispose() }
        if (Test-Path -LiteralPath $target) {
            # PowerShell 7 coerces $null to an empty backup path for this overload.
            $previous = $target + '.previous-' + [Guid]::NewGuid().ToString('N')
            [IO.File]::Replace($temporary,$target,$previous)
            Remove-Item -LiteralPath $previous
        } else { [IO.File]::Move($temporary,$target) }
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
$runtimeZip = Join-Path $dist "codex-connect-fix-v$Version-windows.zip"
$sourceZip = Join-Path $dist "codex-connect-fix-v$Version-source.zip"
Write-Package $runtimeZip $runtimeMap
Write-Package $sourceZip $sourceMap
$checksums = foreach ($file in @($runtimeZip,$sourceZip)) { ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()) + '  ' + [IO.Path]::GetFileName($file) }
[IO.File]::WriteAllText((Join-Path $dist 'SHA256SUMS.txt'),($checksums -join "`n") + "`n",[Text.UTF8Encoding]::new($false))
$notes = "# Codex 连接修复 v$Version`n`n" + @'
面向 Windows 的中文图形化工具，主要用于解决无法添加远程控制设备，以及每次使用 Codex 时反复“重新连接 5 次”的问题。

- 一键部署或卸载后台转发器，支持识别已有的两种部署方式。
- 一键修复系统代理设置，提供“还原重连修复前配置”。
- 自动检测转发器、系统代理、HTTP 和 WebSocket 状态。
- 支持高 DPI 布局，代理端口设置和诊断详情默认折叠。

`windows.zip` 包含可执行程序与使用说明；`source.zip` 包含源代码、测试和构建脚本。对应的 SHA-256 校验值位于 `SHA256SUMS.txt`。

运行条件：Windows 10/11、.NET Framework 4.8、Codex 桌面应用及可用的本机 HTTP CONNECT 代理。转发器会修改 chatgpt.com 的系统 hosts 映射，可通过卸载撤销。重连修复适用于系统代理未被正确使用的情况。

项目为独立社区工具，二进制未进行代码签名。使用条件、配置备份与还原说明见 README；测试范围和未覆盖场景见 docs/verification.md。
'@
[IO.File]::WriteAllText((Join-Path $dist 'release-notes.md'),$notes,[Text.UTF8Encoding]::new($false))
Write-Output ('Created: ' + $runtimeZip)
Write-Output ('Created: ' + $sourceZip)
Write-Output ('Checksums: ' + (Join-Path $dist 'SHA256SUMS.txt'))
