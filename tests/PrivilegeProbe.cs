using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
public static class PrivilegeProbe {
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool GetTokenInformation(IntPtr token,int kind,IntPtr value,int size,out int needed);
    public static int Main(string[] args) {
        if (args.Length > 0 && args[0] == "hold-output") { System.Threading.Thread.Sleep(15000); return 0; }
        if (args.Length > 0 && args[0] == "features") {
            // Exit the direct CLI but leave a descendant holding both pipe writers.
            using (var child=Process.Start(new ProcessStartInfo(typeof(PrivilegeProbe).Assembly.Location,"hold-output") { UseShellExecute=false,CreateNoWindow=true })) {
                File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME"),"probe-child.txt"),child.Id.ToString());
            }
            Console.WriteLine("respect_system_proxy stable true"); return 0;
        }
        using (var identity=WindowsIdentity.GetCurrent()) {
            int size; GetTokenInformation(identity.Token,25,IntPtr.Zero,0,out size); IntPtr buffer=Marshal.AllocHGlobal(size);
            try {
                if (!GetTokenInformation(identity.Token,25,buffer,size,out size)) return 2;
                string integrity=new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
                Console.WriteLine(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) ? "admin" : "standard");
                Console.WriteLine(integrity); Console.WriteLine(identity.User.Value);
                Console.Error.WriteLine("stderr redirected");
            } finally { Marshal.FreeHGlobal(buffer); }
        }
        return 0;
    }
}
