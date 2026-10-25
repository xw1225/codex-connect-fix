using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RemoteHelper;
public static class NetworkTests {
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static int Port(string host) { var listener = new TcpListener(IPAddress.Parse(host),0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static TcpListener Proxy(int port, bool accept) {
        var listener = new TcpListener(IPAddress.Loopback,port); listener.Start();
        Task.Run(async delegate {
            try { while (true) {
                var client = await listener.AcceptTcpClientAsync();
                var operation = Task.Run(async delegate {
                    using (client) {
                        try {
                            var stream = client.GetStream(); var text = new StringBuilder();
                            byte[] one = new byte[1];
                            while (text.Length < 8192 && !text.ToString().EndsWith("\r\n\r\n")) { if (await stream.ReadAsync(one,0,1) == 0) return; text.Append((char)one[0]); }
                            if (text.ToString() != "CONNECT chatgpt.com:443 HTTP/1.1\r\nHost: chatgpt.com:443\r\n\r\n") return;
                            byte[] response = Encoding.ASCII.GetBytes(accept ? "HTTP/1.1 200 Connection established\r\n\r\n" : "HTTP/1.1 407 Authentication required\r\n\r\n");
                            await stream.WriteAsync(response,0,response.Length); if (!accept) return;
                            byte[] buffer = new byte[8192]; int count;
                            while ((count = await stream.ReadAsync(buffer,0,buffer.Length)) > 0) await stream.WriteAsync(buffer,0,count);
                        } catch (IOException) { } catch (ObjectDisposedException) { }
                    }
                });
            }} catch (ObjectDisposedException) { } catch (SocketException) { }
        });
        return listener;
    }
    private static void Roundtrip(int port) {
        using (var client = new TcpClient()) {
            client.Connect("127.0.0.2",port); var stream = client.GetStream(); stream.ReadTimeout = 7000; stream.WriteTimeout = 7000;
            byte[] sent = new byte[32768], received = new byte[sent.Length]; new Random(13).NextBytes(sent);
            stream.Write(sent,0,sent.Length); int offset = 0;
            while (offset < received.Length) { int size = stream.Read(received,offset,received.Length-offset); if (size == 0) throw new IOException("Relay disconnected"); offset += size; }
            for (int i=0; i<sent.Length; i++) Assert(sent[i] == received[i],"Opaque payload changed");
        }
    }
    private static void ExpectOffline(int port) {
        try { Roundtrip(port); } catch (IOException) { return; } catch (SocketException) { return; }
        throw new Exception("Unavailable proxy looked connected");
    }
    public static int Main() {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        string dir = Path.Combine(root,"test-data","network-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        int relayPort = Port("127.0.0.2"), proxyPort = Port("127.0.0.1");
        string console = Path.Combine(dir,"console.txt");
        string arguments = relayPort + " 127.0.0.1:" + proxyPort + " \"" + Path.Combine(dir,"relay.log") + "\" \"" + console + "\"";
        TcpListener proxy = null; Process child = null;
        try {
            child = Process.Start(new ProcessStartInfo(Path.Combine(root,"RelayHarness.exe"),arguments) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            DateTime deadline = DateTime.UtcNow.AddSeconds(8);
            while (!Tunnel.IsListening(relayPort)) { Assert(!child.HasExited && DateTime.UtcNow < deadline,"Relay failed to start"); Thread.Sleep(50); }
            Assert(File.ReadAllText(console) == "no-console","Relay has an attached console"); Console.WriteLine("PASS WinExe relay runs without a terminal console");
            ExpectOffline(relayPort); Assert(!child.HasExited,"Missing proxy terminated relay"); Console.WriteLine("PASS unavailable proxy leaves relay alive");
            proxy = Proxy(proxyPort,true); Roundtrip(relayPort); Roundtrip(relayPort); Console.WriteLine("PASS fixed CONNECT destination and opaque binary roundtrips");
            proxy.Stop(); proxy = null; ExpectOffline(relayPort); Assert(!child.HasExited,"Proxy outage terminated relay");
            proxy = Proxy(proxyPort,true); Roundtrip(relayPort); Assert(!child.HasExited,"Relay restarted"); Console.WriteLine("PASS proxy stop and restart recover in the same process");
            proxy.Stop(); proxy = Proxy(proxyPort,false); ExpectOffline(relayPort); Assert(!child.HasExited,"CONNECT rejection terminated relay"); Console.WriteLine("PASS rejected proxy handshake fails closed");
            Console.WriteLine("RESULT 5 passed, 0 failed"); return 0;
        } catch (Exception error) { Console.Error.WriteLine("FAIL " + error); return 1; }
        finally { if (proxy != null) proxy.Stop(); if (child != null) { if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); } child.Dispose(); } }
    }
}
