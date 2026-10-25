// Optional, local integration checks. No production hosts, tasks, or config are edited.
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Threading;
using RemoteHelper;
public static class LiveChecks {
    private static void Assert(bool value,string message) { if (!value) throw new Exception(message); }
    private static int FreePort() { var listener = new TcpListener(IPAddress.Parse("127.0.0.2"),0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static void WaitListening(int port,bool expected) {
        DateTime end = DateTime.UtcNow.AddSeconds(15);
        while (Tunnel.IsListening(port) != expected) { if (DateTime.UtcNow > end) throw new Exception("Unexpected listener state"); Thread.Sleep(100); }
    }
    public static int Main(string[] args) {
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-data","live-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            if (args[0] == "cli") {
                string file = Path.Combine(root,"config.toml");
                File.WriteAllText(file,"model_provider = \"openai\"\r\n[features]\r\nrespect_system_proxy = false\r\n");
                var cli = new CodexCli(CodexCli.Find(),root); var fix = new ReconnectFix(file,Path.Combine(root,"backup"),cli);
                Assert(!cli.ProxyFlag(),"Isolated initial flag was not false"); fix.Repair(); Assert(cli.ProxyFlag(),"Official CLI did not enable flag");
                File.AppendAllText(file,"\r\n# preserve later edits\r\n"); fix.Restore(); Assert(!cli.ProxyFlag(),"Official CLI did not restore flag");
                string restored = File.ReadAllText(file); Assert(restored.Contains("# preserve later edits") && restored.Contains("model_provider = \"openai\""),"Official CLI lost unrelated config");
                Console.WriteLine("PASS real Codex CLI enable and restore with isolated CODEX_HOME");
            } else if (args[0] == "doctor") {
                Console.WriteLine(new CodexCli(CodexCli.Find()).Diagnose());
            } else if (args[0] == "tls") {
                ProxyEndpoint endpoint; if (!ProxyEndpoint.TryParse(args[1],out endpoint)) throw new Exception("Invalid loopback HTTP proxy");
                int port = FreePort(); string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"RelayHarness.exe");
                string parameters = port + " " + endpoint + " \"" + Path.Combine(root,"relay.log") + "\" \"" + Path.Combine(root,"console.txt") + "\"";
                using (var child = Process.Start(new ProcessStartInfo(exe,parameters) { UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden })) {
                    try { WaitListening(port,true); int code = Tunnel.ProbeRelay(port).GetAwaiter().GetResult(); Console.WriteLine("PASS real chatgpt.com TLS certificate and HTTP response " + code); }
                    finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); } }
                }
            } else if (args[0] == "scheduler") {
                int port = FreePort(); var layout = new Layout { InstallDir=root,DataDir=root,TaskName="CodexConnectionHelper-Test-"+Guid.NewGuid().ToString("N") };
                File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"RelayHarness.exe"),layout.Executable);
                string parameters = port + " 127.0.0.1:9 \"" + Path.Combine(root,"relay.log") + "\" \"" + Path.Combine(root,"console.txt") + "\"";
                var job = new ScheduledJob(layout,parameters);
                try {
                    Assert(job.Snapshot() == null,"Task name collision"); job.Register(WindowsIdentity.GetCurrent().User.Value);
                    job.ValidateOwner(WindowsIdentity.GetCurrent().User.Value); // Validates actual RunLevel=0, even when XML omits its default value.
                    string xml = job.Snapshot(); Assert(xml.Contains("<Interval>PT1M</Interval>"),"Task recovery missing");
                    job.Start(); WaitListening(port,true); Console.WriteLine("CHECK task started: " + job.State()); Assert(File.ReadAllText(Path.Combine(root,"console.txt")) == "no-console","Scheduled process has console");
                    job.Stop(); Console.WriteLine("CHECK stop returned: " + job.State()); WaitListening(port,false); job.Delete(); Assert(job.Snapshot() == null,"Task was not deleted");
                    job.Restore(xml); job.Start(); WaitListening(port,true);
                    Console.WriteLine("PASS ordinary-user scheduler create start stop restore and no console");
                } finally { job.Delete(); WaitListening(port,false); }
                Assert(job.Snapshot() == null,"Test task was not cleaned up"); Console.WriteLine("PASS disposable scheduler task removed");
            } else if (args[0] == "removal") {
                int port=FreePort();
                var layout=new Layout { InstallDir=root,TaskName="CodexConnectionHelper-RemovalTest-"+Guid.NewGuid().ToString("N"),ExecutableFileName="CodexRemoteRelay.exe" };
                File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"LegacyRemovalProbe.exe"),layout.Executable);
                var job=new ScheduledJob(layout,"--listen-port "+port,LegacyTaskInfo.Description);
                string hosts=Path.Combine(root,"hosts"),backups=Path.Combine(root,"backups");
                const string original="# preserve this file\r\n127.0.0.1 localhost\r\n";
                const string block="# BEGIN CodexRemoteProxyFix\r\n127.0.0.2 chatgpt.com\r\n# END CodexRemoteProxyFix\r\n";
                try {
                    Assert(job.Snapshot() == null,"Task name collision"); File.WriteAllText(hosts,block+original);
                    job.Register(WindowsIdentity.GetCurrent().User.Value); job.Start(); WaitListening(port,true);
                    var compatible=LegacyTaskInfo.OpenJob(layout.TaskName);
                    new LegacyRemoval(hosts,backups,compatible,compatible.IsRunning,()=>Directory.CreateDirectory(backups),()=>{},()=>WaitListening(port,false)).Uninstall();
                    Assert(job.Snapshot() == null && File.ReadAllText(hosts) == original,"Live removal did not remove task and mapping");
                    Assert(Directory.GetFiles(backups,"*-task.xml").Length == 1 && Directory.GetFiles(backups,"*-hosts.bak").Length == 1,"Live removal backups missing");
                    Console.WriteLine("PASS recognized existing task stopped and deleted; temporary hosts restored and both backups saved");
                    job.Register(WindowsIdentity.GetCurrent().User.Value); job.Stop(); File.WriteAllText(hosts,block+original);
                    compatible=LegacyTaskInfo.OpenJob(layout.TaskName); bool failed=false,first=true;
                    try { new LegacyRemoval(hosts,backups,compatible,compatible.IsRunning,()=>Directory.CreateDirectory(backups),()=>{if(first) {first=false; throw new IOException("injected refresh failure");}},()=>{}).Uninstall(); }
                    catch(IOException) { failed=true; }
                    dynamic service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")); service.Connect(); dynamic task=service.GetFolder(@"\").GetTask(layout.TaskName);
                    Assert(failed && !((bool)task.Enabled) && !job.IsRunning() && File.ReadAllText(hosts) == block+original,"Rollback changed disabled task state or hosts");
                    Console.WriteLine("PASS failed removal restores actual disabled task without starting it");
                } finally { job.Delete(); WaitListening(port,false); }
                Assert(job.Snapshot() == null,"Disposable removal task remains");
            } else throw new Exception("Expected cli, doctor, tls <proxy>, scheduler, or removal");
            return 0;
        } catch (Exception error) { Console.Error.WriteLine("FAIL " + error); return 1; }
    }
}
