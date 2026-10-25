using System;
using System.IO;
using System.Runtime.InteropServices;
using RemoteHelper;
public static class RelayHarness {
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    public static int Main(string[] args) {
        File.WriteAllText(args[3],GetConsoleWindow() == IntPtr.Zero ? "no-console" : "console");
        ProxyEndpoint endpoint;
        if (!ProxyEndpoint.TryParse(args[1],out endpoint)) return 2;
        return Relay.Run(endpoint,Int32.Parse(args[0]),args[2]);
    }
}
