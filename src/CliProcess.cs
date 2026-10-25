using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.AccessControl;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace RemoteHelper {
    // A GUI launched elevated must not elevate the user-writable desktop CLI.
    // Keep the same account/environment, remove privileges and admin membership,
    // and launch at medium integrity. Failure never falls back to elevation.
    public sealed class CliProcess : IDisposable {
        private SafeFileHandle native,job;
        public StreamReader Output { get; private set; }
        public StreamReader Errors { get; private set; }
        [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
        [StructLayout(LayoutKind.Sequential)] private struct SidAttributes { public IntPtr Sid; public uint Attributes; }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct StartupInfo {
            public int Size; public string Reserved,Desktop,Title;
            public int X,Y,XSize,YSize,XChars,YChars,FillAttribute,Flags;
            public short ShowWindow,ReservedBytes; public IntPtr ReservedPointer,Input,Output,Error;
        }
        [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process,Thread; public int ProcessId,ThreadId; }
        [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }
        [StructLayout(LayoutKind.Sequential)] private struct JobBasicLimits {
            public long ProcessTime,JobTime; public uint Flags; public UIntPtr MinimumWorkingSet,MaximumWorkingSet;
            public uint ActiveProcesses; public UIntPtr Affinity; public uint Priority,Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)] private struct JobLimits {
            public JobBasicLimits Basic; public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes;
            public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;
        }
        [DllImport("advapi32.dll",SetLastError=true)] private static extern bool OpenProcessToken(IntPtr process,uint access,out SafeFileHandle token);
        [DllImport("advapi32.dll",SetLastError=true)] private static extern bool CreateRestrictedToken(SafeFileHandle token,uint flags,int disabled,ref SidAttributes sid,int deleted,IntPtr privileges,int restricted,IntPtr restrictedSids,out SafeFileHandle result);
        [DllImport("advapi32.dll",SetLastError=true)] private static extern bool SetTokenInformation(SafeFileHandle token,int kind,IntPtr info,int length);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool CreateProcessAsUser(SafeFileHandle token,string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,bool inherit,uint flags,IntPtr environment,string directory,ref StartupInfoEx startup,out ProcessInfo info);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool CreateProcess(string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,bool inherit,uint flags,IntPtr environment,string directory,ref StartupInfoEx startup,out ProcessInfo info);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,int flags,ref IntPtr size);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,IntPtr attribute,IntPtr value,IntPtr size,IntPtr previous,IntPtr returned);
        [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes,string name);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool SetInformationJobObject(SafeFileHandle job,int kind,ref JobLimits limits,int size);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job,IntPtr process);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool TerminateJobObject(SafeFileHandle job,int code);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool CreatePipe(out SafeFileHandle read,out SafeFileHandle write,ref SecurityAttributes attributes,int size);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool SetHandleInformation(SafeFileHandle handle,int mask,int flags);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern uint WaitForSingleObject(SafeFileHandle handle,uint milliseconds);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetExitCodeProcess(SafeFileHandle handle,out int code);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool TerminateProcess(IntPtr handle,int code);
        public int ExitCode { get { int code; Require(GetExitCodeProcess(native,out code)); return code; } }
        public bool WaitForExit(int milliseconds) {
            uint result=WaitForSingleObject(native,(uint)milliseconds);
            if (result == 0) return true; if (result == 0x102) return false;
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Kill() { Require(TerminateJobObject(job,1)); }
        private static void Require(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(),"无法以普通权限启动 Codex 检查，已停止执行。"); }
        private static byte[] SidBytes(SecurityIdentifier sid) { var bytes=new byte[sid.BinaryLength]; sid.GetBinaryForm(bytes,0); return bytes; }
        private static SafeFileHandle LimitedToken() {
            SafeFileHandle original; Require(OpenProcessToken(Process.GetCurrentProcess().Handle,0x8B,out original));
            using (original) {
                byte[] admin=SidBytes(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null));
                IntPtr adminPointer=Marshal.AllocHGlobal(admin.Length);
                SafeFileHandle limited=null;
                try {
                    Marshal.Copy(admin,0,adminPointer,admin.Length);
                    var disabled=new SidAttributes { Sid=adminPointer };
                    Require(CreateRestrictedToken(original,5,1,ref disabled,0,IntPtr.Zero,0,IntPtr.Zero,out limited)); // DISABLE_MAX_PRIVILEGE | LUA_TOKEN
                    // Elevated tokens can give only Administrators full access in
                    // their default DACL. The restricted child must still be able
                    // to access its own process/thread objects as the same user.
                    var acl=new RawAcl(2,2);
                    foreach(var sid in new[] {WindowsIdentity.GetCurrent().User,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)})
                        acl.InsertAce(acl.Count,new CommonAce(AceFlags.None,AceQualifier.AccessAllowed,0x10000000,sid,false,null));
                    byte[] aclBytes=new byte[acl.BinaryLength]; acl.GetBinaryForm(aclBytes,0);
                    IntPtr defaultAcl=Marshal.AllocHGlobal(IntPtr.Size+aclBytes.Length);
                    try {
                        IntPtr data=IntPtr.Add(defaultAcl,IntPtr.Size); Marshal.Copy(aclBytes,0,data,aclBytes.Length); Marshal.WriteIntPtr(defaultAcl,data);
                        Require(SetTokenInformation(limited,6,defaultAcl,IntPtr.Size+aclBytes.Length));
                    } finally { Marshal.FreeHGlobal(defaultAcl); }
                    byte[] medium=SidBytes(new SecurityIdentifier("S-1-16-8192"));
                    int prefix=Marshal.SizeOf(typeof(SidAttributes)); IntPtr label=Marshal.AllocHGlobal(prefix+medium.Length);
                    try {
                        IntPtr sid=IntPtr.Add(label,prefix); Marshal.Copy(medium,0,sid,medium.Length);
                        Marshal.StructureToPtr(new SidAttributes { Sid=sid,Attributes=0x20 },label,false);
                        Require(SetTokenInformation(limited,25,label,prefix+medium.Length));
                    } finally { Marshal.FreeHGlobal(label); }
                    return limited;
                } catch { if (limited != null) limited.Dispose(); throw; }
                finally { Marshal.FreeHGlobal(adminPointer); }
            }
        }
        public static CliProcess Start(ProcessStartInfo info) {
            bool admin=new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            SafeFileHandle outputRead=null,outputWrite=null,errorRead=null,errorWrite=null,inputRead=null,inputWrite=null;
            SafeFileHandle group=null;
            IntPtr environment=IntPtr.Zero,attributes=IntPtr.Zero,handles=IntPtr.Zero;
            bool attributesReady=false;
            ProcessInfo created=new ProcessInfo();
            try {
                var security=new SecurityAttributes { Length=Marshal.SizeOf(typeof(SecurityAttributes)),Inherit=1 };
                Require(CreatePipe(out outputRead,out outputWrite,ref security,0)); Require(SetHandleInformation(outputRead,1,0));
                Require(CreatePipe(out errorRead,out errorWrite,ref security,0)); Require(SetHandleInformation(errorRead,1,0));
                Require(CreatePipe(out inputRead,out inputWrite,ref security,0)); Require(SetHandleInformation(inputWrite,1,0));
                string[] keys=info.EnvironmentVariables.Keys.Cast<string>().OrderBy(k=>k,StringComparer.OrdinalIgnoreCase).ToArray();
                string block=String.Join("\0",keys.Select(k=>k+"="+info.EnvironmentVariables[k]))+"\0\0";
                environment=Marshal.StringToHGlobalUni(block);
                // Only standard IO handles cross the privilege boundary.
                IntPtr size=IntPtr.Zero; InitializeProcThreadAttributeList(IntPtr.Zero,1,0,ref size);
                attributes=Marshal.AllocHGlobal(size); Require(InitializeProcThreadAttributeList(attributes,1,0,ref size)); attributesReady=true;
                handles=Marshal.AllocHGlobal(3*IntPtr.Size);
                var inherited=new[] {inputRead.DangerousGetHandle(),outputWrite.DangerousGetHandle(),errorWrite.DangerousGetHandle()};
                for (int i=0;i<inherited.Length;i++) Marshal.WriteIntPtr(handles,i*IntPtr.Size,inherited[i]);
                Require(UpdateProcThreadAttribute(attributes,0,new IntPtr(0x20002),handles,new IntPtr(3*IntPtr.Size),IntPtr.Zero,IntPtr.Zero));
                var startup=new StartupInfoEx { Attributes=attributes,Info=new StartupInfo { Size=Marshal.SizeOf(typeof(StartupInfoEx)),Flags=0x100,Input=inherited[0],Output=inherited[1],Error=inherited[2] } };
                group=CreateJobObject(IntPtr.Zero,null); Require(!group.IsInvalid);
                var limits=new JobLimits { Basic=new JobBasicLimits { Flags=0x2000 } }; // KILL_ON_JOB_CLOSE
                Require(SetInformationJobObject(group,9,ref limits,Marshal.SizeOf(typeof(JobLimits))));
                var command=new StringBuilder("\""+info.FileName+"\" "+info.Arguments);
                string directory=String.IsNullOrEmpty(info.WorkingDirectory) ? Environment.CurrentDirectory : info.WorkingDirectory;
                const uint flags=0x08080404; // NO_WINDOW | UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO | SUSPENDED
                if (admin) { using (var token=LimitedToken()) Require(CreateProcessAsUser(token,info.FileName,command,IntPtr.Zero,IntPtr.Zero,true,flags,environment,directory,ref startup,out created)); }
                else Require(CreateProcess(info.FileName,command,IntPtr.Zero,IntPtr.Zero,true,flags,environment,directory,ref startup,out created));
                // Assign before running so even fast-spawned descendants belong
                // to this operation and cannot keep the output pipes open forever.
                Require(AssignProcessToJobObject(group,created.Process)); Require(ResumeThread(created.Thread) != UInt32.MaxValue);
                outputWrite.Dispose(); errorWrite.Dispose(); inputRead.Dispose(); inputWrite.Dispose();
                // Keep the creation handle: even an already-exited child remains
                // queryable, without a PID lookup or process-reuse race.
                var result=new CliProcess { native=new SafeFileHandle(created.Process,true),job=group }; created.Process=IntPtr.Zero; group=null;
                try {
                    result.Output=new StreamReader(new FileStream(outputRead,FileAccess.Read),Encoding.UTF8);
                    result.Errors=new StreamReader(new FileStream(errorRead,FileAccess.Read),Encoding.UTF8);
                    outputRead=null; errorRead=null; return result;
                } catch { try { result.Kill(); } catch { } result.Dispose(); throw; }
            } finally {
                if (created.Process != IntPtr.Zero) TerminateProcess(created.Process,1);
                if (group != null) group.Dispose();
                foreach (var handle in new[] {outputRead,outputWrite,errorRead,errorWrite,inputRead,inputWrite}) if (handle != null) handle.Dispose();
                if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
                if (attributesReady) DeleteProcThreadAttributeList(attributes);
                if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
                if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
                if (created.Thread != IntPtr.Zero) CloseHandle(created.Thread);
                if (created.Process != IntPtr.Zero) CloseHandle(created.Process);
            }
        }
        public void Dispose() {
            // Close the job first: descendants exit and release pipe writers
            // before readers are disposed, including on timeout or parser error.
            if (job != null) job.Dispose();
            if (Output != null) Output.Dispose(); if (Errors != null) Errors.Dispose(); if (native != null) native.Dispose();
        }
    }
}
