# 贡献指南

请先运行 `./build.ps1`。涉及 hosts、任务、配置恢复的变更，应增加失败回滚与用户已有设置保留的测试。默认测试不得修改真实 hosts、注册生产任务或更改用户 Codex 配置。

优先保持单文件 WinExe、固定目标、回环监听、普通权限后台、严格证书校验。不要加入账号令牌收集、远程代理、任意目标转发或关闭证书检查。

提交 Issue 时提供工具版本、Windows/Codex 版本、按钮步骤、精简错误文字和预期行为。不要附上原始 `config.toml`、`auth.json`、代理订阅、二维码、设备标识或完整 doctor 输出。可遮盖私人信息后附界面截图。

版本更新应同步 `src/AssemblyInfo.cs`、manifest 和界面版本。`./package.ps1` 负责构建、测试和打包；新增源文件或必需文档时应同步打包清单。构建产物规则见 [构建文档](docs/releasing.md)，验收范围见 [验证记录](docs/verification.md)。
