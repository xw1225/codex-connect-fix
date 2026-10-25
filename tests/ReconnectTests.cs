using System;
using System.IO;
using System.Text;
using RemoteHelper;
public static class ReconnectTests {
    private sealed class Cli : ICodexCli {
        public string File; public bool Flag, Unsupported, ConcurrentEdit;
        public bool ProxyFlag() { if (Unsupported) throw new InvalidOperationException("unsupported"); return Flag; }
        public void SetProxyFlag(bool value) {
            Flag = value;
            string text = System.IO.File.Exists(File) ? System.IO.File.ReadAllText(File) : "[features]\r\n";
            text = System.Text.RegularExpressions.Regex.Replace(text,@"(?m)^respect_system_proxy\s*=.*\r?\n?","");
            System.IO.File.WriteAllText(File,text + "respect_system_proxy = " + (value ? "true" : "false") + "\r\n",new UTF8Encoding(false));
            if (ConcurrentEdit && value) System.IO.File.AppendAllText(File,"# another writer added a provider\r\n");
        }
        public string Diagnose() { return "diagnostic"; }
    }
    private static int passed,failed;
    private static void Check(bool ok,string message) { if (!ok) throw new Exception(message); }
    private static void Test(string name,Action action) { try { action();passed++;Console.WriteLine("PASS " + name); } catch(Exception e) { failed++;Console.WriteLine("FAIL " + name + ": " + e.Message); } }
    public static int Main() {
        Test("restore only proxy flag and preserve later provider edits",delegate {
            string root = NewRoot(); string config = Path.Combine(root,"config.toml");
            File.WriteAllText(config,"model_provider = \"openai\"\r\n[features]\r\nrespect_system_proxy = false\r\n");
            var cli = new Cli { File=config }; var fix = new ReconnectFix(config,Path.Combine(root,"backup"),cli);
            fix.Repair(); Check(cli.Flag,"flag not enabled");
            File.AppendAllText(config,"# added later\r\n[model_providers.custom]\r\nname = \"keep me\"\r\n");
            fix.Restore(); string text = File.ReadAllText(config);
            Check(!cli.Flag && text.Contains("# added later") && text.Contains("keep me") && text.Contains("model_provider = \"openai\""),"unrelated config lost");
        });
        Test("already enabled and unsupported features do not change config",delegate {
            string root = NewRoot(); string config = Path.Combine(root,"config.toml"); File.WriteAllText(config,"# preserve");
            var cli = new Cli { File=config,Flag=true }; var fix = new ReconnectFix(config,Path.Combine(root,"backup"),cli);
            fix.Repair(); Check(File.ReadAllText(config) == "# preserve" && !Directory.Exists(Path.Combine(root,"backup")),"changed already-enabled config");
            cli.Unsupported = true; try { fix.Repair(); } catch(InvalidOperationException) {}
            Check(File.ReadAllText(config) == "# preserve","unsupported version changed config");
        });
        Test("new config restores only proxy flag",delegate {
            string root = NewRoot(); string config = Path.Combine(root,"config.toml"); var cli = new Cli { File=config };
            var fix = new ReconnectFix(config,Path.Combine(root,"backup"),cli); fix.Repair(); fix.Restore();
            Check(File.Exists(config) && !cli.Flag,"new config did not restore proxy behavior");
        });
        Test("restore after user deleted new config does not recreate it",delegate {
            string root = NewRoot(); string config = Path.Combine(root,"config.toml"); var cli = new Cli { File=config };
            var fix = new ReconnectFix(config,Path.Combine(root,"backup"),cli); fix.Repair(); File.Delete(config); fix.Restore();
            Check(!File.Exists(config),"restore recreated a deleted config");
        });
        Test("restore preserves edits made before repair CLI returned",delegate {
            string root = NewRoot(); string config = Path.Combine(root,"config.toml"); var cli = new Cli { File=config,ConcurrentEdit=true };
            var fix = new ReconnectFix(config,Path.Combine(root,"backup"),cli); fix.Repair(); fix.Restore();
            Check(File.ReadAllText(config).Contains("another writer"),"concurrent content was deleted");
        });
        Test("restore respects deletion of originally existing config",delegate {
            string root = NewRoot(); string config = Path.Combine(root,"config.toml"); File.WriteAllText(config,"# existing"); var cli = new Cli { File=config };
            var fix = new ReconnectFix(config,Path.Combine(root,"backup"),cli); fix.Repair(); File.Delete(config); fix.Restore();
            Check(!File.Exists(config),"deleted original config was recreated");
        });
        Test("doctor status mapping accepts actual CLI values without secrets",delegate {
            string report = "{\"checks\":{\"config.load\":{\"status\":\"warning\"},\"auth.credentials\":{\"status\":\"ok\",\"detail\":\"secret-value\"},\"network.provider_reachability\":{\"status\":\"error\"},\"network.websocket_reachability\":{\"status\":\"skipped\"}}}";
            string result = CodexCli.FormatDiagnostic(report);
            Check(result.Contains("配置读取：需要留意") && result.Contains("登录凭据：通过") && result.Contains("HTTP 通道：未通过") && result.Contains("WebSocket 通道：未执行"),"incorrect status mapping");
            Check(!result.Contains("secret-value"),"diagnostic leaked details");
            Check(CodexCli.FormatDiagnostic("{}").Contains("不能据此判断连接已恢复"),"malformed report looked healthy");
        });
        Console.WriteLine("RESULT " + passed + " passed, " + failed + " failed"); return failed == 0 ? 0 : 1;
    }
    private static string NewRoot() { string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-data",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); return dir; }
}
