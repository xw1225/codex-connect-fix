// Disposable task fixture. Never uses the production relay port or hosts file.
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
public static class LegacyRemovalProbe {
    public static int Main(string[] args) {
        int port;
        if (args.Length != 2 || args[0] != "--listen-port" || !Int32.TryParse(args[1],out port) || port < 1024 || port > 65535) return 1;
        var listener=new TcpListener(IPAddress.Parse("127.0.0.2"),port);
        try { listener.Start(); Thread.Sleep(Timeout.Infinite); return 0; }
        finally { listener.Stop(); }
    }
}
