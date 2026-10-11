using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

namespace ClearFolder {
    internal static class UsabilityDiagnostics {
        sealed class RenameResult {
            internal bool MenuFound,MenuClicked,DialogFound,TextSet,ButtonClicked,Completed;
            internal string Error;
        }
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd,uint message,IntPtr wParam,IntPtr lParam);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
        delegate bool EnumProc(IntPtr hwnd,IntPtr unused);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback,IntPtr unused);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent,EnumProc callback,IntPtr unused);
        [StructLayout(LayoutKind.Sequential)] struct Rect {internal int Left,Top,Right,Bottom;}
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd,out Rect rectangle);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr hwnd,uint message,IntPtr wParam,IntPtr lParam,uint flags,uint timeout,out UIntPtr result);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr hwnd,uint message,IntPtr wParam,StringBuilder lParam,uint flags,uint timeout,out UIntPtr result);

        public static bool Run(string root) {
            string artifacts=Path.Combine(root,"artifacts"),directory=Path.Combine(artifacts,"test-usability-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(directory);var report=new StringBuilder();Point original=Cursor.Position;
            object progressLock=new object();Action<string> progress=delegate(string value){lock(progressLock){string line=DateTime.UtcNow.ToString("O")+" "+value+Environment.NewLine;File.AppendAllText(Path.Combine(directory,"progress.txt"),line);File.AppendAllText(Path.Combine(artifacts,"usability-test.txt"),line);}};
            File.WriteAllText(Path.Combine(artifacts,"usability-test.txt"),"RUNNING: "+directory+Environment.NewLine);progress("Starting native isolated rename diagnostic");
            bool saved=false,cancelled=false;FolderWindow window=null;
            try {
                var store=new Store(Path.Combine(directory,"store"));store.Load();Deck deck=store.State.Decks[0];
                deck.Name="重命名验证原名称";deck.X=Screen.PrimaryScreen.WorkingArea.Right-460;deck.Y=150;store.Save();
                window=new FolderWindow(store,deck){ForceVisible=true};
                var layout=window.CurrentLayout;
                using(var background=new Bitmap(layout.Bounds.Width,layout.Bounds.Height))using(var graphics=Graphics.FromImage(background)) {
                    graphics.Clear(Color.FromArgb(32,65,90));window.SetDiagnosticBackground(background);
                }
                report.AppendLine("Native rename menu test using isolated store: "+directory);
                report.AppendLine("Synthetic fixed background isolates context-menu/dialog behavior; normal desktop capture is not tested here.");
                Pump(550);
                string wanted="已验证的新文件夹名称";
                RenameResult accepted=RenameThroughMenu(window,wanted,false,progress);
                Log(report,"Confirm rename",accepted);
                var reloaded=new Store(store.Root);reloaded.Load();
                saved=accepted.Completed&&deck.Name==wanted&&reloaded.State.Decks[0].Name==wanted;
                report.AppendLine("Confirmed rename updates live and reloaded layout: "+saved);
                string prior=deck.Name;
                RenameResult rejected=RenameThroughMenu(window,"取消后不应出现的名称",true,progress);
                Log(report,"Cancel rename",rejected);
                var afterCancel=new Store(store.Root);afterCancel.Load();
                cancelled=rejected.Completed&&deck.Name==prior&&afterCancel.State.Decks[0].Name==prior;
                report.AppendLine("Cancel preserves live and reloaded name: "+cancelled);
            }catch(Exception error){report.AppendLine("FAILED: "+error);}
            finally {
                Native.mouse_event(4|16,0,0,0,UIntPtr.Zero);if(window!=null)window.Dispose();Native.SetCursorPos(original.X,original.Y);
                report.AppendLine("Result: "+(saved&&cancelled?"PASS":"FAIL"));report.AppendLine("Only isolated layout data was changed; test files retained.");
                File.WriteAllText(Path.Combine(directory,"report.txt"),report.ToString());File.WriteAllText(Path.Combine(artifacts,"usability-test.txt"),report.ToString());
            }
            return saved&&cancelled;
        }
        static RenameResult RenameThroughMenu(FolderWindow window,string text,bool cancel,Action<string> progress) {
            var result=new RenameResult();var done=new ManualResetEventSlim(false);uint ownProcess=(uint)Process.GetCurrentProcess().Id;
            var guard=new System.Threading.Timer(delegate {
                progress("Native watchdog cancelling only own rename/menu windows after 12s");
                EnumWindows(delegate(IntPtr handle,IntPtr unused){uint pid;GetWindowThreadProcessId(handle,out pid);if(pid!=ownProcess||!Native.IsWindowVisible(handle))return true;
                    string cls=Native.ClassName(handle);if(!cls.StartsWith("WindowsForms10.Window",StringComparison.Ordinal)&&cls!="#32768")return true;
                    string title=NativeText(handle);if(title=="文件夹名称"||string.IsNullOrEmpty(title))PostMessage(handle,0x10,IntPtr.Zero,IntPtr.Zero);return true;},IntPtr.Zero);
            },null,12000,Timeout.Infinite);
            var worker=new Thread(delegate() {
                IntPtr dialogHandle=IntPtr.Zero;
                try {
                    progress("Waiting for native own context-menu HWND (no WPF UIA traversal)");
                    IntPtr menuHandle=WaitHandle(delegate{return FindNativeWindow(ownProcess,false);},5000);
                    if(menuHandle==IntPtr.Zero){result.Error="Native context menu window not found";return;}
                    progress("Menu HWND="+menuHandle+"; querying only its WinForms UIA descendants");
                    AutomationElement menuRoot=AutomationElement.FromHandle(menuHandle);
                    AutomationElement menu=menuRoot.FindFirst(TreeScope.Descendants,new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.MenuItem),new PropertyCondition(AutomationElement.NameProperty,"重命名文件夹…")));
                    if(menu==null){result.Error="Rename menu item not found";return;}
                    result.MenuFound=true;progress("Physically clicking rename menu item");Click(menu);result.MenuClicked=true;
                    progress("Waiting for native rename dialog title");dialogHandle=WaitHandle(delegate{return FindNativeWindow(ownProcess,true);},5000);
                    if(dialogHandle==IntPtr.Zero){result.Error="Menu click did not open rename dialog";return;}
                    result.DialogFound=true;progress("Rename dialog HWND="+dialogHandle);
                    IntPtr input=FindChild(dialogHandle,true,null),button=FindChild(dialogHandle,false,cancel?"取消":"确定");
                    if(input==IntPtr.Zero||button==IntPtr.Zero)throw new InvalidOperationException("Native rename edit/action controls not found");
                    IntPtr buffer=Marshal.StringToHGlobalUni(text);
                    try {UIntPtr response;if(SendMessageTimeout(input,0xC,IntPtr.Zero,buffer,2,1500,out response)==IntPtr.Zero)throw new TimeoutException("Native edit WM_SETTEXT timed out");}
                    finally{Marshal.FreeHGlobal(buffer);}
                    result.TextSet=true;progress("Text set; physically clicking native "+(cancel?"Cancel":"OK"));ClickNative(button);result.ButtonClicked=true;
                    var clock=Stopwatch.StartNew();while(dialogHandle!=IntPtr.Zero&&IsWindow(dialogHandle)&&clock.ElapsedMilliseconds<3000)Thread.Sleep(20);
                    result.Completed=dialogHandle!=IntPtr.Zero&&!IsWindow(dialogHandle);
                    if(!result.Completed)result.Error="Clicked dialog action did not close its window";
                }catch(Exception error){result.Error=error.ToString();}
                finally {
                    if(dialogHandle!=IntPtr.Zero&&IsWindow(dialogHandle)) {
                        uint pid;GetWindowThreadProcessId(dialogHandle,out pid);
                        if(pid==ownProcess)PostMessage(dialogHandle,0x10,IntPtr.Zero,IntPtr.Zero);
                    }
                    progress("Worker finished: "+(result.Error??"normal"));done.Set();
                }
            });worker.IsBackground=true;worker.SetApartmentState(ApartmentState.MTA);worker.Start();
            var layout=window.CurrentLayout;Point icon=window.PointToScreen(new Point(layout.Icon.X+layout.Icon.Width/2,layout.Icon.Y+layout.Icon.Height/2));
            progress("Sending actual right-click at "+icon);Native.SetCursorPos(icon.X,icon.Y);Pump(60);Native.mouse_event(8,0,0,0,UIntPtr.Zero);Pump(40);Native.mouse_event(16,0,0,0,UIntPtr.Zero);
            var deadline=Stopwatch.StartNew();while(!done.IsSet&&deadline.ElapsedMilliseconds<16000)Pump(20);
            if(!done.IsSet)result.Error="Diagnostic worker timed out";
            guard.Dispose();Pump(150);return result;
        }
        static string NativeText(IntPtr handle) {
            var buffer=new StringBuilder(512);UIntPtr response;
            return SendMessageTimeout(handle,0xD,new IntPtr(buffer.Capacity),buffer,2,250,out response)==IntPtr.Zero?null:buffer.ToString();
        }
        static IntPtr FindNativeWindow(uint process,bool dialog) {
            IntPtr found=IntPtr.Zero;EnumWindows(delegate(IntPtr handle,IntPtr unused){uint pid;GetWindowThreadProcessId(handle,out pid);if(pid!=process||!Native.IsWindowVisible(handle))return true;
                string cls=Native.ClassName(handle);if(!cls.StartsWith("WindowsForms10.Window",StringComparison.Ordinal)&&cls!="#32768")return true;
                string title=NativeText(handle);bool match=dialog?title=="文件夹名称":title=="";
                if(match){found=handle;return false;}return true;},IntPtr.Zero);return found;
        }
        static IntPtr FindChild(IntPtr parent,bool edit,string title) {
            IntPtr found=IntPtr.Zero;EnumChildWindows(parent,delegate(IntPtr handle,IntPtr unused){string cls=Native.ClassName(handle);
                bool match=edit?(cls=="Edit"||cls.StartsWith("WindowsForms10.EDIT",StringComparison.Ordinal)):(cls=="Button"||cls.StartsWith("WindowsForms10.BUTTON",StringComparison.Ordinal));
                if(match&&(title==null||NativeText(handle)==title)){found=handle;return false;}return true;},IntPtr.Zero);return found;
        }
        static IntPtr WaitHandle(Func<IntPtr> find,int timeout) {
            var clock=Stopwatch.StartNew();while(clock.ElapsedMilliseconds<timeout){IntPtr handle=find();if(handle!=IntPtr.Zero)return handle;Thread.Sleep(40);}return IntPtr.Zero;
        }
        static void ClickNative(IntPtr handle) {
            Rect bounds;if(!GetWindowRect(handle,out bounds))throw new InvalidOperationException("Native target bounds unavailable");
            Native.SetCursorPos((bounds.Left+bounds.Right)/2,(bounds.Top+bounds.Bottom)/2);Thread.Sleep(30);Native.mouse_event(2,0,0,0,UIntPtr.Zero);Thread.Sleep(30);Native.mouse_event(4,0,0,0,UIntPtr.Zero);
        }
        static void Click(AutomationElement element) {
            var bounds=element.Current.BoundingRectangle;if(bounds.IsEmpty||bounds.Width<1||bounds.Height<1)throw new InvalidOperationException("UIA target has no visible bounds");
            int x=(int)(bounds.Left+bounds.Width/2),y=(int)(bounds.Top+bounds.Height/2);
            if(!Native.SetCursorPos(x,y))throw new InvalidOperationException("Cannot move pointer to own UIA target");
            Thread.Sleep(30);Native.mouse_event(2,0,0,0,UIntPtr.Zero);Thread.Sleep(30);Native.mouse_event(4,0,0,0,UIntPtr.Zero);
        }
        static void Log(StringBuilder report,string stage,RenameResult result) {
            report.AppendLine(stage+": menuFound="+result.MenuFound+"; menuClicked="+result.MenuClicked+"; dialogFound="+result.DialogFound+"; textSet="+result.TextSet+"; buttonClicked="+result.ButtonClicked+"; completed="+result.Completed+"; error="+(result.Error??"none"));
        }
        static void Pump(int milliseconds){var watch=Stopwatch.StartNew();while(watch.ElapsedMilliseconds<milliseconds){Application.DoEvents();Thread.Sleep(8);}}
    }
}
