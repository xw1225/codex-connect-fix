using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Runtime.ExceptionServices;
namespace RemoteHelper {
    public sealed class RelayEvidence {
        public bool ManagedTask,LegacyTask,ManagedMapping,LegacyMapping,Running,Listening,MatchingListener;
        public bool? TunnelHealthy;
        public string Error,Details;
    }
    public sealed class ReconnectEvidence {
        public bool SameUser,HasBackup;
        public bool? ProxyEnabled;
        public string Http="未能确认",WebSocket="未能确认",Error,Details;
    }
    public sealed class StatusView {
        public string Title,Detail;
        public bool Good,CanDeploy,CanUninstall,CanRepair,CanRestore;
    }
    public static class StatusRules {
        public static StatusView Relay(RelayEvidence value) {
            var result = new StatusView();
            if (!String.IsNullOrEmpty(value.Error)) { result.Title="转发器状态未能确认"; result.Detail=value.Error; return result; }
            if ((value.ManagedTask || value.ManagedMapping) && (value.LegacyTask || value.LegacyMapping)) {
                result.Title="检测到多套转发配置"; result.Detail="请先核查重复的任务与映射，避免覆盖正在使用的转发器。"; return result;
            }
            bool legacy=value.LegacyTask || value.LegacyMapping, task=value.ManagedTask || value.LegacyTask;
            bool mapping=legacy ? value.LegacyMapping : value.ManagedMapping;
            result.CanUninstall=task || mapping;
            if (!task && !mapping) {
                result.Title=value.Listening ? "转发地址被其他程序占用" : "尚未部署转发器";
                result.Detail=value.Listening ? "请先确认占用程序，再进行部署。" : "遇到无法添加远程设备时，可部署后台转发器。";
                result.CanDeploy=!value.Listening; return result;
            }
            if (!task) { result.Title="检测到转发映射残留"; result.Detail="hosts 中仍有转发配置，但没有对应任务。可点击一键卸载转发器撤销。"; return result; }
            result.CanDeploy=!legacy;
            string prefix="转发器";
            if (!mapping) { result.Title=prefix+"已部署，映射缺失"; result.Detail="启动任务存在，但 chatgpt.com 尚未指向转发器。"; }
            else if (!value.Running || !value.MatchingListener) { result.Title=prefix+"已部署，后台未正常运行"; result.Detail="任务存在，但没有确认到它在监听转发地址。"; }
            else if (value.TunnelHealthy == false) { result.Title=prefix+"运行中，连接通路异常"; result.Detail="后台进程仍在，请检查代理是否开启、端口及线路是否可用。"; }
            else { result.Good=value.TunnelHealthy == true; result.Title=result.Good ? "转发器运行正常" : "转发器运行中"; result.Detail=result.Good ? "后台与加密通路正常，可继续使用，无需重复部署。需要移除时点击一键卸载转发器。" : "任务与监听正常，加密通路尚未检查。"; }
            return result;
        }
        public static StatusView Reconnect(ReconnectEvidence value) {
            var result = new StatusView { CanRestore=value.SameUser && value.HasBackup };
            if (!value.SameUser) { result.Title="尚未检查桌面用户的重连设置"; result.Detail="当前运行账户与桌面登录用户不同或无法核实，请用桌面用户打开工具。"; return result; }
            result.CanRepair=value.ProxyEnabled != true;
            if (value.ProxyEnabled == null) { result.Title="系统代理设置未能确认"; result.Detail=value.Error ?? "请展开诊断详情查看原因。"; return result; }
            string network="HTTP："+value.Http+" · WebSocket："+value.WebSocket+"。";
            if (value.ProxyEnabled == false) { result.Title="系统代理支持尚未启用"; result.Detail=network+"开启系统代理后，可应用重连修复。"; }
            else if (value.Http == "未通过" || value.WebSocket == "未通过") { result.Title="代理设置已启用，连接检查异常"; result.Detail=network+"无需重复修改同一开关，请排查网络线路。"; }
            else if (value.Http == "通过" && value.WebSocket == "通过") { result.Title="代理设置已启用，当前通道正常"; result.Good=true; result.Detail=network+"请重开 Codex 后确认是否还出现重连 5 次。"; }
            else { result.Title="代理设置已启用，连接结果待确认"; result.Detail=network+(value.Error ?? "请刷新检测后查看结果。"); }
            result.Detail += value.HasBackup ? " 可还原本工具保存的设置。" : " 没有本工具的待还原记录。";
            return result;
        }
    }
    public static class DesktopIdentity {
        [DllImport("wtsapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool WTSQuerySessionInformation(IntPtr server,int sessionId,int info,out IntPtr buffer,out int bytes);
        [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
        private static string Query(int info) {
            IntPtr buffer; int bytes;
            if (!WTSQuerySessionInformation(IntPtr.Zero,Process.GetCurrentProcess().SessionId,info,out buffer,out bytes)) throw new IOException("无法核实桌面登录账户。");
            try { return Marshal.PtrToStringUni(buffer); } finally { WTSFreeMemory(buffer); }
        }
        public static bool SameUser(string currentSid,string desktopSid) { return !String.IsNullOrEmpty(currentSid) && !String.IsNullOrEmpty(desktopSid) && String.Equals(currentSid,desktopSid,StringComparison.OrdinalIgnoreCase); }
        public static bool IsCurrentDesktopUser() {
            try {
                string user=Query(5), domain=Query(7);
                if (String.IsNullOrWhiteSpace(user)) return false;
                var account=String.IsNullOrEmpty(domain) ? new NTAccount(user) : new NTAccount(domain,user);
                string desktopSid=((SecurityIdentifier)account.Translate(typeof(SecurityIdentifier))).Value;
                return SameUser(WindowsIdentity.GetCurrent().User.Value,desktopSid);
            } catch { return false; }
        }
    }
    public static class DiagnosticInspector {
        public static bool[] ValidateMappings(string hosts) {
            bool modern=HostsText.HasBlock(hosts);
            string remaining=HostsText.Remove(hosts);
            bool legacy=LegacyHosts.HasBlock(remaining);
            remaining=LegacyHosts.Remove(remaining);
            // The shared validator rejects any active chatgpt.com alias outside the owned blocks.
            HostsText.Add(remaining);
            return new[] {modern,legacy};
        }
        public static async Task<string> MutateAndRefresh(Func<string> mutation,Func<Task> refresh) {
            string result=null; Exception original=null;
            try { result=await Task.Run(mutation); } catch (Exception e) { original=e; }
            try { await refresh(); } catch (Exception e) { if (original == null) original=e; }
            if (original != null) ExceptionDispatchInfo.Capture(original).Throw();
            return result;
        }
        private static dynamic ReadTask(dynamic folder,string name) {
            try { return folder.GetTask(name); }
            catch (FileNotFoundException e) { if (e.HResult == unchecked((int)0x80070002)) return null; throw; }
            catch (COMException e) { if (e.HResult == unchecked((int)0x80070002)) return null; throw; }
        }
        public static RelayEvidence Relay() {
            var result=new RelayEvidence(); var layout=Layout.Production();
            try {
                string hosts=TextBytes.Read(File.ReadAllBytes(layout.HostsPath)).Text;
                bool[] mappings=ValidateMappings(hosts); result.ManagedMapping=mappings[0]; result.LegacyMapping=mappings[1];
                dynamic service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")); service.Connect(); dynamic folder=service.GetFolder(@"\");
                dynamic modern=ReadTask(folder,layout.TaskName), legacy=ReadTask(folder,"CodexRemoteProxyFix");
                string executable=null;
                if (modern != null) {
                    new ScheduledJob(layout).ValidateOwner(null);
                    result.ManagedTask=true; result.Running=(int)modern.State == 4; executable=layout.Executable;
                }
                if (legacy != null) {
                    var identity=LegacyTaskInfo.Parse((string)legacy.Xml);
                    result.LegacyTask=true;
                    if (modern == null) { result.Running=(int)legacy.State == 4; executable=identity.Executable; }
                }
                int pid=WindowsSystem.ListenerProcess(443); result.Listening=pid != 0;
                if (pid != 0 && executable != null) {
                    using (var process=Process.GetProcessById(pid)) result.MatchingListener=String.Equals(process.MainModule.FileName,executable,StringComparison.OrdinalIgnoreCase);
                }
                string tasks=result.ManagedTask && result.LegacyTask ? layout.TaskName+"、"+LegacyTaskInfo.TaskName : result.ManagedTask ? layout.TaskName : result.LegacyTask ? LegacyTaskInfo.TaskName : "未检测到";
                result.Details="已检测到的任务："+tasks+"\r\n后台状态："+(result.Running ? "运行中" : "未运行")+"\r\nhosts 映射："+(result.ManagedMapping || result.LegacyMapping ? "存在" : "不存在")+"\r\n监听进程："+(result.MatchingListener ? "与任务匹配" : result.Listening ? "其他程序" : "未监听");
                if (result.MatchingListener) {
                    try { int code=Tunnel.ProbeRelay(443).GetAwaiter().GetResult(); result.TunnelHealthy=true; result.Details+="\r\n转发加密通路：通过（HTTP "+code+(code == 426 ? "，服务入口已响应；此探测未发起 WebSocket 升级" : "")+"）"; }
                    catch { result.TunnelHealthy=false; result.Details+="\r\n转发加密通路：未通过"; }
                }
            } catch (Exception e) { result.Error=e.Message; }
            return result;
        }
        public static string ReadCheck(string report,string label) {
            var match=Regex.Match(report ?? "","(?m)^"+Regex.Escape(label)+"：(通过|未通过|需要留意|未执行|未能确认)\\r?$");
            return match.Success ? match.Groups[1].Value : "未能确认";
        }
        public static ReconnectEvidence Reconnect() {
            var result=new ReconnectEvidence { SameUser=DesktopIdentity.IsCurrentDesktopUser() };
            if (!result.SameUser) return result;
            try {
                var cli=new CodexCli(CodexCli.Find());
                var fix=new ReconnectFix(ReconnectFix.ConfigFile(),ReconnectFix.BackupDirectory(),cli);
                result.HasBackup=fix.HasPendingRestore();
                try { result.ProxyEnabled=cli.ProxyFlag(); } catch(Exception e) { result.Error=e.Message; }
                try { result.Details=cli.Diagnose(); result.Http=ReadCheck(result.Details,"HTTP 通道"); result.WebSocket=ReadCheck(result.Details,"WebSocket 通道"); }
                catch(Exception e) { result.Error=e.Message; }
                result.Details="桌面用户身份：已核实\r\n系统代理开关："+(result.ProxyEnabled == true ? "已启用" : result.ProxyEnabled == false ? "未启用" : "未能确认")+"\r\n可还原记录："+(result.HasBackup ? "存在" : "无")+"\r\n"+result.Details;
            } catch(Exception e) { result.Error=e.Message; }
            return result;
        }
    }
}
