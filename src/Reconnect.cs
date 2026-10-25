using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
namespace RemoteHelper {
    public interface ICodexCli { bool ProxyFlag(); void SetProxyFlag(bool value); string Diagnose(); }
    public sealed class CodexCli : ICodexCli {
        private readonly string executable;
        private readonly string configHome;
        private readonly int timeoutMilliseconds;
        public CodexCli(string executable, string configHome = null, int timeoutMilliseconds = 45000) {
            if (timeoutMilliseconds < 1) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            this.executable = executable; this.configHome = configHome; this.timeoutMilliseconds = timeoutMilliseconds;
        }
        public static string Find() {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"OpenAI\Codex\bin");
            foreach (var process in Process.GetProcessesByName("codex")) {
                using (process) { try {
                    string file = process.MainModule.FileName;
                    if (file.StartsWith(root + Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) return file;
                } catch { } }
            }
            if (Directory.Exists(root)) {
                string file = Directory.GetDirectories(root).Select(d => Path.Combine(d,"codex.exe")).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (file != null) return file;
            }
            throw new InvalidOperationException("没有找到桌面版内置 Codex。请安装并打开官方桌面应用后重试。");
        }
        private static async Task<string> ReadBounded(StreamReader reader) {
            var result = new StringBuilder(); char[] buffer = new char[4096]; int count;
            while ((count = await reader.ReadAsync(buffer,0,buffer.Length).ConfigureAwait(false)) != 0) {
                if (result.Length + count > 1048576) throw new IOException("自检输出超出限制"); result.Append(buffer,0,count);
            }
            return result.ToString();
        }
        private string Run(string arguments, bool requireSuccess) {
            var info = new ProcessStartInfo(executable,arguments) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            if (configHome != null) info.EnvironmentVariables["CODEX_HOME"] = Path.GetFullPath(configHome);
            using (var child = CliProcess.Start(info)) {
                var watch = Stopwatch.StartNew();
                var output = ReadBounded(child.Output); var errors = ReadBounded(child.Errors);
                var drain = Task.WhenAll(output,errors);
                if (!child.WaitForExit(timeoutMilliseconds) || Task.WaitAny(new Task[] {drain},(int)Math.Max(0,timeoutMilliseconds-watch.ElapsedMilliseconds)) < 0) {
                    try { child.Kill(); } catch { }
                    throw new TimeoutException("Codex 自检超时，已停止本次自检及其子进程。请检查代理或应用状态。");
                }
                string text = output.GetAwaiter().GetResult(); errors.GetAwaiter().GetResult();
                if (requireSuccess && child.ExitCode != 0) throw new InvalidOperationException("Codex 命令未成功。可能是版本不兼容或配置无法解析；未显示可能含私人信息的原始输出。");
                return text;
            }
        }
        public bool ProxyFlag() {
            string output = Run("features list",true);
            Match match = Regex.Match(output,@"(?m)^respect_system_proxy\s+(.+?)\s+(true|false)\s*$");
            if (!match.Success || match.Groups[1].Value.IndexOf("removed",StringComparison.OrdinalIgnoreCase) >= 0)
                throw new InvalidOperationException("此 Codex 版本不提供 respect_system_proxy 开关，不能套用本修复。请使用官方版本对应的网络设置。");
            return match.Groups[2].Value == "true";
        }
        public void SetProxyFlag(bool value) { Run("features " + (value ? "enable" : "disable") + " respect_system_proxy",true); }
        [DataContract] private sealed class Report { [DataMember(Name="checks")] public Dictionary<string,Check> Checks { get; set; } }
        [DataContract] private sealed class Check { [DataMember(Name="status")] public string Status { get; set; } }
        public string Diagnose() {
            return FormatDiagnostic(Run("doctor --json",false));
        }
        public static string FormatDiagnostic(string output) {
            try {
                var serializer = new DataContractJsonSerializer(typeof(Report),new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(output))) {
                    var report = (Report)serializer.ReadObject(stream);
                    if (report == null || report.Checks == null) throw new InvalidDataException();
                    var text = new StringBuilder("Codex 自检（不包含账号、令牌或原始日志）：\r\n");
                    string[] keys = { "config.load", "auth.credentials", "network.provider_reachability", "network.websocket_reachability" };
                    string[] names = { "配置读取", "登录凭据", "HTTP 通道", "WebSocket 通道" };
                    for (int index = 0; index < keys.Length; index++) {
                        Check check; string state = report.Checks.TryGetValue(keys[index],out check) && check != null ? check.Status : "unknown";
                        text.Append(names[index]).Append("：").Append(state == "pass" || state == "ok" ? "通过" : state == "fail" || state == "error" ? "未通过" : state == "warning" ? "需要留意" : state == "skipped" ? "未执行" : "未能确认").AppendLine();
                    }
                    return text.ToString();
                }
            } catch (Exception error) {
                if (!(error is SerializationException || error is InvalidDataException || error is System.Xml.XmlException)) throw;
                return "此版本的自检输出格式不兼容，未显示原始输出。不能据此判断连接已恢复。";
            }
        }
    }
    [DataContract] public sealed class ReconnectState {
        [DataMember] public string ConfigPath;
        [DataMember] public string BackupName;
        [DataMember] public bool OriginalExisted;
        [DataMember] public bool OriginalFlag;
    }
    public sealed class ReconnectFix {
        private readonly string configFile, backupDir;
        private readonly ICodexCli cli;
        private string StateFile { get { return Path.Combine(backupDir,"active.json"); } }
        public ReconnectFix(string configFile, string backupDir, ICodexCli cli) { this.configFile = Path.GetFullPath(configFile); this.backupDir = backupDir; this.cli = cli; }
        public static string ConfigFile() {
            string home = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (String.IsNullOrWhiteSpace(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");
            return Path.Combine(Path.GetFullPath(home),"config.toml");
        }
        public static string BackupDirectory() { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"CodexConnectionHelper\reconnect-backups"); }
        private void PreparePrivateDirectory() {
            Installation.NoReparse(backupDir); Directory.CreateDirectory(backupDir);
            var security = new DirectorySecurity(); security.SetAccessRuleProtection(true,false);
            foreach (var sid in new[] { WindowsIdentity.GetCurrent().User, new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null) })
                security.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            Directory.SetAccessControl(backupDir,security);
        }
        private void SaveState(ReconnectState state) {
            Installation.NoReparse(StateFile);
            string temp = StateFile + ".new-" + Guid.NewGuid().ToString("N");
            try {
                using (var stream = File.Create(temp)) new DataContractJsonSerializer(typeof(ReconnectState)).WriteObject(stream,state);
                if (File.Exists(StateFile)) File.Replace(temp,StateFile,null); else File.Move(temp,StateFile);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        private ReconnectState ReadState() {
            Installation.NoReparse(StateFile);
            using (var stream = File.OpenRead(StateFile)) {
                var state = (ReconnectState)new DataContractJsonSerializer(typeof(ReconnectState)).ReadObject(stream);
                if (!String.Equals(state.ConfigPath,configFile,StringComparison.OrdinalIgnoreCase) || String.IsNullOrEmpty(state.BackupName) || Path.GetFileName(state.BackupName) != state.BackupName)
                    throw new InvalidOperationException("备份不属于当前 Codex 配置，已停止还原。");
                return state;
            }
        }
        public bool HasPendingRestore() {
            Installation.NoReparse(backupDir); Installation.NoReparse(StateFile);
            if (!File.Exists(StateFile)) return false;
            ReadState(); return true;
        }
        public string Repair() {
            using (OperationLock.Acquire("Global\\CodexConnectionHelper-Config-" + WindowsIdentity.GetCurrent().User.Value)) return RepairCore();
        }
        private string RepairCore() {
            Installation.NoReparse(configFile);
            if (cli.ProxyFlag()) return "系统代理开关已经启用，无需重复修改。若仍反复重连，需要继续排查网络、会话或服务状态。";
            PreparePrivateDirectory();
            if (File.Exists(StateFile)) throw new InvalidOperationException("已有一次尚未还原的重连修复，请先点“还原重连修复前配置”，再重新应用。");
            var state = new ReconnectState { ConfigPath = configFile, OriginalExisted = File.Exists(configFile), OriginalFlag = false, BackupName = Guid.NewGuid().ToString("N") + ".toml" };
            File.WriteAllBytes(Path.Combine(backupDir,state.BackupName),state.OriginalExisted ? File.ReadAllBytes(configFile) : new byte[0]);
            SaveState(state); // Keep a recoverable record even if the CLI update fails midway.
            cli.SetProxyFlag(true);
            if (!cli.ProxyFlag()) throw new InvalidOperationException("设置没有在当前配置中生效，可能有配置档或策略覆盖。备份已保留，可一键还原。");
            return "已启用使用系统代理，并保存私人配置备份。请彻底退出并重新打开 Codex 后再试。服务商、模型和 ChatGPT 模式未改动。";
        }
        public string Restore() {
            using (OperationLock.Acquire("Global\\CodexConnectionHelper-Config-" + WindowsIdentity.GetCurrent().User.Value)) return RestoreCore();
        }
        private string RestoreCore() {
            Installation.NoReparse(configFile); Installation.NoReparse(backupDir);
            if (!File.Exists(StateFile)) return "没有本工具创建的待还原重连设置。";
            ReconnectState state = ReadState();
            if (File.Exists(configFile)) {
                // Ask the official writer to restore only our flag. Never replace a
                // whole config or delete it, even if it did not exist originally:
                // another application may have added unrelated settings meanwhile.
                cli.SetProxyFlag(state.OriginalFlag);
            }
            File.Delete(StateFile);
            return "已恢复原来的系统代理开关值，保留其他配置及后来修改的内容。完整备份仍保存在本机。请重新打开 Codex 使设置生效。";
        }
    }
}
