using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace RemoteHelper {
    public static class Program {
        [STAThread] public static int Main(string[] args) {
            var layout = Layout.Production();
            if (args.Length == 1 && args[0] == "--relay") {
                try {
                    var config = RelayConfig.Read(layout.ConfigPath);
                    if (config.OwnerSid != WindowsIdentity.GetCurrent().User.Value) return 1;
                    return Relay.Run(config.Endpoint(),443,Path.Combine(layout.LogDir,"relay.log"));
                } catch { return 1; }
            }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length > 0) {
                try {
                    if (!WindowsSystem.IsAdmin) throw new InvalidOperationException("修改系统转发设置需要 Windows 管理员确认。");
                    WindowsSystem.ValidateTrustedDirectories(layout);
                    var job = new ScheduledJob(layout);
                    var installation = new Installation(layout,job,delegate(string sid) { WindowsSystem.Prepare(layout,sid); },delegate { WindowsSystem.VerifyRuntime(layout); },delegate { WindowsSystem.DnsFlushResolverCache(); });
                    if (args.Length == 3 && args[0] == "--apply-install") {
                        ProxyEndpoint endpoint;
                        if (!ProxyEndpoint.TryParse(args[1],out endpoint)) throw new InvalidOperationException("请输入本机 HTTP 代理端口。");
                        new SecurityIdentifier(args[2]).Translate(typeof(NTAccount));
                        job.ValidateOwner(args[2]);
                        int listener = WindowsSystem.ListenerProcess(443);
                        if (listener != 0 && !WindowsSystem.OwnsListener(layout.Executable)) throw new InvalidOperationException("转发地址已被另一程序占用。已有转发器时请先检测，正常运行无需重复部署。");
                        Tunnel.Probe(endpoint).GetAwaiter().GetResult();
                        installation.Install(endpoint,args[2],Assembly.GetExecutingAssembly().Location);
                    } else if (args.Length == 1 && args[0] == "--apply-uninstall") UninstallRelay(layout,installation,job);
                    else throw new InvalidOperationException("不支持的操作。");
                    if (!WindowsSystem.DnsFlushResolverCache()) MessageBox.Show("操作已完成，但 DNS 缓存刷新未成功。请重新打开 Codex 后检查连接。","提示",MessageBoxButtons.OK,MessageBoxIcon.Information);
                    return 0;
                } catch (Exception error) { MessageBox.Show(error.Message,"操作未完成",MessageBoxButtons.OK,MessageBoxIcon.Error); return 1; }
            }
            Application.Run(new MainWindow()); return 0;
        }
        private static void UninstallRelay(Layout layout,Installation installation,ScheduledJob managed) {
            var existing=LegacyTaskInfo.OpenJob(); string snapshot=existing.Snapshot();
            if (snapshot == null && !LegacyHosts.HasBlock(TextBytes.Read(File.ReadAllBytes(layout.HostsPath)).Text)) { installation.Uninstall(); return; }
            if (managed.Snapshot() != null) throw new InvalidOperationException("检测到多套转发任务，请先核查重复部署。");
            string executable=snapshot == null ? null : LegacyTaskInfo.Parse(snapshot).Executable;
            string backups=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"CodexConnectionHelper-UninstallBackups");
            new LegacyRemoval(layout.HostsPath,backups,existing,existing.IsRunning,
                delegate { WindowsSystem.PrepareUninstallBackups(backups); },
                delegate { WindowsSystem.DnsFlushResolverCache(); },delegate {
                    DateTime until=DateTime.UtcNow.AddSeconds(8);
                    while (executable != null && WindowsSystem.OwnsListener(executable)) {
                        if (DateTime.UtcNow > until) throw new IOException("转发进程尚未停止，已保留原配置，请稍后重试。");
                        System.Threading.Thread.Sleep(100);
                    }
                }).Uninstall();
        }
    }
}
