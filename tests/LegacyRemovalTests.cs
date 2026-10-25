using System;
using System.IO;
using System.Linq;
using System.Text;
using RemoteHelper;
public static class LegacyRemovalTests {
    private const string Block="# BEGIN CodexRemoteProxyFix\r\n127.0.0.2 chatgpt.com\r\n# END CodexRemoteProxyFix\r\n";
    private sealed class Job : IJob {
        public string Xml="original-task"; public bool Running=true,Foreign,FailDelete; public int Starts,Stops;
        public void ValidateOwner(string sid) { if (Foreign) throw new InvalidOperationException("unknown task"); }
        public string Snapshot() { return Xml; }
        public bool IsRunning() { return Running; }
        public void Stop() { Stops++; Running=false; }
        public void Register(string sid) { throw new Exception("uninstall must not register new tasks"); }
        public void Start() { Starts++; Running=true; }
        public void Delete() { Xml=null; if (FailDelete) throw new IOException("failure after task deletion"); }
        public void Restore(string xml) { Xml=xml; }
    }
    private sealed class Fixture {
        public readonly string Root,Hosts,Backups; public readonly Job Task=new Job(); public readonly LegacyRemoval Removal;
        public Fixture() {
            Root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-data",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root);
            Hosts=Path.Combine(Root,"hosts"); Backups=Path.Combine(Root,"backups");
            File.WriteAllText(Hosts,"# 保留其他内容\r\n"+Block+"10.0.0.2 intranet\r\n",new UnicodeEncoding(false,true));
            Removal=new LegacyRemoval(Hosts,Backups,Task,()=>Task.Running,()=>Directory.CreateDirectory(Backups),()=>{},()=>{});
        }
    }
    private static int passed,failed;
    private static void Check(bool value,string why) { if (!value) throw new Exception(why); }
    private static void Test(string name,Action body) { try { body(); passed++; Console.WriteLine("PASS "+name); } catch(Exception e) { failed++; Console.WriteLine("FAIL "+name+": "+e.Message); } }
    private static void Reject(Action body) { bool rejected=false; try { body(); } catch(InvalidOperationException) { rejected=true; } Check(rejected,"unsafe operation was accepted"); }
    public static int Main() {
        Test("existing deployment uninstall backs up task and exact hosts while preserving other entries",()=> {
            var f=new Fixture(); byte[] before=File.ReadAllBytes(f.Hosts); f.Removal.Uninstall();
            byte[] expected=new byte[] {255,254}.Concat(Encoding.Unicode.GetBytes("# 保留其他内容\r\n10.0.0.2 intranet\r\n")).ToArray();
            Check(File.ReadAllBytes(f.Hosts).SequenceEqual(expected),"unrelated hosts bytes changed");
            Check(File.ReadAllBytes(Directory.GetFiles(f.Backups,"*-hosts.bak").Single()).SequenceEqual(before),"hosts backup differs");
            Check(File.ReadAllText(Directory.GetFiles(f.Backups,"*-task.xml").Single()) == "original-task" && f.Task.Xml == null && !f.Task.Running,"task not backed up and removed");
        });
        Test("failed task deletion restores exact hosts placement and running task",()=> {
            var f=new Fixture(); byte[] before=File.ReadAllBytes(f.Hosts); f.Task.FailDelete=true;
            try { f.Removal.Uninstall(); throw new Exception("failure missing"); } catch(IOException) {}
            Check(File.ReadAllBytes(f.Hosts).SequenceEqual(before) && f.Task.Xml == "original-task" && f.Task.Running,"rollback did not restore original state");
        });
        Test("rollback does not start a task that was stopped before uninstall",()=> {
            var f=new Fixture(); f.Task.Running=false; f.Task.Xml="disabled-task"; f.Task.FailDelete=true;
            try { f.Removal.Uninstall(); } catch(IOException) {}
            Check(f.Task.Xml == "disabled-task" && !f.Task.Running && f.Task.Starts == 0,"rollback enabled previously stopped task");
        });
        Test("modified or duplicate mappings are rejected before stopping task",()=> {
            foreach (string text in new[] {Block+Block,Block.Replace("127.0.0.2","127.0.0.3"),Block+HostsText.Block}) {
                var f=new Fixture(); File.WriteAllText(f.Hosts,text); Reject(f.Removal.Uninstall);
                Check(f.Task.Stops == 0 && File.ReadAllText(f.Hosts) == text,"invalid mapping caused mutation");
            }
        });
        Test("foreign task is rejected without creating backups or changing hosts",()=> {
            var f=new Fixture(); f.Task.Foreign=true; byte[] before=File.ReadAllBytes(f.Hosts); Reject(f.Removal.Uninstall);
            Check(!Directory.Exists(f.Backups) && f.Task.Stops == 0 && File.ReadAllBytes(f.Hosts).SequenceEqual(before),"foreign task caused mutation");
        });
        Test("orphan mapping can be removed without a task",()=> {
            var f=new Fixture(); f.Task.Xml=null; f.Task.Running=false; f.Removal.Uninstall();
            Check(!File.ReadAllText(f.Hosts).Contains("chatgpt.com") && File.ReadAllText(f.Hosts).Contains("intranet"),"orphan mapping remains");
            Check(Directory.GetFiles(f.Backups,"*-hosts.bak").Length == 1 && Directory.GetFiles(f.Backups,"*-task.xml").Length == 0,"orphan backup incorrect");
        });
        Test("backup failure leaves a running task untouched",()=> {
            var f=new Fixture(); byte[] before=File.ReadAllBytes(f.Hosts);
            var removal=new LegacyRemoval(f.Hosts,f.Backups,f.Task,()=>true,()=>{throw new IOException("backup unavailable");},()=>{},()=>{});
            try { removal.Uninstall(); } catch(IOException) {}
            Check(f.Task.Stops == 0 && f.Task.Running && File.ReadAllBytes(f.Hosts).SequenceEqual(before),"backup failure interrupted existing relay");
        });
        Test("task identity accepts known relay and rejects changed executable privileges or arguments",()=> {
            string xml="<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><RegistrationInfo><Description>"+LegacyTaskInfo.Description+"</Description></RegistrationInfo><Principals><Principal><UserId>S-1-5-21-100-200-300-1001</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals><Actions><Exec><Command>C:\\Relay\\CodexRemoteRelay.exe</Command><Arguments>--proxy-port 7890 --listen-port 443</Arguments></Exec></Actions></Task>";
            Check(LegacyTaskInfo.Parse(xml).Arguments.Contains("7890"),"recognized task was rejected");
            foreach (string changed in new[] {xml.Replace("CodexRemoteRelay.exe","other.exe"),xml.Replace("LeastPrivilege","HighestAvailable"),xml.Replace("--proxy-port 7890","--unknown 7890"),xml.Replace("--proxy-port 7890","--proxy-port 99999"),xml.Replace(LegacyTaskInfo.Description,"unrelated task")}) Reject(()=>LegacyTaskInfo.Parse(changed));
        });
        Console.WriteLine("RESULT "+passed+" passed, "+failed+" failed"); return failed == 0 ? 0 : 1;
    }
}
