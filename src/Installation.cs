using System;
using System.IO;
using System.Linq;
namespace RemoteHelper {
    public sealed class Layout {
        public string InstallDir, DataDir, HostsPath, TaskName;
        public string ExecutableFileName="CodexConnectionHelper.exe";
        public string Executable { get { return Path.Combine(InstallDir, ExecutableFileName); } }
        public string ConfigPath { get { return Path.Combine(DataDir, "config.json"); } }
        public string LogDir { get { return Path.Combine(DataDir, "logs"); } }
        public static Layout Production() {
            return new Layout {
                InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CodexRemoteRelayHelper"),
                DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CodexRemoteRelayHelper"),
                HostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\drivers\etc\hosts"),
                TaskName = "CodexRemoteRelayHelper"
            };
        }
    }
    public interface IJob {
        string Snapshot();
        bool IsRunning();
        void ValidateOwner(string sid);
        void Stop();
        void Register(string sid);
        void Start();
        void Delete();
        void Restore(string xml);
    }
    public sealed class Installation {
        public const string ProductId = "CodexRemoteRelayHelper/1/7f7ad364-9335-42da-8b98-a6834ed9cf3f";
        private readonly Layout layout;
        private readonly IJob job;
        private readonly Action<string> prepare;
        private readonly Action verify;
        private readonly Action refresh;
        public Installation(Layout layout, IJob job, Action<string> prepare, Action verify, Action refresh = null) { this.layout = layout; this.job = job; this.prepare = prepare; this.verify = verify; this.refresh = refresh ?? delegate {}; }
        private void PrepareDirectories(string sid) {
            string[] newlyCreated = new[] { layout.LogDir,layout.DataDir,layout.InstallDir }.Where(d => !Directory.Exists(d)).ToArray();
            try {
                prepare(sid);
                File.WriteAllText(Path.Combine(layout.InstallDir,"product.id"),ProductId);
                File.WriteAllText(Path.Combine(layout.DataDir,"product.id"),ProductId);
            } catch {
                // Remove only empty directories absent before this operation; never recurse.
                foreach (string directory in newlyCreated) {
                    try { NoReparse(directory); if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory,false); } catch { }
                }
                throw;
            }
        }
        public static void NoReparse(string path) {
            string current = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current)) {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("安装位置包含链接或重解析点，请使用普通本机目录。");
                current = Path.GetDirectoryName(current);
            }
        }
        private void ValidateDirectories() {
            foreach (string directory in new[] { layout.InstallDir, layout.DataDir }) {
                NoReparse(directory);
                if (Directory.Exists(directory)) {
                    string marker = Path.Combine(directory,"product.id");
                    if (!File.Exists(marker) || File.ReadAllText(marker) != ProductId)
                        throw new InvalidOperationException("同名安装目录不属于此工具，请先核查：" + directory);
                }
            }
            NoReparse(layout.HostsPath);
        }
        private static byte[] Existing(string path) { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        private static void Save(string file, byte[] bytes) {
            NoReparse(file);
            if (bytes == null) { if (File.Exists(file)) File.Delete(file); return; }
            if (File.Exists(file) && File.ReadAllBytes(file).SequenceEqual(bytes)) return;
            string temp = file + ".new-" + Guid.NewGuid().ToString("N");
            try {
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(file)) File.Replace(temp,file,null); else File.Move(temp,file);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        private bool EditHosts(Func<string,string> edit) {
            NoReparse(layout.HostsPath);
            using (var stream = new FileStream(layout.HostsPath,FileMode.Open,FileAccess.ReadWrite,FileShare.Read)) {
                if (stream.Length > 4 * 1024 * 1024) throw new InvalidOperationException("hosts 文件异常大，已停止自动修改。");
                byte[] before = new byte[stream.Length];
                int read = 0; while (read < before.Length) { int count = stream.Read(before,read,before.Length-read); if (count == 0) throw new EndOfStreamException(); read += count; }
                var document = TextBytes.Read(before);
                byte[] after = document.Encode(edit(document.Text));
                if (before.SequenceEqual(after)) return false;
                string backup = Path.Combine(layout.DataDir,"hosts-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".bak");
                File.WriteAllBytes(backup,before);
                try { stream.Position = 0; stream.Write(after,0,after.Length); stream.SetLength(after.Length); stream.Flush(true); refresh(); }
                catch { stream.Position = 0; stream.Write(before,0,before.Length); stream.SetLength(before.Length); stream.Flush(true); refresh(); throw; }
                return true;
            }
        }
        public void Install(ProxyEndpoint endpoint, string sid, string source) {
            using (OperationLock.Acquire("Global\\CodexConnectionHelper-Install")) InstallCore(endpoint,sid,source);
        }
        private void InstallCore(ProxyEndpoint endpoint, string sid, string source) {
            var config = new RelayConfig { Proxy = endpoint.ToString(), OwnerSid = sid };
            byte[] newConfig = config.Bytes();
            ValidateDirectories();
            job.ValidateOwner(sid);
            string previousTask = job.Snapshot();
            byte[] previousConfig = Existing(layout.ConfigPath), previousBinary = Existing(layout.Executable);
            bool active = previousTask != null || HostsText.HasBlock(TextBytes.Read(File.ReadAllBytes(layout.HostsPath)).Text);
            if (active && previousConfig != null && RelayConfig.Read(layout.ConfigPath).OwnerSid != sid)
                throw new InvalidOperationException("此工具已由另一 Windows 用户安装，请由原用户管理或先卸载。");
            HostsText.Add(TextBytes.Read(File.ReadAllBytes(layout.HostsPath)).Text);
            byte[] candidate = File.ReadAllBytes(source);
            bool touchedTask = false, addedHosts = false;
            try {
                PrepareDirectories(sid);
                if (previousTask != null) File.WriteAllText(Path.Combine(layout.DataDir,"task-backup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".xml"),previousTask);
                touchedTask = true;
                job.Stop();
                Save(layout.Executable,candidate);
                Save(layout.ConfigPath,newConfig);
                job.Register(sid); job.Start(); verify();
                addedHosts = EditHosts(HostsText.Add);
                verify(); // A proxy may resolve its CONNECT target through the newly changed hosts file.
            } catch (Exception original) {
                try {
                    if (addedHosts) EditHosts(HostsText.Remove);
                    if (touchedTask) {
                        job.Stop(); job.Delete();
                        Save(layout.Executable,previousBinary); Save(layout.ConfigPath,previousConfig);
                        if (previousTask != null) { job.Restore(previousTask); job.Start(); }
                    }
                } catch (Exception rollback) { throw new IOException("操作失败，自动回退也遇到问题：" + rollback.Message + "；原始错误：" + original.Message,original); }
                throw;
            }
        }
        public void Uninstall() {
            using (OperationLock.Acquire("Global\\CodexConnectionHelper-Install")) UninstallCore();
        }
        private void UninstallCore() {
            ValidateDirectories();
            string sid = File.Exists(layout.ConfigPath) ? RelayConfig.Read(layout.ConfigPath).OwnerSid : null;
            job.ValidateOwner(sid);
            string previousTask = job.Snapshot();
            string originalText = TextBytes.Read(File.ReadAllBytes(layout.HostsPath)).Text;
            HostsText.Remove(originalText); // Refuse edited markers before stopping a working task.
            bool removedHosts = false,touchedTask=false,wasRunning=previousTask != null && job.IsRunning();
            try {
                if ((previousTask != null || HostsText.HasBlock(originalText)) && !Directory.Exists(layout.DataDir)) PrepareDirectories(sid ?? System.Security.Principal.WindowsIdentity.GetCurrent().User.Value);
                if (previousTask != null) File.WriteAllText(Path.Combine(layout.DataDir,"task-uninstall-"+Guid.NewGuid().ToString("N")+".xml"),previousTask);
                touchedTask=true;
                job.Stop();
                removedHosts = EditHosts(HostsText.Remove);
                job.Delete();
            } catch {
                if (removedHosts) EditHosts(HostsText.Add);
                if (touchedTask && previousTask != null) { job.Restore(previousTask); if (wasRunning) job.Start(); }
                throw;
            }
        }
    }
}
