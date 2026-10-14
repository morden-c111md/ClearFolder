using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ClearFolder {
    // This boundary runs before the single-instance mutex and any layout access.
    internal static class LifetimeBootstrap {
        internal const string Marker="--clearfolder-independent=";
        const uint KillOnClose=0x2000,CreateSuspended=0x4,CreateBreakaway=0x01000000;
        internal sealed class JobState {internal bool InJob;internal uint Flags;}
        internal static JobState Inspect() {
            bool inJob;
            if(!IsProcessInJob(Process.GetCurrentProcess().Handle,IntPtr.Zero,out inJob))throw new Win32Exception(Marshal.GetLastWin32Error(),"无法核验进程生命周期。");
            uint flags=0;
            if(inJob) {
                IntPtr buffer=Marshal.AllocHGlobal(144);
                try {uint size;if(!QueryInformationJobObject(IntPtr.Zero,9,buffer,144,out size))throw new Win32Exception(Marshal.GetLastWin32Error(),"无法读取宿主终止策略。");flags=(uint)Marshal.ReadInt32(buffer,16);}
                finally{Marshal.FreeHGlobal(buffer);}
            }
            return new JobState{InJob=inJob,Flags=flags};
        }
        internal static bool NeedsHandoff(JobState state){return state.InJob;}
        internal static string[] UserArguments(string[] args){return args.Where(a=>!a.StartsWith(Marker,StringComparison.Ordinal)).ToArray();}
        internal static bool Prepare(string[] args,string root) {
            var state=Inspect();LifecycleLog.Write(root,"startup job="+state.InJob+" flags=0x"+state.Flags.ToString("X"));
            var markers=args.Where(a=>a.StartsWith(Marker,StringComparison.Ordinal)).ToArray();
            if(markers.Length>0) {
                if(markers.Length!=1||state.InJob)throw new InvalidOperationException("独立实例仍受宿主控制，已停止启动以避免再次随宿主退出。");
                Guid token;if(!Guid.TryParseExact(markers[0].Substring(Marker.Length),"N",out token))throw new InvalidOperationException("独立启动交接标识无效。");
                using(var ready=EventWaitHandle.OpenExisting("Local\\ClearFolder.Ready."+token.ToString("N")))ready.Set();
                LifecycleLog.Write(root,"independent-instance-confirmed");return false;
            }
            if(!NeedsHandoff(state))return false;
            string id=Guid.NewGuid().ToString("N");
            using(var ready=new EventWaitHandle(false,EventResetMode.ManualReset,"Local\\ClearFolder.Ready."+id)) {
                string executable=Process.GetCurrentProcess().MainModule.FileName;
                string command=Quote(executable)+" "+string.Join(" ",args.Select(Quote).ToArray())+" "+Quote(Marker+id);
                var start=new STARTUPINFO();start.cb=Marshal.SizeOf(typeof(STARTUPINFO));PROCESS_INFORMATION child;
                bool launched=CreateProcess(executable,new StringBuilder(command),IntPtr.Zero,IntPtr.Zero,false,CreateSuspended|CreateBreakaway,IntPtr.Zero,Path.GetDirectoryName(executable),ref start,out child);
                bool resumed=false;
                try {
                    if(launched) {
                        bool inherited;
                        if(!IsProcessInJob(child.process,IntPtr.Zero,out inherited))throw new Win32Exception(Marshal.GetLastWin32Error());
                        if(!inherited) {
                            if(ResumeThread(child.thread)==uint.MaxValue)throw new Win32Exception(Marshal.GetLastWin32Error());
                            resumed=true;
                        }
                    }
                    if(!resumed) {
                        if(launched) {
                            if(!TerminateProcess(child.process,1))throw new Win32Exception(Marshal.GetLastWin32Error(),"无法清理尚未执行的启动子进程。");
                            CloseHandle(child.thread);CloseHandle(child.process);launched=false;
                        }
                        LaunchFromDesktop(executable,string.Join(" ",args.Select(Quote).ToArray())+" "+Quote(Marker+id));
                        LifecycleLog.Write(root,"desktop-broker-launch");
                    }
                    if(!ready.WaitOne(30000))throw new TimeoutException("独立实例未及时确认启动。请检查桌面或生命周期日志，避免重复启动。");
                    LifecycleLog.Write(root,"handoff-confirmed");return true;
                } finally {
                    // Only terminate our own suspended child: it has not executed
                    // application code or touched user data. Never kill after resume.
                    if(launched) {
                        if(!resumed&&!TerminateProcess(child.process,1))LifecycleLog.Write(root,"suspended-child-cleanup-failed");
                        CloseHandle(child.thread);CloseHandle(child.process);
                    }
                }
            }
        }
        static void LaunchFromDesktop(string executable,string arguments) {
            object windows=null,desktop=null,document=null,application=null;
            try {
                windows=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")));
                desktop=windows.GetType().InvokeMember("FindWindowSW",System.Reflection.BindingFlags.InvokeMethod,null,windows,new object[]{0,null,8,0,1});
                if(desktop==null)throw new InvalidOperationException("无法连接实际 Windows 桌面，请从桌面启动 ClearFolder。");
                document=desktop.GetType().InvokeMember("Document",System.Reflection.BindingFlags.GetProperty,null,desktop,null);
                application=document.GetType().InvokeMember("Application",System.Reflection.BindingFlags.GetProperty,null,document,null);
                application.GetType().InvokeMember("ShellExecute",System.Reflection.BindingFlags.InvokeMethod,null,application,new object[]{executable,arguments,Path.GetDirectoryName(executable),"open",1});
            }finally {
                foreach(object value in new[]{application,document,desktop,windows})if(value!=null&&Marshal.IsComObject(value))Marshal.ReleaseComObject(value);
            }
        }
        internal static string Quote(string value) {
            var result=new StringBuilder("\"");int slashes=0;
            foreach(char c in value) {
                if(c=='\\'){slashes++;continue;}
                if(c=='\"'){result.Append('\\',slashes*2+1);result.Append(c);slashes=0;continue;}
                result.Append('\\',slashes);slashes=0;result.Append(c);
            }
            result.Append('\\',slashes*2);return result.Append('"').ToString();
        }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct STARTUPINFO {
            internal int cb;internal string reserved,desktop,title;internal uint x,y,width,height,xChars,yChars,fill,flags;internal short show,reservedSize;internal IntPtr reservedBytes,input,output,error;
        }
        [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION {internal IntPtr process,thread;internal uint processId,threadId;}
        [DllImport("kernel32.dll",SetLastError=true)]static extern bool IsProcessInJob(IntPtr process,IntPtr job,out bool result);
        [DllImport("kernel32.dll",SetLastError=true)]static extern bool QueryInformationJobObject(IntPtr job,int info,IntPtr buffer,uint size,out uint returned);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool CreateProcess(string app,StringBuilder command,IntPtr processSecurity,IntPtr threadSecurity,bool inherit,uint flags,IntPtr environment,string directory,ref STARTUPINFO startup,out PROCESS_INFORMATION info);
        [DllImport("kernel32.dll",SetLastError=true)]static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll",SetLastError=true)]static extern bool TerminateProcess(IntPtr process,uint code);
        [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
    }
    internal static class LifecycleLog {
        internal static void Write(string root,string stage) {
            try {
                using(var gate=new Mutex(false,"Local\\ClearFolder.LifecycleLog")) {
                    bool held=false;
                    try {
                        try{held=gate.WaitOne(1000);}catch(AbandonedMutexException){held=true;}
                        if(!held){Debug.WriteLine("ClearFolder lifecycle log busy");return;}
                        string directory=Path.Combine(root,"artifacts");Directory.CreateDirectory(directory);
                        string path=Path.Combine(directory,"lifecycle.log");
                        if(File.Exists(path)&&new FileInfo(path).Length>131072)File.WriteAllText(path,"Log restarted at size limit\r\n");
                        using(var process=Process.GetCurrentProcess())File.AppendAllText(path,DateTimeOffset.Now.ToString("o")+" pid="+process.Id+" session="+process.SessionId+" "+stage+"\r\n");
                    }finally{if(held)gate.ReleaseMutex();}
                }
            }catch(Exception error){Debug.WriteLine("ClearFolder lifecycle log unavailable: "+error.GetType().Name);}
        }
    }
}
