using System;
using RemoteHelper;
public static class DiagnosticsTests {
    static int passed, failed;
    static void Check(bool value,string message) { if (!value) throw new Exception(message); }
    static void Test(string name,Action test) { try { test(); passed++; Console.WriteLine("PASS " + name); } catch(Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); } }
    public static int Main() {
        Test("legacy relay is identified as deployed and healthy",delegate {
            var state = StatusRules.Relay(new RelayEvidence { LegacyTask=true,LegacyMapping=true,Running=true,MatchingListener=true,Listening=true,TunnelHealthy=true });
            Check(state.Good,"existing relay falsely reported unhealthy");
            Check(!state.CanDeploy && state.CanUninstall,"recognized existing relay must be removable without duplicate deployment");
        });
        Test("installed relay with failed route is not healthy",delegate {
            var state = StatusRules.Relay(new RelayEvidence { ManagedTask=true,ManagedMapping=true,Running=true,MatchingListener=true,Listening=true,TunnelHealthy=false });
            Check(!state.Good && state.Title.Contains("通路异常") && state.CanUninstall,"bad route looked healthy");
        });
        Test("orphan mapping and inspection error are not uninstalled",delegate {
            var orphan = StatusRules.Relay(new RelayEvidence { LegacyMapping=true });
            Check(orphan.Title.Contains("残留") && !orphan.CanDeploy,"orphan was missed");
            Check(!StatusRules.Relay(new RelayEvidence { Error="无法读取任务" }).Title.Contains("未部署"),"access error looked absent");
        });
        Test("empty machine can deploy",delegate {
            var result = StatusRules.Relay(new RelayEvidence()); Check(result.CanDeploy && !result.CanUninstall && result.Title.Contains("未部署"),"wrong empty state");
        });
        Test("enabled proxy and passed checks distinguish configuration from symptom",delegate {
            var result = StatusRules.Reconnect(new ReconnectEvidence { SameUser=true,ProxyEnabled=true,Http="通过",WebSocket="通过" });
            Check(result.Good && result.Title.Contains("已启用") && result.Detail.Contains("重开 Codex") && !result.Title.Contains("已修复"),"overstated symptom fix");
        });
        Test("enabled proxy with failed websocket calls for investigation",delegate {
            var result = StatusRules.Reconnect(new ReconnectEvidence { SameUser=true,ProxyEnabled=true,Http="通过",WebSocket="未通过" });
            Check(!result.Good && result.Title.Contains("异常"),"websocket failure hidden");
        });
        Test("same desktop identity is allowed regardless of elevation",delegate {
            Check(DesktopIdentity.SameUser("S-1-5-21-1","S-1-5-21-1"),"same user blocked");
            Check(!DesktopIdentity.SameUser("S-1-5-21-1","S-1-5-21-2") && !DesktopIdentity.SameUser("S-1-5-21-1",null),"unknown or different user allowed");
        });
        Test("diagnostic labels require exact pass status",delegate {
            Check(DiagnosticInspector.ReadCheck("HTTP 通道：未通过\r\nWebSocket 通道：通过\r\n","HTTP 通道") == "未通过","failure parsed as pass");
            Check(DiagnosticInspector.ReadCheck("unavailable","HTTP 通道") == "未能确认","missing result parsed as pass");
        });
        Test("hosts diagnostics reject conflicts and broken legacy markers",delegate {
            string legacy="# BEGIN CodexRemoteProxyFix\r\n127.0.0.2 chatgpt.com\r\n# END CodexRemoteProxyFix\r\n";
            Check(DiagnosticInspector.ValidateMappings(legacy)[1],"valid legacy missing");
            foreach (string text in new[] {legacy+"127.0.0.3 chatgpt.com\r\n",legacy+legacy,legacy+"# BEGIN CodexRemoteProxyFix\n",HostsText.Block+"127.0.0.3 chatgpt.com\r\n"}) {
                bool rejected=false; try { DiagnosticInspector.ValidateMappings(text); } catch(InvalidOperationException) { rejected=true; }
                Check(rejected,"conflicting hosts looked healthy");
            }
        });
        Test("failed mutation refreshes restore availability and preserves original error",delegate {
            bool hasBackup=false,canRestore=false; var original=new InvalidOperationException("mutation failure"); Exception seen=null;
            try { DiagnosticInspector.MutateAndRefresh(delegate { hasBackup=true; throw original; },delegate {
                canRestore=StatusRules.Reconnect(new ReconnectEvidence { SameUser=true,HasBackup=hasBackup }).CanRestore;
                throw new Exception("refresh failure");
            }).GetAwaiter().GetResult(); } catch(Exception e) { seen=e; }
            Check(canRestore && Object.ReferenceEquals(original,seen),"refresh or original error was lost");
        });
        Console.WriteLine("RESULT " + passed + " passed, " + failed + " failed"); return failed == 0 ? 0 : 1;
    }
}
