using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Principal;
using RemoteHelper;
public static class InstallationTests {
    private sealed class FakeJob : IJob {
        public string Xml;
        public bool Running, FailStart, Foreign, FailDelete;
        public string Snapshot() { return Xml; }
        public bool IsRunning() { return Running; }
        public void ValidateOwner(string sid) { if (Foreign) throw new InvalidOperationException("foreign task"); }
        public void Stop() { Running = false; }
        public void Register(string sid) { Xml = "new-task"; }
        public void Start() { if (FailStart) { FailStart = false; throw new IOException("simulated start failure"); } Running = true; }
        public void Delete() { Xml = null; Running = false; if (FailDelete) throw new IOException("simulated deletion failure"); }
        public void Restore(string xml) { Xml = xml; }
    }
    private sealed class Fixture {
        public Layout Layout;
        public FakeJob Job = new FakeJob();
        public Installation Installer;
        public string Sid = WindowsIdentity.GetCurrent().User.Value;
        public string Source;
        public ProxyEndpoint Proxy;
        public Fixture() {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Layout = new Layout { InstallDir = Path.Combine(root,"program"), DataDir = Path.Combine(root,"data"), HostsPath = Path.Combine(root,"hosts"), TaskName = "fixture" };
            File.WriteAllText(Layout.HostsPath,"# 中文\r\n127.0.0.1 localhost",new UnicodeEncoding(false,true));
            Source = Path.Combine(root,"candidate.exe"); File.WriteAllText(Source,"new binary");
            ProxyEndpoint.TryParse("127.0.0.1:15236",out Proxy);
            Installer = new Installation(Layout,Job,delegate(string sid) { Directory.CreateDirectory(Layout.InstallDir); Directory.CreateDirectory(Layout.DataDir); Directory.CreateDirectory(Layout.LogDir); },delegate {});
        }
        public void Install() { Installer.Install(Proxy,Sid,Source); }
    }
    private static int failed, passed;
    private static void Check(bool value, string why) { if (!value) throw new Exception(why); }
    private static void Test(string name, Action body) { try { body(); passed++; Console.WriteLine("PASS " + name); } catch(Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); } }
    private static void Reject(Action body) { bool did = false; try { body(); } catch(InvalidOperationException) { did = true; } Check(did,"did not refuse unsafe input"); }
    public static int Main() {
        Test("install repair uninstall preserve hosts bytes and other edits", delegate {
            var f = new Fixture(); byte[] original = File.ReadAllBytes(f.Layout.HostsPath);
            f.Install(); f.Install(); Check(f.Job.Running,"not running");
            Check(RelayConfig.Read(f.Layout.ConfigPath).Proxy == "127.0.0.1:15236","wrong config");
            f.Installer.Uninstall(); Check(File.ReadAllBytes(f.Layout.HostsPath).SequenceEqual(original),"encoding or content lost"); Check(f.Job.Xml == null,"task remains");
            Check(Directory.GetFiles(f.Layout.DataDir,"task-uninstall-*.xml").Any(file=>File.ReadAllText(file) == "new-task"),"task was not backed up before removal");
            f.Install(); File.AppendAllText(f.Layout.HostsPath,"\r\n10.0.0.1 intranet",Encoding.Unicode); f.Installer.Uninstall();
            Check(File.ReadAllText(f.Layout.HostsPath).EndsWith("10.0.0.1 intranet"),"unrelated edit lost");
        });
        Test("fresh failed installation restores hooks", delegate {
            var f = new Fixture(); byte[] original = File.ReadAllBytes(f.Layout.HostsPath); f.Job.FailStart = true;
            bool failedAsExpected = false; try { f.Install(); } catch(IOException) { failedAsExpected = true; }
            Check(failedAsExpected,"failure missing"); Check(File.ReadAllBytes(f.Layout.HostsPath).SequenceEqual(original),"hosts changed"); Check(f.Job.Xml == null,"new task remains");
        });
        Test("failed repair restores prior binary config and task", delegate {
            var f = new Fixture(); f.Install(); byte[] oldConfig = File.ReadAllBytes(f.Layout.ConfigPath);
            File.WriteAllText(f.Source,"replacement"); f.Job.Xml = "old-task"; f.Job.FailStart = true;
            try { f.Install(); } catch(IOException) {}
            Check(File.ReadAllText(f.Layout.Executable) == "new binary","old runtime not restored");
            Check(File.ReadAllBytes(f.Layout.ConfigPath).SequenceEqual(oldConfig),"old config not restored");
            Check(f.Job.Xml == "old-task" && f.Job.Running,"old task not restarted");
        });
        Test("refuse foreign task or directory before modification", delegate {
            var f = new Fixture(); f.Job.Foreign = true; byte[] original = File.ReadAllBytes(f.Layout.HostsPath);
            Reject(f.Install); Check(!File.Exists(f.Layout.Executable),"foreign task caused file write");
            f.Job.Foreign = false; Directory.CreateDirectory(f.Layout.InstallDir); File.WriteAllText(Path.Combine(f.Layout.InstallDir,"keep.txt"),"preserve");
            Reject(f.Install); Check(File.ReadAllBytes(f.Layout.HostsPath).SequenceEqual(original),"hosts changed on conflict");
        });
        Test("refuse conflicting mapping and malformed uninstall", delegate {
            var f = new Fixture(); File.WriteAllText(f.Layout.HostsPath,"127.0.0.3 chatgpt.com\n"); Reject(f.Install);
            File.WriteAllText(f.Layout.HostsPath,"127.0.0.1 localhost\n"); f.Install();
            File.WriteAllText(f.Layout.HostsPath,HostsText.Begin + "\nmanual edit\n");
            Reject(f.Installer.Uninstall); Check(f.Job.Running,"malformed block stopped working task");
        });
        Test("final mapped route failure rolls back a fresh install",delegate {
            var f = new Fixture(); byte[] original = File.ReadAllBytes(f.Layout.HostsPath);
            f.Installer = new Installation(f.Layout,f.Job,delegate(string sid) { Directory.CreateDirectory(f.Layout.InstallDir); Directory.CreateDirectory(f.Layout.DataDir); },delegate {
                if (HostsText.HasBlock(File.ReadAllText(f.Layout.HostsPath))) throw new IOException("proxy DNS recursion after hosts commit");
            });
            bool rejected = false; try { f.Install(); } catch(IOException) { rejected = true; }
            Check(rejected,"final route was never verified"); Check(File.ReadAllBytes(f.Layout.HostsPath).SequenceEqual(original),"failed final route left hosts mapped"); Check(f.Job.Xml == null,"failed final route left task");
        });
        Test("partial directory preparation can be retried",delegate {
            var f = new Fixture(); var normal = f.Installer;
            f.Installer = new Installation(f.Layout,f.Job,delegate(string sid) { Directory.CreateDirectory(f.Layout.InstallDir); throw new IOException("failed during preparation"); },delegate {});
            try { f.Install(); } catch(IOException) { }
            f.Installer = normal; f.Install(); Check(f.Job.Running,"retry failed");
        });
        Test("uninstall orphaned owned mapping recreates backup location",delegate {
            var f = new Fixture(); File.WriteAllText(f.Layout.HostsPath,HostsText.Block + "127.0.0.1 localhost\n");
            f.Installer.Uninstall(); Check(!HostsText.HasBlock(File.ReadAllText(f.Layout.HostsPath)),"orphan mapping remains");
            Check(Directory.GetFiles(f.Layout.DataDir,"hosts-*.bak").Length == 1,"orphan hosts not backed up");
        });
        Test("another user can deploy after a completed uninstall",delegate {
            var f = new Fixture(); f.Install(); string originalSid = f.Sid; f.Sid = "S-1-5-21-100-200-300-1001";
            Reject(f.Install); f.Sid = originalSid; f.Installer.Uninstall(); f.Sid = "S-1-5-21-100-200-300-1001";
            f.Install(); Check(RelayConfig.Read(f.Layout.ConfigPath).OwnerSid == f.Sid,"new owner did not take effect");
        });
        Test("managed uninstall rollback preserves a stopped task",delegate {
            var f=new Fixture(); f.Install(); f.Job.Running=false; f.Job.FailDelete=true; byte[] before=File.ReadAllBytes(f.Layout.HostsPath);
            try { f.Installer.Uninstall(); } catch(IOException) {}
            Check(!f.Job.Running && f.Job.Xml == "new-task" && File.ReadAllBytes(f.Layout.HostsPath).SequenceEqual(before),"uninstall rollback changed original stopped state");
        });
        Test("managed backup preparation failure does not start or change a task",delegate {
            var f=new Fixture(); f.Job.Xml="existing-task"; f.Job.Running=false;
            var removal=new Installation(f.Layout,f.Job,delegate(string sid) { throw new IOException("backup unavailable"); },delegate {});
            try { removal.Uninstall(); } catch(IOException) {}
            Check(!f.Job.Running && f.Job.Xml == "existing-task","backup failure changed task state");
        });
        Console.WriteLine("RESULT " + passed + " passed, " + failed + " failed"); return failed == 0 ? 0 : 1;
    }
}
