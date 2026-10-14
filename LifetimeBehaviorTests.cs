using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ClearFolder.Tests {
    internal static class LifetimeBehaviorTests {
        internal static int Probe(string[] args) {
            string root=args[1];
            if(args[0]=="--lifetime-probe"&&LifetimeBootstrap.Prepare(args,root))return 0;
            File.WriteAllText(Path.Combine(root,"pid.txt"),Process.GetCurrentProcess().Id.ToString());
            for(int n=0;n<600&&!File.Exists(Path.Combine(root,"stop"));n++)Thread.Sleep(100);
            return 0;
        }
        internal static int RunNative(string root) {
            Directory.CreateDirectory(root);
            bool baseline=RunCase(Path.Combine(root,"baseline"),false);
            bool detached=RunCase(Path.Combine(root,"independent"),true);
            Console.WriteLine("Baseline terminated when host Job closed: "+baseline);
            Console.WriteLine("Independent child survives host Job close and stops on request: "+detached);
            return baseline&&detached?0:1;
        }
        static bool RunCase(string root,bool detach) {
            Directory.CreateDirectory(root);IntPtr job=CreateJobObject(IntPtr.Zero,null);PROCESS_INFORMATION child=new PROCESS_INFORMATION();
            Process probe=null;bool jobClosed=false;
            try {
                if(job==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();
                IntPtr buffer=Marshal.AllocHGlobal(144);
                try {for(int n=0;n<144;n++)Marshal.WriteByte(buffer,n,0);Marshal.WriteInt32(buffer,16,0x2800);if(!SetInformationJobObject(job,9,buffer,144))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());}
                finally{Marshal.FreeHGlobal(buffer);}
                string exe=Process.GetCurrentProcess().MainModule.FileName;
                string command=LifetimeBootstrap.Quote(exe)+" "+(detach?"--lifetime-probe":"--lifetime-baseline")+" "+LifetimeBootstrap.Quote(root);
                var startup=new STARTUPINFO();startup.cb=Marshal.SizeOf(typeof(STARTUPINFO));
                if(!CreateProcess(exe,new StringBuilder(command),IntPtr.Zero,IntPtr.Zero,false,0x01000004,IntPtr.Zero,Path.GetDirectoryName(exe),ref startup,out child))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                if(!AssignProcessToJobObject(job,child.process))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                if(ResumeThread(child.thread)==uint.MaxValue)throw new System.ComponentModel.Win32Exception();
                string pidFile=Path.Combine(root,"pid.txt");var elapsed=Stopwatch.StartNew();while(!File.Exists(pidFile)&&elapsed.ElapsedMilliseconds<15000)Thread.Sleep(50);
                if(!File.Exists(pidFile))throw new Exception("Probe did not start: "+root);
                // The file is only one small integer; allow the writer to close it.
                int pid=0;while(pid==0&&elapsed.ElapsedMilliseconds<16000){try{int.TryParse(File.ReadAllText(pidFile),out pid);}catch(IOException){}if(pid==0)Thread.Sleep(20);}
                probe=Process.GetProcessById(pid);
                bool inJob;if(!IsProcessInJob(probe.Handle,IntPtr.Zero,out inJob))throw new System.ComponentModel.Win32Exception();
                if(detach&&inJob)throw new Exception("Independent probe still belongs to a Job");
                if(!detach&&!inJob)throw new Exception("Baseline was not contained in the host Job");
                CloseHandle(job);jobClosed=true;
                if(!detach)return probe.WaitForExit(5000);
                Thread.Sleep(500);if(probe.HasExited)return false;
                File.WriteAllText(Path.Combine(root,"stop"),"stop");return probe.WaitForExit(5000)&&probe.ExitCode==0;
            }finally {
                if(!jobClosed&&job!=IntPtr.Zero)CloseHandle(job);
                File.WriteAllText(Path.Combine(root,"stop"),"stop");
                if(probe!=null)probe.Dispose();
                if(child.thread!=IntPtr.Zero)CloseHandle(child.thread);if(child.process!=IntPtr.Zero)CloseHandle(child.process);
            }
        }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct STARTUPINFO {internal int cb;internal string reserved,desktop,title;internal uint x,y,width,height,xChars,yChars,fill,flags;internal short show,reservedSize;internal IntPtr reservedBytes,input,output,error;}
        [StructLayout(LayoutKind.Sequential)]struct PROCESS_INFORMATION {internal IntPtr process,thread;internal uint processId,threadId;}
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr CreateJobObject(IntPtr security,string name);
        [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetInformationJobObject(IntPtr job,int kind,IntPtr data,uint size);
        [DllImport("kernel32.dll",SetLastError=true)]static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
        [DllImport("kernel32.dll",SetLastError=true)]static extern bool IsProcessInJob(IntPtr process,IntPtr job,out bool result);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool CreateProcess(string app,StringBuilder command,IntPtr ps,IntPtr ts,bool inherit,uint flags,IntPtr env,string directory,ref STARTUPINFO start,out PROCESS_INFORMATION child);
        [DllImport("kernel32.dll",SetLastError=true)]static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr h);
    }
}
