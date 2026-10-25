using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
namespace RemoteHelper {
    public static class WindowsSystem {
        private static readonly SecurityIdentifier Admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);
        private static readonly SecurityIdentifier SystemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null);
        public static bool IsAdmin { get { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); } }
        public static void ValidateTrustedDirectories(Layout layout) {
            foreach (string dir in new[] { layout.InstallDir, layout.DataDir }) {
                Installation.NoReparse(dir);
                if (!Directory.Exists(dir)) continue;
                var security = Directory.GetAccessControl(dir);
                var owner = (SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier));
                if (!owner.Equals(Admins) && !owner.Equals(SystemSid)) throw new InvalidOperationException("同名目录并非由管理员创建，已停止操作：" + dir);
                foreach (FileSystemAccessRule rule in security.GetAccessRules(true,true,typeof(SecurityIdentifier))) {
                    var identity = (SecurityIdentifier)rule.IdentityReference;
                    var writeRights = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
                    if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & writeRights) != 0 && !identity.Equals(Admins) && !identity.Equals(SystemSid))
                        throw new InvalidOperationException("安装目录权限过宽，请由管理员核查：" + dir);
                }
            }
        }
        private static void Protect(string directory, string logOwner) {
            Installation.NoReparse(directory);
            Directory.CreateDirectory(directory);
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true,false);
            acl.SetOwner(Admins);
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            foreach (var sid in new[] { Admins, SystemSid }) acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,inherit,PropagationFlags.None,AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null),FileSystemRights.ReadAndExecute,inherit,PropagationFlags.None,AccessControlType.Allow));
            if (logOwner != null) acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(logOwner),FileSystemRights.Modify,inherit,PropagationFlags.None,AccessControlType.Allow));
            Directory.SetAccessControl(directory,acl);
        }
        public static void Prepare(Layout layout, string sid) {
            ValidateTrustedDirectories(layout);
            Protect(layout.InstallDir,null); Protect(layout.DataDir,null); Protect(layout.LogDir,sid);
        }
        public static void PrepareUninstallBackups(string directory) {
            ValidateTrustedDirectories(new Layout { InstallDir=directory,DataDir=directory }); Protect(directory,null);
        }
        [DllImport("dnsapi.dll",SetLastError=true)] public static extern bool DnsFlushResolverCache();
        [DllImport("iphlpapi.dll",SetLastError=true)] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sorted, int family, int tableClass, uint reserved);
        [StructLayout(LayoutKind.Sequential)] private struct TcpRow { public uint State,LocalAddress,LocalPort,RemoteAddress,RemotePort,Pid; }
        public static int ListenerProcess(int port) {
            int size = 0; GetExtendedTcpTable(IntPtr.Zero,ref size,false,2,5,0);
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try {
                if (GetExtendedTcpTable(buffer,ref size,false,2,5,0) != 0) throw new IOException("无法读取本机监听状态");
                int count = Marshal.ReadInt32(buffer);
                int rowSize = Marshal.SizeOf(typeof(TcpRow));
                uint loopback = BitConverter.ToUInt32(System.Net.IPAddress.Parse("127.0.0.2").GetAddressBytes(),0);
                for (int index = 0; index < count; index++) {
                    var row = (TcpRow)Marshal.PtrToStructure(IntPtr.Add(buffer,4+index*rowSize),typeof(TcpRow));
                    int actual = (int)(((row.LocalPort & 255) << 8) | ((row.LocalPort >> 8) & 255));
                    if (row.State == 2 && row.LocalAddress == loopback && actual == port) return (int)row.Pid;
                }
                return 0;
            } finally { Marshal.FreeHGlobal(buffer); }
        }
        public static bool OwnsListener(string executable) {
            int pid = ListenerProcess(443);
            if (pid == 0) return false;
            try { return String.Equals(Process.GetProcessById(pid).MainModule.FileName,executable,StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
        public static void VerifyRuntime(Layout layout) {
            DateTime until = DateTime.UtcNow.AddSeconds(12);
            while (!OwnsListener(layout.Executable)) {
                if (DateTime.UtcNow > until) throw new IOException("后台未能开始监听，可能端口被占用或程序被系统策略阻止。");
                Thread.Sleep(150);
            }
            Tunnel.ProbeRelay(443).GetAwaiter().GetResult();
        }
    }
    public sealed class ScheduledJob : IJob {
        private readonly Layout layout;
        private readonly dynamic service;
        private readonly dynamic folder;
        private readonly string arguments;
        private readonly string description;
        public ScheduledJob(Layout layout, string arguments = "--relay", string description = Installation.ProductId) {
            this.layout = layout; this.arguments = arguments; this.description=description;
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            service.Connect(); folder = service.GetFolder(@"\");
        }
        private dynamic Read() {
            try { return folder.GetTask(layout.TaskName); }
            catch (FileNotFoundException error) { if (error.HResult == unchecked((int)0x80070002)) return null; throw; }
            catch (COMException error) { if (error.HResult == unchecked((int)0x80070002)) return null; throw; }
        }
        private void Validate(dynamic task, string sid) {
            if (task == null) return;
            dynamic definition = task.Definition;
            if ((int)definition.Actions.Count != 1 || (int)definition.Actions[1].Type != 0 ||
                !String.Equals((string)definition.Actions[1].Path,layout.Executable,StringComparison.OrdinalIgnoreCase) ||
                (string)definition.Actions[1].Arguments != arguments ||
                (string)definition.RegistrationInfo.Description != description ||
                (int)definition.Principal.RunLevel != 0 || (int)definition.Principal.LogonType != 3)
                throw new InvalidOperationException("同名计划任务不属于本工具，已停止操作。");
            if (sid != null) {
                string registered = (string)definition.Principal.UserId;
                if (!registered.StartsWith("S-",StringComparison.OrdinalIgnoreCase)) registered = ((SecurityIdentifier)new NTAccount(registered).Translate(typeof(SecurityIdentifier))).Value;
                if (registered != sid) throw new InvalidOperationException("任务属于另一 Windows 用户，请由原用户管理。");
            }
        }
        public void ValidateOwner(string sid) { Validate(Read(),sid); }
        public string Snapshot() { dynamic task = Read(); Validate(task,null); return task == null ? null : (string)task.Xml; }
        public string State() { dynamic task = Read(); Validate(task,null); return task == null ? "未安装" : (int)task.State == 4 ? "后台运行中" : "已安装，后台未运行"; }
        public bool IsRunning() { dynamic task=Read(); Validate(task,null); return task != null && (int)task.State == 4; }
        public void Stop() {
            dynamic task = Read(); Validate(task,null); if (task == null) return;
            task.Enabled = false;
            // GetInstances may omit the running interactive instance. Stop the
            // registered task directly and verify its state as well as instances.
            try { task.Stop(0); }
            catch (COMException error) { if (error.HResult != unchecked((int)0x8004130B)) throw; }
            DateTime until = DateTime.UtcNow.AddSeconds(8);
            while ((int)task.State == 4 || (int)task.GetInstances(0).Count > 0) { if (DateTime.UtcNow > until) throw new IOException("后台进程尚未停止，请稍后重试。"); Thread.Sleep(100); }
        }
        public void Register(string sid) {
            Validate(Read(),sid);
            dynamic definition = service.NewTask(0);
            definition.RegistrationInfo.Description = description;
            definition.RegistrationInfo.Author = "Remote Relay Helper contributors";
            definition.Principal.UserId = sid; definition.Principal.LogonType = 3; definition.Principal.RunLevel = 0;
            definition.Settings.Enabled = true; definition.Settings.Hidden = true;
            definition.Settings.StartWhenAvailable = true; definition.Settings.MultipleInstances = 2;
            definition.Settings.DisallowStartIfOnBatteries = false; definition.Settings.StopIfGoingOnBatteries = false;
            definition.Settings.ExecutionTimeLimit = "PT0S"; definition.Settings.RestartInterval = "PT1M"; definition.Settings.RestartCount = 3;
            dynamic login = definition.Triggers.Create(9); login.UserId = sid; login.Enabled = true;
            dynamic recovery = definition.Triggers.Create(1); recovery.Enabled = true;
            recovery.StartBoundary = DateTime.Now.AddMinutes(1).ToString("yyyy-MM-ddTHH:mm:ss");
            recovery.Repetition.Interval = "PT1M"; recovery.Repetition.StopAtDurationEnd = false;
            dynamic action = definition.Actions.Create(0); action.Path = layout.Executable; action.Arguments = arguments; action.WorkingDirectory = layout.InstallDir;
            folder.RegisterTaskDefinition(layout.TaskName,definition,6,sid,null,3,null);
        }
        public void Start() { dynamic task = Read(); Validate(task,null); if (task == null) throw new IOException("计划任务不存在。"); task.Enabled = true; task.Run(null); }
        public void Delete() { dynamic task = Read(); Validate(task,null); if (task != null) { Stop(); folder.DeleteTask(layout.TaskName,0); } }
        public void Restore(string xml) { Validate(Read(),null); folder.RegisterTask(layout.TaskName,xml,6,null,null,3,null); }
    }
}
