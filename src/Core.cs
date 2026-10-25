using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
namespace RemoteHelper {
    public sealed class OperationLock : IDisposable {
        private readonly System.Threading.Mutex mutex;
        private OperationLock(System.Threading.Mutex mutex) { this.mutex = mutex; }
        public static OperationLock Acquire(string name) {
            var mutex = new System.Threading.Mutex(false,name);
            bool owned;
            try { owned = mutex.WaitOne(0); } catch (System.Threading.AbandonedMutexException) { owned = true; }
            if (!owned) { mutex.Dispose(); throw new InvalidOperationException("另一个工具正在修改设置，请等待它完成后重试。"); }
            return new OperationLock(mutex);
        }
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
    public sealed class ProxyEndpoint {
        public string Host; public int Port;
        public static bool TryParse(string text, out ProxyEndpoint endpoint) {
            endpoint = null;
            if (String.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            int shorthand;
            if (Int32.TryParse(text, out shorthand)) text = "127.0.0.1:" + text;
            Uri uri;
            if (!Uri.TryCreate(text.Contains("://") ? text : "http://" + text, UriKind.Absolute, out uri)) return false;
            if (uri.Scheme != "http" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/") return false;
            string host = uri.Host.Trim('[', ']');
            IPAddress address;
            if (String.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) host = "127.0.0.1";
            if (!IPAddress.TryParse(host, out address) || !IPAddress.IsLoopback(address) || uri.Port < 1 || uri.Port > 65535) return false;
            endpoint = new ProxyEndpoint { Host = address.ToString(), Port = uri.Port };
            return true;
        }
        public override string ToString() { return (Host.Contains(":") ? "[" + Host + "]" : Host) + ":" + Port; }
    }
    public static class ProxyDiscovery {
        public static List<ProxyEndpoint> ParseSetting(string value) {
            var result = new List<ProxyEndpoint>();
            if (String.IsNullOrWhiteSpace(value)) return result;
            var values = new List<string>();
            if (value.Contains("=")) {
                foreach (string scheme in new[] { "https", "http" }) {
                    foreach (string entry in value.Split(';')) {
                        string[] pair = entry.Split(new[] { '=' }, 2);
                        if (pair.Length == 2 && String.Equals(pair[0].Trim(), scheme, StringComparison.OrdinalIgnoreCase)) values.Add(pair[1]);
                    }
                }
            } else values.Add(value);
            foreach (string candidate in values) {
                ProxyEndpoint endpoint;
                if (ProxyEndpoint.TryParse(candidate, out endpoint) && !result.Any(e => e.ToString() == endpoint.ToString())) result.Add(endpoint);
            }
            return result;
        }
        public static List<ProxyEndpoint> Candidates() {
            var result = new List<ProxyEndpoint>();
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings")) {
                if (key != null && Convert.ToInt32(key.GetValue("ProxyEnable", 0)) == 1)
                    result.AddRange(ParseSetting(Convert.ToString(key.GetValue("ProxyServer", ""))));
            }
            foreach (int port in new[] { 15236, 7890, 7897, 10809, 8080 }) {
                if (!result.Any(e => e.Host == "127.0.0.1" && e.Port == port)) result.Add(new ProxyEndpoint { Host = "127.0.0.1", Port = port });
            }
            return result;
        }
    }
    public static class HostsText {
        public const string Begin = "# BEGIN CodexRemoteRelayHelper";
        public const string End = "# END CodexRemoteRelayHelper";
        public const string Block = Begin + "\r\n127.0.0.2 chatgpt.com\r\n" + End + "\r\n";
        private static Match OwnedMatch(string text) {
            var matches = Regex.Matches(text, "(?m)^" + Regex.Escape(Begin) + "\\r?\\n127\\.0\\.0\\.2[ \\t]+chatgpt\\.com\\r?\\n" + Regex.Escape(End) + "(?:\\r?\\n|$)");
            int begins = Regex.Matches(text, Regex.Escape(Begin)).Count;
            int ends = Regex.Matches(text, Regex.Escape(End)).Count;
            if (begins == 0 && ends == 0) return null;
            if (begins != 1 || ends != 1 || matches.Count != 1) throw new InvalidOperationException("本工具的 hosts 标记已被改动或重复，请先人工核查；未覆盖现有内容。");
            return matches[0];
        }
        public static string Add(string text) {
            Match match = OwnedMatch(text);
            string remaining = match == null ? text : text.Remove(match.Index, match.Length);
            foreach (string line in remaining.Split('\n')) {
                string active = line.Split('#')[0].Trim();
                string[] tokens = Regex.Split(active, @"\s+");
                if (tokens.Skip(1).Any(t => String.Equals(t, "chatgpt.com", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("已存在其他 chatgpt.com 映射。请先由原工具撤销，避免覆盖其他设置。");
            }
            return match == null ? Block + text : text;
        }
        public static string Remove(string text) {
            Match match = OwnedMatch(text);
            return match == null ? text : text.Remove(match.Index, match.Length);
        }
        public static bool HasBlock(string text) { return OwnedMatch(text) != null; }
    }
    public sealed class TextBytes {
        public string Text;
        private Encoding encoding;
        private byte[] prefix;
        public static TextBytes Read(byte[] bytes) {
            Encoding encoding;
            int skip;
            if (bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191) { encoding = new UTF8Encoding(false, true); skip = 3; }
            else if (bytes.Length >= 2 && bytes[0] == 255 && bytes[1] == 254) { encoding = new UnicodeEncoding(false, false, true); skip = 2; }
            else if (bytes.Length >= 2 && bytes[0] == 254 && bytes[1] == 255) { encoding = new UnicodeEncoding(true, false, true); skip = 2; }
            else {
                skip = 0;
                encoding = new UTF8Encoding(false, true);
                try { encoding.GetString(bytes); }
                catch (DecoderFallbackException) { encoding = Encoding.GetEncoding(Encoding.Default.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); }
            }
            return new TextBytes { Text = encoding.GetString(bytes, skip, bytes.Length - skip), encoding = encoding, prefix = bytes.Take(skip).ToArray() };
        }
        public byte[] Encode(string text) { return prefix.Concat(encoding.GetBytes(text)).ToArray(); }
    }
    [DataContract]
    public sealed class RelayConfig {
        [DataMember] public int Schema = 1;
        [DataMember] public string Proxy = "";
        [DataMember] public string OwnerSid = "";
        public ProxyEndpoint Endpoint() {
            ProxyEndpoint endpoint;
            if (Schema != 1 || !ProxyEndpoint.TryParse(Proxy, out endpoint)) throw new InvalidOperationException("配置无效，请重新安装/修复。");
            new System.Security.Principal.SecurityIdentifier(OwnerSid);
            return endpoint;
        }
        public static RelayConfig Read(string file) {
            using (var stream = File.OpenRead(file)) {
                var config = (RelayConfig)new DataContractJsonSerializer(typeof(RelayConfig)).ReadObject(stream);
                config.Endpoint(); return config;
            }
        }
        public byte[] Bytes() {
            Endpoint();
            using (var stream = new MemoryStream()) { new DataContractJsonSerializer(typeof(RelayConfig)).WriteObject(stream, this); return stream.ToArray(); }
        }
    }
}
