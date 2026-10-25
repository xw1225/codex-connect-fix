using System;
using RemoteHelper;
public static class CoreTests {
    private static int passed, failed;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Test(string name, Action body) {
        try { body(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
    }
    private static void Reject(Action body) {
        bool rejected = false;
        try { body(); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "expected a safe refusal");
    }
    public static int Main() {
        Test("accept local HTTP endpoint", delegate {
            ProxyEndpoint endpoint;
            Check(ProxyEndpoint.TryParse("http://localhost:15236", out endpoint), "valid endpoint rejected");
            Check(endpoint.Host == "127.0.0.1" && endpoint.Port == 15236, "wrong normalization");
        });
        Test("accept a port entered by a novice", delegate {
            ProxyEndpoint endpoint;
            Check(ProxyEndpoint.TryParse("7890", out endpoint) && endpoint.Port == 7890, "port shorthand rejected");
        });
        Test("reject external, credentialed, SOCKS, and invalid ports", delegate {
            foreach (string value in new[] { "https://127.0.0.1:80", "socks5://127.0.0.1:1080", "proxy.example:80", "192.168.1.2:80", "user:pass@127.0.0.1:80", "0", "65536", "127.0.0.1:80/path", "127.0.0.1:80?x=1" }) {
                ProxyEndpoint endpoint; Check(!ProxyEndpoint.TryParse(value, out endpoint), "accepted: " + value);
            }
        });
        Test("read per-scheme Windows proxy setting", delegate {
            var endpoints = ProxyDiscovery.ParseSetting("http=127.0.0.1:7890;https=localhost:15236;socks=127.0.0.1:1080");
            Check(endpoints.Count == 2 && endpoints[0].Port == 15236 && endpoints[1].Port == 7890, "scheme parsing failed");
        });
        Test("hosts install then uninstall preserves exact original", delegate {
            foreach (string original in new[] { "", "# 用户备注\r\n127.0.0.1 localhost\r\n", "127.0.0.1 localhost", "# unix\n::1 localhost\n" }) {
                string installed = HostsText.Add(original);
                Check(installed.Contains("127.0.0.2 chatgpt.com"), "mapping missing");
                Check(HostsText.Remove(installed) == original, "original text changed");
                Check(HostsText.Add(installed) == installed, "not idempotent");
            }
        });
        Test("preserve unrelated edits made after installation", delegate {
            string installed = HostsText.Add("127.0.0.1 localhost\r\n") + "10.1.1.1 intranet\r\n";
            Check(HostsText.Remove(installed) == "127.0.0.1 localhost\r\n10.1.1.1 intranet\r\n", "unrelated edit lost");
        });
        Test("refuse existing domain mappings but ignore comments", delegate {
            Reject(delegate { HostsText.Add("127.0.0.1 example CHATGPT.COM # existing\n"); });
            Check(HostsText.Add("# 127.0.0.1 chatgpt.com\n").Contains("127.0.0.2 chatgpt.com"), "comment treated as mapping");
        });
        Test("refuse partial, duplicate, or edited owned blocks", delegate {
            Reject(delegate { HostsText.Remove(HostsText.Begin + "\nchanged\n"); });
            Reject(delegate { HostsText.Remove(HostsText.Add("") + HostsText.Add("")); });
            Reject(delegate { HostsText.Remove(HostsText.Begin + "\r\n127.0.0.3 chatgpt.com\r\n" + HostsText.End + "\r\n"); });
        });
        Test("concurrent operations refuse safely and release after completion",delegate {
            string name = "Local\\CodexConnectionHelper-Test-" + Guid.NewGuid().ToString("N");
            using (OperationLock.Acquire(name)) {
                System.Threading.Tasks.Task.Run(delegate { Reject(delegate { using (OperationLock.Acquire(name)) { } }); }).GetAwaiter().GetResult();
            }
            using (OperationLock.Acquire(name)) { }
        });
        Console.WriteLine("RESULT " + passed + " passed, " + failed + " failed");
        return failed == 0 ? 0 : 1;
    }
}
