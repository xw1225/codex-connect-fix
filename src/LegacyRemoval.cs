using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Runtime.InteropServices;
namespace RemoteHelper {
    // Compatibility identification stays internal; the UI presents one relay.
    public sealed class LegacyTaskInfo {
        public const string TaskName="CodexRemoteProxyFix";
        public const string Description="Compatibility relay for Codex Remote. Listens only on 127.0.0.2:443 and forwards opaque TLS to chatgpt.com:443 through the existing local Veee proxy.";
        public string Executable,Arguments;
        public static ScheduledJob OpenJob(string name=TaskName) {
            dynamic service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")); service.Connect(); dynamic folder=service.GetFolder(@"\");
            dynamic task=null;
            try { task=folder.GetTask(name); }
            catch (FileNotFoundException e) { if (e.HResult != unchecked((int)0x80070002)) throw; }
            catch (COMException e) { if (e.HResult != unchecked((int)0x80070002)) throw; }
            var info=task == null ? null : Parse((string)task.Xml);
            // The fallback is only an identity guard for an absent task. No file
            // or task is created; a task appearing with another action is refused.
            var layout=new Layout { TaskName=name,ExecutableFileName="CodexRemoteRelay.exe",InstallDir=info == null ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"CodexRemoteProxyFix") : Path.GetDirectoryName(info.Executable) };
            return new ScheduledJob(layout,info == null ? "" : info.Arguments,Description);
        }
        public static LegacyTaskInfo Parse(string xml) {
            if (String.IsNullOrEmpty(xml) || xml.Length > 1048576) throw new InvalidOperationException("转发任务定义无法识别，已停止自动操作。");
            var document=new XmlDocument { XmlResolver=null };
            using (var reader=XmlReader.Create(new StringReader(xml),new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null })) document.Load(reader);
            var ns=new XmlNamespaceManager(document.NameTable); ns.AddNamespace("t","http://schemas.microsoft.com/windows/2004/02/mit/task");
            Func<string,string> value=path => { var node=document.SelectSingleNode("/t:Task/"+path,ns); return node == null ? "" : node.InnerText; };
            var actions=document.SelectNodes("/t:Task/t:Actions/*",ns);
            string command=value("t:Actions/t:Exec/t:Command"), arguments=value("t:Actions/t:Exec/t:Arguments");
            string level=value("t:Principals/t:Principal/t:RunLevel"), user=value("t:Principals/t:Principal/t:UserId");
            if (value("t:RegistrationInfo/t:Description") != Description || actions.Count != 1 || actions[0].LocalName != "Exec" ||
                !Regex.IsMatch(command,@"^[A-Za-z]:[\\/]") || !String.Equals(Path.GetFileName(command),"CodexRemoteRelay.exe",StringComparison.OrdinalIgnoreCase) ||
                value("t:Principals/t:Principal/t:LogonType") != "InteractiveToken" || (level != "" && level != "LeastPrivilege") || String.IsNullOrWhiteSpace(user))
                throw new InvalidOperationException("发现同名任务，但其用途或权限与转发器不符，已停止自动操作。");
            string directory=value("t:Actions/t:Exec/t:WorkingDirectory");
            if (directory.Length > 0 && !String.Equals(Path.GetFullPath(directory).TrimEnd('\\'),Path.GetDirectoryName(Path.GetFullPath(command)).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("转发任务工作目录已变更，请先核查。");
            string remaining=arguments; var seen=new System.Collections.Generic.HashSet<string>();
            while (!String.IsNullOrWhiteSpace(remaining)) {
                var match=Regex.Match(remaining,@"^\s*(--proxy-port|--listen-port|--log-path)\s+(?:""([^""]+)""|(\S+))(?:\s+|$)");
                if (!match.Success || !seen.Add(match.Groups[1].Value)) throw new InvalidOperationException("转发任务参数无法识别，请先核查。");
                string option=match.Groups[1].Value, text=match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
                int port;
                if (option == "--log-path" ? !Path.IsPathRooted(text) : !Int32.TryParse(text,out port) || port < 1 || port > 65535)
                    throw new InvalidOperationException("转发任务参数无效，请先核查。");
                remaining=remaining.Substring(match.Length);
            }
            return new LegacyTaskInfo { Executable=Path.GetFullPath(command),Arguments=arguments };
        }
    }
    public static class LegacyHosts {
        public const string Begin="# BEGIN CodexRemoteProxyFix",End="# END CodexRemoteProxyFix";
        private static Match Find(string text) {
            var matches=Regex.Matches(text,@"(?m)^# BEGIN CodexRemoteProxyFix\r?\n127\.0\.0\.2[ \t]+chatgpt\.com\r?\n# END CodexRemoteProxyFix(?:\r?\n|$)");
            int begins=Regex.Matches(text,Regex.Escape(Begin)).Count,ends=Regex.Matches(text,Regex.Escape(End)).Count;
            if (begins == 0 && ends == 0) return null;
            if (begins != 1 || ends != 1 || matches.Count != 1) throw new InvalidOperationException("转发器 hosts 标记重复或不完整，请先核查。");
            return matches[0];
        }
        public static bool HasBlock(string text) { return Find(text) != null; }
        public static string Remove(string text) { var match=Find(text); return match == null ? text : text.Remove(match.Index,match.Length); }
    }
    public sealed class LegacyRemoval {
        private readonly string hosts,backups;
        private readonly IJob job;
        private readonly Func<bool> running;
        private readonly Action prepare,refresh,verifyStopped;
        public LegacyRemoval(string hosts,string backups,IJob job,Func<bool> running,Action prepare,Action refresh,Action verifyStopped) {
            this.hosts=hosts; this.backups=backups; this.job=job; this.running=running; this.prepare=prepare; this.refresh=refresh; this.verifyStopped=verifyStopped;
        }
        public void Uninstall() {
            using (OperationLock.Acquire("Global\\CodexConnectionHelper-Install")) {
                job.ValidateOwner(null); string snapshot=job.Snapshot(); bool wasRunning=snapshot != null && running();
                Installation.NoReparse(hosts);
                using (var stream=new FileStream(hosts,FileMode.Open,FileAccess.ReadWrite,FileShare.Read)) {
                    if (stream.Length > 4*1024*1024) throw new IOException("hosts 文件异常大，已停止自动修改。");
                    byte[] before=new byte[stream.Length]; int offset=0;
                    while (offset < before.Length) { int count=stream.Read(before,offset,before.Length-offset); if (count == 0) throw new EndOfStreamException(); offset+=count; }
                    var document=TextBytes.Read(before);
                    if (HostsText.HasBlock(document.Text)) throw new InvalidOperationException("检测到多套转发配置，请先核查重复部署。");
                    byte[] after=document.Encode(LegacyHosts.Remove(document.Text));
                    if (snapshot == null && before.SequenceEqual(after)) return;
                    prepare(); Installation.NoReparse(backups);
                    string stem=Path.Combine(backups,"uninstall-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));
                    File.WriteAllBytes(stem+"-hosts.bak",before);
                    if (snapshot != null) File.WriteAllText(stem+"-task.xml",snapshot);
                    bool touched=false,edited=false;
                    try {
                        if (job.Snapshot() != snapshot) throw new InvalidOperationException("转发任务在检测后发生变化，请重新检测。");
                        touched=true; job.Stop(); verifyStopped();
                        if (!before.SequenceEqual(after)) { edited=true; Write(stream,after); refresh(); }
                        job.Delete();
                        if (job.Snapshot() != null) throw new IOException("任务未能删除，正在恢复原配置。");
                    } catch (Exception original) {
                        try {
                            if (edited) { Write(stream,before); refresh(); }
                            if (touched && snapshot != null) { job.Restore(snapshot); if (wasRunning) job.Start(); }
                        } catch (Exception rollback) { throw new IOException("卸载未完成，自动恢复也遇到问题；备份位于 "+backups+"。恢复错误："+rollback.Message,original); }
                        throw;
                    }
                }
            }
        }
        private static void Write(FileStream stream,byte[] bytes) { stream.Position=0; stream.Write(bytes,0,bytes.Length); stream.SetLength(bytes.Length); stream.Flush(true); }
    }
}
