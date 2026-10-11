using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

namespace ClearFolder {
    internal static class TransferDiagnostics {
        sealed class ExplorerWindow {internal IntPtr Handle;internal uint ProcessId;internal string Path;}
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint process);
        [StructLayout(LayoutKind.Sequential)] struct WindowRect {internal int Left,Top,Right,Bottom;public override string ToString(){return Left+","+Top+" "+(Right-Left)+"x"+(Bottom-Top);}}
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd,out WindowRect rectangle);

        public static bool Run(string root) {
            string artifacts=Path.Combine(root,"artifacts");Directory.CreateDirectory(artifacts);
            string directory=Path.Combine(artifacts,"test-transfer-ui-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            string sourceDirectory=Path.Combine(directory,"source"),targetDirectory=Path.Combine(directory,"target"),common=Path.Combine(directory,"public");
            Directory.CreateDirectory(sourceDirectory);Directory.CreateDirectory(targetDirectory);Directory.CreateDirectory(common);
            var report=new StringBuilder();report.AppendLine("Native custom drag-out test; only synthetic files under "+directory);
            report.AppendLine("Does not write to the real user desktop. UIA/Shell destination probes are read only.");
            Point originalCursor=Cursor.Position;ExplorerWindow ownedExplorer=null;FolderWindow window=null;
            bool cancelled=false,transferred=false;
            try {
                ProbeDesktop(report);
                var store=new Store(Path.Combine(directory,"store"),sourceDirectory,common);store.Load();Deck deck=store.State.Decks[0];
                deck.Name="合成拖出验证";Rectangle work=Screen.PrimaryScreen.WorkingArea;deck.X=work.Left+40;deck.Y=work.Top+100;
                string source=Path.Combine(sourceDirectory,"synthetic-transfer.txt");const string content="ClearFolder synthetic native transfer payload";File.WriteAllText(source,content);
                Task<Entry> collecting=store.CollectAsync(deck,source);WaitFor(delegate{return collecting.IsCompleted;},8000);
                if(!collecting.IsCompleted)throw new TimeoutException("合成文件收纳超时。");
                Entry entry=collecting.GetAwaiter().GetResult();string stored=entry.Path;
                if(File.Exists(source)||!File.Exists(stored)||!entry.Managed)throw new InvalidOperationException("合成文件未正确进入独立存储。");
                var before=new HashSet<IntPtr>();foreach(var explorer in ExplorerWindows())before.Add(explorer.Handle);
                Process.Start(new ProcessStartInfo("explorer.exe","/n,\""+targetDirectory+"\""){UseShellExecute=true});
                WaitFor(delegate {
                    foreach(var explorer in ExplorerWindows())if(!before.Contains(explorer.Handle)&&SamePath(explorer.Path,targetDirectory)){ownedExplorer=explorer;return true;}
                    return false;
                },10000);
                if(ownedExplorer==null)throw new InvalidOperationException("未找到新建且路径完全匹配的资源管理器窗口；不借用或关闭用户已有窗口。");
                int width=Math.Min(760,Math.Max(420,work.Width/2-20)),height=Math.Min(650,Math.Max(320,work.Height-100));
                Native.SetWindowPos(ownedExplorer.Handle,IntPtr.Zero,work.Right-width-12,work.Top+35,width,height,0x14);
                Native.SetForegroundWindow(ownedExplorer.Handle);Pump(450);
                window=new FolderWindow(store,deck){ForceVisible=true};Pump(550);
                var layout=window.CurrentLayout;Point icon=window.PointToScreen(Center(layout.Icon));
                report.AppendLine("Grid: "+window.GridDescription);report.AppendLine("Expected bounds: "+layout.Bounds+"; local icon: "+layout.Icon);
                bool pointerMoved=Native.SetCursorPos(icon.X,icon.Y);ReportState(report,"Before hover",window,icon);report.AppendLine("SetCursorPos success: "+pointerMoved);
                Pump(700);ReportState(report,"After hover",window,icon);
                if(!window.IsExpanded||window.Expansion<.95)throw new InvalidOperationException("合成收纳面板未能悬停展开。");
                Point cell=window.PointToScreen(Center(layout.Cells[0]));
                Move(cell);Pump(100);Press();Pump(100);Move(new Point(cell.X+18,cell.Y+12));Pump(150);Release();Pump(350);
                cancelled=deck.Entries.Contains(entry)&&File.Exists(stored)&&!File.Exists(source);
                report.AppendLine("Release inside source panel cancels: "+cancelled);
                if(!cancelled)throw new InvalidOperationException("面板内取消拖动改变了合成文件。");
                Point destination=FindItemsPoint(ownedExplorer.Handle,targetDirectory,window);
                report.AppendLine("Verified Explorer blank item-area destination: "+destination+" -> "+targetDirectory);
                Move(icon);Pump(650);Move(cell);Pump(100);Press();Pump(90);
                Move(new Point(cell.X+20,cell.Y+8));Pump(120);
                for(int step=1;step<=10;step++){Move(new Point(cell.X+(destination.X-cell.X)*step/10,cell.Y+(destination.Y-cell.Y)*step/10));Pump(25);}
                Pump(120);
                Release();
                string target=Path.Combine(targetDirectory,"synthetic-transfer.txt");
                WaitFor(delegate{return File.Exists(target)&&!deck.Entries.Contains(entry);},8500);
                transferred=File.Exists(target)&&File.ReadAllText(target)==content&&!File.Exists(stored)&&!File.Exists(source)&&!deck.Entries.Contains(entry);
                report.AppendLine("Actual mouse capture drag into Explorer target moves file and removes panel entry: "+transferred);
                report.AppendLine("Source storage gone: "+!File.Exists(stored));report.AppendLine("Target payload matches: "+(File.Exists(target)&&File.ReadAllText(target)==content));
            }catch(Exception error){report.AppendLine("FAILED: "+error);}
            finally {
                Release();
                if(window!=null)window.Dispose();Move(originalCursor);
                if(ownedExplorer!=null) {
                    bool closed=CloseOwnedExplorer(ownedExplorer,targetDirectory);
                    report.AppendLine("Closed only test-created Explorer window still showing exact target: "+closed);
                    if(!closed)report.AppendLine("Test Explorer left open because ownership/current-directory could not be reconfirmed.");
                }
                report.AppendLine("UIA limitations: custom shell replacements and ambiguous tab views can reject the destination safely.");
                report.AppendLine("Synthetic test data retained; no files or directories deleted.");
                report.AppendLine("Result: "+(cancelled&&transferred?"PASS":"FAIL"));
                File.WriteAllText(Path.Combine(directory,"report.txt"),report.ToString());
                File.WriteAllText(Path.Combine(artifacts,"transfer-test.txt"),report.ToString());
            }
            return cancelled&&transferred;
        }
        static Point FindItemsPoint(IntPtr handle,string directory,FolderWindow source) {
            var root=AutomationElement.FromHandle(handle);
            var condition=new OrCondition(new PropertyCondition(AutomationElement.ClassNameProperty,"UIItemsView"),new PropertyCondition(AutomationElement.AutomationIdProperty,"ItemsView"),new PropertyCondition(AutomationElement.ClassNameProperty,"SysListView32"));
            var views=root.FindAll(TreeScope.Descendants,condition);
            foreach(AutomationElement view in views) {
                var bounds=view.Current.BoundingRectangle;if(bounds.Width<120||bounds.Height<100)continue;
                foreach(double fx in new[]{.7,.5,.85,.3})foreach(double fy in new[]{.7,.5,.85,.3}) {
                    Point point=new Point((int)(bounds.Left+bounds.Width*fx),(int)(bounds.Top+bounds.Height*fy));
                    if(source.CurrentLayout.Contains(source.PointToClient(point),true))continue;
                    var resolved=DropDestinations.Resolve(point,source.Handle);
                    if(resolved.Allowed&&SamePath(resolved.Directory,directory))return point;
                }
            }
            throw new InvalidOperationException("无法通过 UIA 与 Shell 同时确认新窗口的空白文件区域；未进行拖出。");
        }
        static void ReportState(StringBuilder report,string stage,FolderWindow source,Point requested) {
            Point actual=Cursor.Position;IntPtr under=Native.WindowFromPoint(new Native.POINT(actual.X,actual.Y));IntPtr root=Native.GetAncestor(under,2);
            IntPtr foreground=Native.GetForegroundWindow();WindowRect rectangle;bool gotRect=GetWindowRect(source.Handle,out rectangle);
            report.AppendLine(stage+": requested="+requested+"; cursor="+actual+"; source="+source.Handle+"; nativeRect="+(gotRect?rectangle.ToString():"unavailable")+
                "; under="+under+"/"+Native.ClassName(under)+"; underRoot="+root+"/"+Native.ClassName(root)+"; foreground="+foreground+"/"+Native.ClassName(foreground)+
                "; visible="+source.Visible+"/"+Native.IsWindowVisible(source.Handle)+"; iconic="+Native.IsIconic(source.Handle)+"; expanded="+source.IsExpanded+"; expansion="+source.Expansion.ToString("F3")+
                "; escape="+Native.GetAsyncKeyState(27)+"; localContains="+source.CurrentLayout.Contains(source.PointToClient(actual),false));
        }
        static void ProbeDesktop(StringBuilder report) {
            Rectangle work=Screen.PrimaryScreen.WorkingArea;
            foreach(var point in new[]{new Point(work.Left+8,work.Bottom-8),new Point(work.Right-8,work.Bottom-8),new Point(work.Left+8,work.Top+8),new Point(work.Right-8,work.Top+8)}) {
                IntPtr under=Native.WindowFromPoint(new Native.POINT(point.X,point.Y));if(!Native.IsDesktop(under))continue;
                var result=DropDestinations.Resolve(point,IntPtr.Zero);
                report.AppendLine("Read-only desktop destination probe: allowed="+result.Allowed+"; "+result.Description);return;
            }
            report.AppendLine("Read-only desktop destination probe: SKIP (desktop covered; no windows minimized).");
        }
        static List<ExplorerWindow> ExplorerWindows() {
            var results=new List<ExplorerWindow>();object shell=null,windows=null;
            try {
                shell=Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));windows=Get(shell,"Windows",null);
                int count=Convert.ToInt32(Get(windows,"Count",null));
                for(int i=0;i<Math.Min(256,count);i++) {
                    object window=null,document=null,folder=null,self=null;
                    try {
                        window=Get(windows,"Item",new object[]{i});if(window==null)continue;
                        IntPtr handle=new IntPtr(Convert.ToInt64(Get(window,"HWND",null)));uint pid;GetWindowThreadProcessId(handle,out pid);
                        var record=new ExplorerWindow {Handle=handle,ProcessId=pid};results.Add(record);
                        document=Get(window,"Document",null);folder=Get(document,"Folder",null);self=Get(folder,"Self",null);record.Path=Convert.ToString(Get(self,"Path",null));
                    }catch(COMException){}catch(TargetInvocationException){}
                    finally{ReleaseCom(self);ReleaseCom(folder);ReleaseCom(document);ReleaseCom(window);}
                }
                return results;
            }finally{ReleaseCom(windows);ReleaseCom(shell);}
        }
        static bool CloseOwnedExplorer(ExplorerWindow owned,string target) {
            object shell=null,windows=null;
            try {
                int matchingViews=0;
                foreach(var existing in ExplorerWindows())if(existing.Handle==owned.Handle) {
                    matchingViews++;
                    if(existing.ProcessId!=owned.ProcessId||!SamePath(existing.Path,target))return false;
                }
                // If a user added a tab to this window, leave the entire window open.
                if(matchingViews!=1)return false;
                shell=Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));windows=Get(shell,"Windows",null);int count=Convert.ToInt32(Get(windows,"Count",null));
                for(int i=0;i<Math.Min(count,256);i++) {
                    object window=null,document=null,folder=null,self=null;
                    try {
                        window=Get(windows,"Item",new object[]{i});IntPtr handle=new IntPtr(Convert.ToInt64(Get(window,"HWND",null)));
                        uint pid;GetWindowThreadProcessId(handle,out pid);if(handle!=owned.Handle||pid!=owned.ProcessId)continue;
                        document=Get(window,"Document",null);folder=Get(document,"Folder",null);self=Get(folder,"Self",null);
                        if(!SamePath(Convert.ToString(Get(self,"Path",null)),target))return false;
                        Get(window,"Quit",null);return true;
                    }finally{ReleaseCom(self);ReleaseCom(folder);ReleaseCom(document);ReleaseCom(window);}
                }
            }catch {return false;}finally{ReleaseCom(windows);ReleaseCom(shell);}
            return false;
        }
        static object Get(object target,string name,object[] args){return target.GetType().InvokeMember(name,BindingFlags.InvokeMethod|BindingFlags.GetProperty|BindingFlags.IgnoreCase,null,target,args);}
        static void ReleaseCom(object value){if(value!=null&&Marshal.IsComObject(value))Marshal.FinalReleaseComObject(value);}
        static bool SamePath(string a,string b){try{return string.Equals(Path.GetFullPath(a).TrimEnd('\\'),Path.GetFullPath(b).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase);}catch{return false;}}
        static Point Center(Rectangle rectangle){return new Point(rectangle.X+rectangle.Width/2,rectangle.Y+rectangle.Height/2);}
        static void Move(Point point){Native.SetCursorPos(point.X,point.Y);}
        static void Press(){Native.mouse_event(2,0,0,0,UIntPtr.Zero);}
        static void Release(){Native.mouse_event(4,0,0,0,UIntPtr.Zero);}
        static void Pump(int milliseconds){var clock=Stopwatch.StartNew();while(clock.ElapsedMilliseconds<milliseconds){Application.DoEvents();Thread.Sleep(8);}}
        static void WaitFor(Func<bool> predicate,int milliseconds){var clock=Stopwatch.StartNew();while(!predicate()&&clock.ElapsedMilliseconds<milliseconds)Pump(20);}
    }
}
