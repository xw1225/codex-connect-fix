# Codex 连接修复

**Codex 连接修复是一款 Windows 图形化修复工具，主要用于解决两个问题：无法添加远程控制设备，以及每次使用 Codex 时反复“重新连接 5 次”。**

- **无法添加远程控制设备**：在使用本地代理的环境下，Codex 无法发起配对或添加手机、另一台电脑。工具提供一键部署后台转发器，帮助恢复远程设备的添加与连接。
- **每次使用 Codex 都重新连接 5 次**：针对 Codex 未正确使用系统代理的情况，提供一键修复，并检测当前 HTTP 和 WebSocket 连接状态。

两项修复放在同一个中文界面中，同时提供**一键卸载转发器**和**还原重连修复前配置**，方便撤销相应修改。解压后双击 `CodexConnectFix.exe` 即可使用，无需安装 Python、Node.js 或手动运行 PowerShell。

**本项目是社区兼容工具，与 OpenAI 无隶属关系。** 请先更新官方 Codex 桌面应用；只有对应问题仍然存在时才使用这里的修复。功能与版本可能变化，参见 [官方远程连接说明](https://learn.chatgpt.com/docs/remote-connections) 和 [官方更新记录](https://learn.chatgpt.com/docs/changelog)。

![中文版界面](docs/interface.png)

| 界面区域 | 按钮 | 作用 |
| --- | --- | --- |
| 解决无法添加远程控制设备问题 | 一键部署转发器 | 检测本机 HTTP 代理，部署无窗口后台转发器，配置登录启动和故障恢复 |
| 同一区域 | 一键卸载转发器 | 停止并删除启动任务，撤销本工具添加的 hosts 映射，保留程序与备份 |
| 解决每次使用codex重新连接5次问题 | 一键修复重连 | 私人备份配置，通过内置 Codex CLI 启用 `respect_system_proxy`，显示筛选后的自检结果 |
| 同一区域 | 还原重连修复前配置 | 只恢复这项代理开关的原值，保留其他配置及后来新增的内容 |

“重连 5 次”是症状，可能来自不同原因。这里集成的是**系统代理未被使用时的兼容处理与诊断**，不保证解决所有重复重连。开关已启用时不会重复修改，也不会通过更换模型服务商或关闭 WebSocket 来掩盖问题。

## 快速使用

1. 从本仓库 Releases 下载 `codex-connect-fix-v1.0.2-windows.zip`，解压到普通文件夹。
2. 打开代理软件并启用系统代理，双击 `CodexConnectFix.exe`。同一桌面用户即使具有管理员权限也能诊断；使用另一账户运行时会阻止对用户配置的操作。Codex CLI 始终以普通权限运行。
3. 窗口会自动检测，在两个区域直接显示当前状态。需要手动指定端口时展开“代理设置”，填写代理软件的 **HTTP / 混合端口**，例如 `7890`、`15236` 或 `127.0.0.1:7890`。留空自动检测。
4. 部署或卸载转发器时，阅读影响说明并接受 Windows 管理员确认。重连配置的修改无需管理员权限。
5. 完成后彻底退出并重新打开 Codex。远程设备仍需在 Codex 的连接设置里配对；网络检测通过不代表账号已经完成配对。

如未提供预编译 Release，可按下方“从源码构建”生成可执行文件。当前社区构建没有代码签名，Windows 可能显示未知发布者；也可自行检查源码并构建。

## 使用条件与影响

- 面向 Windows 10/11，需 .NET Framework 4.8。发布前主要在 Windows x64 环境验证。
- 需要官方 Codex 桌面应用及其内置 CLI。重连按钮只支持仍提供 `respect_system_proxy` 功能开关的版本；不支持时会停止并说明原因。
- 转发器需要本机、无认证、支持 HTTP CONNECT 的代理。兼容常见 HTTP / 混合端口；SOCKS 专用端口、远端代理、PAC 自动脚本和带认证代理不在当前支持范围内。
- 代理软件必须把 `chatgpt.com` 送往可用代理线路。若规则把该域名直连，并通过系统 hosts 解析目的地，可能出现回环；安装会在 hosts 修改后再次检测，失败则回退。
- 部署会为 **所有本机应用对 `chatgpt.com` 的直连** 添加 `127.0.0.2` hosts 映射，不仅影响 Codex。卸载会撤销本工具的映射；不会删除其他 hosts 条目。
- 关闭 Veee 或其他代理软件时，转发器继续运行，但经过它的连接不可用。重新打开代理、保持端口一致后，新的连接可自动恢复。若端口改变，可填写新端口后再次部署。
- 后台以部署用户的普通权限运行，登录时启动，每分钟检查恢复，已有实例时跳过。关闭工具或 PowerShell 窗口不影响后台；注销、关机或睡眠时电脑无法提供远程访问。
- 一台电脑只部署一份。已部署且运行正常时无需换装。工具可识别并卸载 `CodexRemoteProxyFix` 和 `CodexRemoteRelayHelper` 两种受支持的部署；未知或被修改的任务/映射会停止自动操作。

启动时自动检测，也可点“刷新检测”。转发器通过任务、hosts、监听进程和加密通路检查后，显示“转发器运行正常”；详情中列出实际检测到的任务。两种受支持的部署都使用“一键卸载转发器”，移除前保存备份并保留程序文件。`CodexRemoteProxyFix` 的卸载备份位于 `%ProgramData%\CodexConnectionHelper-UninstallBackups\`。重连区分别展示代理开关和当前网络检查；已启用时无需重复修复，没有本工具的备份时“还原重连修复前配置”不可用。该还原只恢复系统代理开关，保留其他设置及后续修改。端口输入及诊断详情默认折叠，窄窗口可滚动。

## 还原与备份

两个区域独立管理：卸载转发器不会还原重连配置，重连还原也不会停止转发器。

| 内容 | 默认位置 |
| --- | --- |
| 已部署程序 | `%ProgramFiles%\CodexRemoteRelayHelper\CodexConnectionHelper.exe` |
| 转发配置、hosts / 任务备份 | `%ProgramData%\CodexRemoteRelayHelper\` |
| 后台简要日志 | `%ProgramData%\CodexRemoteRelayHelper\logs\relay.log` |
| 计划任务名称 | `CodexRemoteRelayHelper` |
| 当前用户的重连备份 | `%LOCALAPPDATA%\CodexConnectionHelper\reconnect-backups\` |

“一键卸载转发器”撤销系统中的转发配置并停止后台，**保留程序和备份供核查**。如需清理磁盘，可在卸载成功后自行删除上述两个专用目录。

“还原重连修复前配置”恢复代理开关的行为，不覆盖整份 TOML。原来没有显式配置的开关，恢复后可能留下显式 `false`；若配置文件已被用户删除，则不会重新创建。原始完整备份仍保留在本机，限当前用户、管理员及 SYSTEM 访问。原始 TOML 可能包含私人信息，**请勿将备份、配置、账号文件或原始自检输出上传到 GitHub Issue**。

## 工作原理与隐私

转发路径：`Codex / 其他直连应用 → 127.0.0.2:443 → 本机 HTTP CONNECT 代理 → chatgpt.com:443`。

后台只允许这个固定目标，只监听回环地址，原样转发 TLS 字节。不会安装根证书、替换服务器证书或解密 TLS。检测使用正常的主机名与证书校验，不提供关闭证书验证的选项。

重连修复调用桌面版内置 CLI 的 `features list`、`features enable/disable respect_system_proxy`。诊断调用 `doctor --json`；官方 CLI 会按自身逻辑使用登录状态并探测服务。本工具只展示配置、凭据、HTTP、WebSocket 四项状态，不显示其原始详情、账号或令牌；不设遥测或上传服务。后台日志仅记录时间、就绪状态和异常类型，并限制大小。

## 从源码构建

依赖 Windows 自带 .NET Framework C# 编译器和 PowerShell。无需 NuGet、npm 或 Python 包。

```powershell
.\build.ps1
.\package.ps1
```

`build.ps1` 编译并执行离线测试，成功后在 `build` 生成程序。测试仅使用临时配置、临时 hosts 文件和本机随机测试端口，不修改真实 hosts、系统代理或当前 Codex 配置。

`package.ps1` 会重新构建与测试，按明确文件清单生成 `dist` 下的用户版 ZIP、源码 ZIP、SHA-256 校验文件。不会打包 `build/test-data`、私人配置或本机日志。最终 EXE 编译为 WinExe，不需要控制台。

可选的本机集成检查：

```powershell
# 使用独立 CODEX_HOME 测试真实 CLI，不改变正在使用的配置
.\build\LiveChecks.exe cli
# 创建并清理一个唯一名称的普通用户测试任务，监听随机本机端口
.\build\LiveChecks.exe scheduler
# 通过本机 HTTP 代理验证真实 TLS 通道；不会修改 hosts
.\build\LiveChecks.exe tls 127.0.0.1:7890
# 只展示筛选后的当前 Codex 自检状态
.\build\LiveChecks.exe doctor
# 渲染两区域界面用于检查布局
.\build\UiSnapshot.exe .\build\interface.png
```

测试覆盖 hosts 编码与其他条目保留、异常回滚、重复部署、跨用户重装、配置恢复、并发锁、CONNECT 拒绝、后台无控制台、代理中断恢复。发布前的实际验证与尚未覆盖场景见 [验证记录](docs/verification.md)。

## 项目资料

项目采用 [MIT 许可证](LICENSE)。[English README](README.en.md) · [贡献指南](CONTRIBUTING.md) · [安全说明](SECURITY.md) · [软件架构](docs/design.md) · [验证记录](docs/verification.md)
