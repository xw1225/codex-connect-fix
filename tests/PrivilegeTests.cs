using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using RemoteHelper;
public static class PrivilegeTests {
    public static int Main() {
        try {
            var info=new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"PrivilegeProbe.exe")) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
            using (var child=CliProcess.Start(info)) {
                var output=child.Output.ReadToEndAsync(); var errors=child.Errors.ReadToEndAsync();
                if (!child.WaitForExit(10000)) { child.Kill(); throw new Exception("child timeout"); }
                string[] lines=output.GetAwaiter().GetResult().Split(new[] {'\r','\n'},StringSplitOptions.RemoveEmptyEntries);
                if (child.ExitCode != 0 || lines.Length != 3 || lines[0] != "standard" || lines[1] != "S-1-16-8192" || lines[2] != WindowsIdentity.GetCurrent().User.Value || !errors.GetAwaiter().GetResult().Contains("stderr redirected")) throw new Exception("child token or redirected pipes are incorrect");
            }
            Console.WriteLine("PASS CLI child is same user, non-admin, medium integrity, with redirected output");
            string fixture=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-data",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
            var watch=Stopwatch.StartNew(); bool timedOut=false;
            try { new CodexCli(info.FileName,fixture,1000).ProxyFlag(); } catch(TimeoutException) { timedOut=true; }
            if (!timedOut || watch.ElapsedMilliseconds > 5000) throw new Exception("pipe EOF timeout did not return promptly");
            int descendant=int.Parse(File.ReadAllText(Path.Combine(fixture,"probe-child.txt")));
            try { using (var process=Process.GetProcessById(descendant)) { if (!process.WaitForExit(1000)) throw new Exception("descendant survived timeout"); } } catch (ArgumentException) { }
            Console.WriteLine("PASS direct CLI exit does not bypass pipe deadline; descendant is terminated");
            return 0;
        } catch(Exception e) { var native=e as System.ComponentModel.Win32Exception; Console.Error.WriteLine("FAIL " + e.Message + (native == null ? "" : " (Win32 " + native.NativeErrorCode + ")") + "\n" + e.StackTrace); return 1; }
    }
}
