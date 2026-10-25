using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
namespace RemoteHelper {
    public static class Tunnel {
        private static async Task Timed(Task task, int milliseconds) {
            if (await Task.WhenAny(task, Task.Delay(milliseconds)).ConfigureAwait(false) != task) throw new TimeoutException("连接超时");
            await task.ConfigureAwait(false);
        }
        public static async Task<TcpClient> Open(ProxyEndpoint endpoint, int connectTimeout) {
            var address = IPAddress.Parse(endpoint.Host);
            var client = new TcpClient(address.AddressFamily);
            try {
                await Timed(client.ConnectAsync(address, endpoint.Port), connectTimeout).ConfigureAwait(false);
                client.NoDelay = true;
                var stream = client.GetStream();
                byte[] request = Encoding.ASCII.GetBytes("CONNECT chatgpt.com:443 HTTP/1.1\r\nHost: chatgpt.com:443\r\n\r\n");
                await Timed(stream.WriteAsync(request, 0, request.Length), 3000).ConfigureAwait(false);
                byte[] header = new byte[8192];
                int size = 0;
                var deadline = Task.Delay(8000);
                while (true) {
                    if (size == header.Length) throw new IOException("代理响应过长");
                    var read = stream.ReadAsync(header, size, 1);
                    if (await Task.WhenAny(read, deadline).ConfigureAwait(false) != read) throw new TimeoutException("代理握手超时");
                    if (await read.ConfigureAwait(false) == 0) throw new IOException("代理已关闭连接");
                    size++;
                    if (size >= 4 && header[size-4] == 13 && header[size-3] == 10 && header[size-2] == 13 && header[size-1] == 10) break;
                }
                string first = Encoding.ASCII.GetString(header, 0, size).Split('\r')[0];
                if (!(first.StartsWith("HTTP/1.1 200 ") || first.StartsWith("HTTP/1.0 200 ")))
                    throw new IOException(first.Contains("407") ? "代理要求认证，本工具只支持无需认证的本机 HTTP 代理" : "该端口不接受 HTTP CONNECT，请选择 HTTP 或混合端口");
                return client;
            } catch { client.Close(); throw; }
        }
        public static async Task<int> Probe(ProxyEndpoint endpoint) {
            using (var client = await Open(endpoint, 1600).ConfigureAwait(false)) {
                return await ProbeStream(client).ConfigureAwait(false);
            }
        }
        public static async Task<int> ProbeRelay(int port) {
            using (var client = new TcpClient()) {
                await Timed(client.ConnectAsync(IPAddress.Parse("127.0.0.2"),port),2000).ConfigureAwait(false);
                return await ProbeStream(client).ConfigureAwait(false);
            }
        }
        private static Task<int> ProbeStream(TcpClient client) {
                return Task.Run(delegate {
                    // Default validation callback: normal hostname and certificate checks.
                    using (var tls = new SslStream(client.GetStream(), false)) {
                        tls.ReadTimeout = 8000; tls.WriteTimeout = 8000;
                        tls.AuthenticateAsClient("chatgpt.com", null, SslProtocols.Tls12, false);
                        byte[] request = Encoding.ASCII.GetBytes("GET /backend-api/wham/remote/control/server HTTP/1.1\r\nHost: chatgpt.com\r\nConnection: close\r\n\r\n");
                        tls.Write(request, 0, request.Length);
                        var line = new StringBuilder();
                        for (int index = 0; index < 2048; index++) { int value = tls.ReadByte(); if (value < 0 || value == 10) break; line.Append((char)value); }
                        string[] parts = line.ToString().Split(' ');
                        int code;
                        if (parts.Length < 2 || !Int32.TryParse(parts[1], out code)) throw new IOException("无法识别服务响应");
                        if (code != 426 && code != 401 && code != 400 && code != 200) throw new IOException("服务返回 HTTP " + code + "，请先检查代理线路或稍后重试");
                        return code;
                    }
                });
        }
        public static bool IsListening(int port) {
            try {
                using (var client = new TcpClient()) {
                    var connect = client.ConnectAsync(IPAddress.Parse("127.0.0.2"), port);
                    return connect.Wait(600) && client.Connected;
                }
            } catch { return false; }
        }
    }
    public static class Relay {
        public static int Run(ProxyEndpoint endpoint, int port, string logFile) {
            bool created;
            using (var guard = new Mutex(true, "Local\\CodexRemoteRelayHelper-" + port, out created)) {
                if (!created) return 0;
                try {
                    var listener = new TcpListener(IPAddress.Parse("127.0.0.2"), port);
                    listener.Start(64);
                    Log(logFile, "ready local=127.0.0.2:" + port);
                    var capacity = new SemaphoreSlim(64);
                    try {
                        while (true) {
                            var incoming = listener.AcceptTcpClient();
                            if (!capacity.Wait(0)) { incoming.Close(); continue; }
                            var operation = Transfer(incoming, endpoint, logFile, capacity);
                        }
                    } finally { listener.Stop(); }
                } catch (Exception error) { Log(logFile, "fatal " + error.GetType().Name); return 1; }
                finally { guard.ReleaseMutex(); }
            }
        }
        private static readonly object logLock = new object();
        private static void Log(string file, string message) {
            try { lock (logLock) {
                if (File.Exists(file) && new FileInfo(file).Length > 524288) File.WriteAllText(file, "");
                File.AppendAllText(file, DateTime.UtcNow.ToString("o") + " " + message + Environment.NewLine);
            }} catch { /* Logging must not terminate the relay. */ }
        }
        private static async Task Transfer(TcpClient incoming, ProxyEndpoint endpoint, string logFile, SemaphoreSlim capacity) {
            try {
                using (incoming)
                using (var upstream = await Tunnel.Open(endpoint, 3000).ConfigureAwait(false)) {
                    incoming.NoDelay = true;
                    var a = incoming.GetStream().CopyToAsync(upstream.GetStream());
                    var b = upstream.GetStream().CopyToAsync(incoming.GetStream());
                    await Task.WhenAny(a, b).ConfigureAwait(false);
                    incoming.Close(); upstream.Close();
                    try { await Task.WhenAll(a, b).ConfigureAwait(false); } catch { }
                }
            } catch (Exception error) { incoming.Close(); Log(logFile, "connection " + error.GetType().Name); }
            finally { capacity.Release(); }
        }
    }
}
