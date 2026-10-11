using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

namespace ClearFolder {
    internal static class RippleMenuDiagnostics {
        sealed class MenuResult {internal bool Found,StateMatches,Clicked;internal string Name,Error;}
        sealed class MenuAttempt {
            internal readonly object InputGate=new object();
            internal int Cancelled;
            internal bool Active=true;
            internal IntPtr Handle;
        }
        delegate bool EnumProc(IntPtr hwnd,IntPtr unused);
        [StructLayout(LayoutKind.Sequential)] struct Rect {internal int Left,Top,Right,Bottom;public override string ToString(){return Left+","+Top+" "+(Right-Left)+"x"+(Bottom-Top);}}
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd,out Rect rectangle);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback,IntPtr unused);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd,uint message,IntPtr wParam,IntPtr lParam);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr hwnd,uint message,IntPtr wParam,StringBuilder lParam,uint flags,uint timeout,out UIntPtr result);
        public static bool Run(string root) {
            string artifacts=Path.Combine(root,"artifacts"),directory=Path.Combine(artifacts,"test-ripple-menu-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(directory);var report=new StringBuilder();Point original=Cursor.Position;FolderWindow window=null;bool passed=false;
            string latest=Path.Combine(artifacts,"ripple-menu-test.txt");File.WriteAllText(latest,"RUNNING: "+directory+Environment.NewLine);
            object gate=new object();Action<string> progress=delegate(string value){lock(gate){string line=DateTime.UtcNow.ToString("O")+" "+value+Environment.NewLine;File.AppendAllText(Path.Combine(directory,"progress.txt"),line);File.AppendAllText(latest,line);}};
            try {
                var store=new Store(Path.Combine(directory,"store"));store.Load();Deck deck=store.State.Decks[0];
                deck.Name="合成水波开关验证";deck.X=Screen.PrimaryScreen.WorkingArea.Right-460;deck.Y=150;store.State.RippleStrength=0;store.Save();
                window=new FolderWindow(store,deck){ForceVisible=true};window.HoverTrace=progress;var layout=window.CurrentLayout;
                using(var checker=new Bitmap(layout.Bounds.Width,layout.Bounds.Height))using(var graphics=Graphics.FromImage(checker)) {
                    graphics.Clear(Color.DarkSlateBlue);for(int y=0;y<checker.Height;y+=16)for(int x=0;x<checker.Width;x+=16)if(((x/16+y/16)&1)==0)graphics.FillRectangle(Brushes.LightSkyBlue,x,y,16,16);
                    window.SetDiagnosticBackground(checker);
                }
                report.AppendLine("Own synthetic store: "+directory);report.AppendLine("Renderer: "+window.RendererDescription);Pump(600);
                MenuResult enable=ClickRipple(window,false,progress);Log(report,"Enable from saved off",enable);
                Pump(300);bool enabled=enable.Found&&enable.StateMatches&&enable.Clicked&&store.State.RippleStrength>0&&ReadStrength(store.Root)>0;
                report.AppendLine("Enable updates live and persisted strength: "+enabled);
                if(!enabled)throw new InvalidOperationException("First enable action failed; stopping sequential toggle checks. "+enable.Error);
                Point icon=window.PointToScreen(new Point(layout.Icon.X+layout.Icon.Width/2,layout.Icon.Y+layout.Icon.Height/2));Move(icon);Pump(650);
                Point panel=window.PointToScreen(new Point(layout.Panel.Left+layout.Panel.Width/2,layout.Panel.Top+layout.Panel.Height/2));Move(panel);Pump(80);Move(new Point(panel.X+18,panel.Y+8));Pump(70);
                var surface=(GlassSurface)typeof(FolderWindow).GetField("surface",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(window);
                var waves=(RippleState)typeof(GlassSurface).GetField("ripples",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(surface);
                bool emitted=window.IsExpanded&&waves.Waves.Count>0;report.AppendLine("Actual pointer motion emits independently tracked waves after menu enable: "+emitted+" (count="+waves.Waves.Count+")");
                if(!emitted)throw new InvalidOperationException("Enabled pointer motion did not emit waves; stopping subsequent toggles.");
                MenuResult disable=ClickRipple(window,true,progress);Log(report,"Disable from saved on",disable);Pump(250);
                bool disabled=disable.Found&&disable.StateMatches&&disable.Clicked&&store.State.RippleStrength==0&&ReadStrength(store.Root)==0;
                report.AppendLine("Disable updates live and persisted strength: "+disabled);
                if(!disabled)throw new InvalidOperationException("Disable action failed; stopping subsequent toggles. "+disable.Error);
                MenuResult reenable=ClickRipple(window,false,progress);Log(report,"Enable again",reenable);Pump(250);
                bool enabledAgain=reenable.Found&&reenable.StateMatches&&reenable.Clicked&&Math.Abs(store.State.RippleStrength-1.8)<.001&&Math.Abs(ReadStrength(store.Root)-1.8)<.001;
                report.AppendLine("Re-enable restores strength 1.8 live and persisted: "+enabledAgain);passed=enabled&&emitted&&disabled&&enabledAgain;
                report.AppendLine("This test verifies actual menu actions and wave emission; GPU visual pixel evidence remains in --animation-test, not inferred from wave count.");
            }catch(Exception error){report.AppendLine("FAILED: "+error);}
            finally {
                // Each input gesture releases its own button before ClickRipple returns.
                // An unconditional late button-up here could target another application.
                if(window!=null)window.Dispose();Move(original);
                report.AppendLine("Result: "+(passed?"PASS":"FAIL"));report.AppendLine("Synthetic test data retained; no user layout/files changed.");
                lock(gate){string trace=Path.Combine(directory,"progress.txt");if(File.Exists(trace)){report.AppendLine("Native progress trace:");report.Append(File.ReadAllText(trace));}}
                File.WriteAllText(Path.Combine(directory,"report.txt"),report.ToString());File.WriteAllText(latest,report.ToString());
            }
            return passed;
        }
        static MenuResult ClickRipple(FolderWindow source,bool expectedOn,Action<string> progress) {
            var result=new MenuResult();var done=new ManualResetEventSlim(false);uint own=(uint)Process.GetCurrentProcess().Id;
            var attempt=new MenuAttempt();
            var layout=source.CurrentLayout;Point icon=source.PointToScreen(new Point(layout.Icon.X+layout.Icon.Width/2,layout.Icon.Y+layout.Icon.Height/2));
            bool moved=Native.SetCursorPos(icon.X,icon.Y);progress("Preflight SetCursorPos="+moved+"; expectedBounds="+layout.Bounds+"; iconLocal="+layout.Icon+"; grid="+source.GridDescription);
            var readyClock=Stopwatch.StartNew();bool ready=false;long nextLog=0;
            while(readyClock.ElapsedMilliseconds<2000) {
                Point actual=Cursor.Position;IntPtr under=Native.WindowFromPoint(new Native.POINT(actual.X,actual.Y));IntPtr root=Native.GetAncestor(under,2);
                ready=moved&&actual==icon&&(under==source.Handle||root==source.Handle)&&Native.IsWindowVisible(source.Handle)&&!Native.IsIconic(source.Handle);
                if(readyClock.ElapsedMilliseconds>=nextLog||ready) {
                    Rect bounds;bool gotRect=GetWindowRect(source.Handle,out bounds);IntPtr foreground=Native.GetForegroundWindow();
                    progress("Native source readiness "+readyClock.ElapsedMilliseconds+"ms: ready="+ready+"; requested="+icon+"; actual="+actual+"; own="+source.Handle+"; nativeRect="+(gotRect?bounds.ToString():"unavailable")+
                        "; under="+under+"/"+Native.ClassName(under)+"; root="+root+"/"+Native.ClassName(root)+"; foreground="+foreground+"/"+Native.ClassName(foreground)+"; expanded="+source.IsExpanded+"; expansion="+source.Expansion);
                    nextLog=readyClock.ElapsedMilliseconds+200;
                }
                if(ready)break;Pump(20);
            }
            if(!ready){result.Error="Actual source hit at icon center was not ready within 2 seconds; no mouse buttons sent";return result;}
            var guard=new System.Threading.Timer(delegate {
                Interlocked.Exchange(ref attempt.Cancelled,1);
                progress("12s watchdog cancels only own visible context menu");
                lock(attempt.InputGate)if(attempt.Active&&OwnMenu(attempt.Handle,own))PostMessage(attempt.Handle,0x10,IntPtr.Zero,IntPtr.Zero);
            },null,12000,Timeout.Infinite);
            var worker=new Thread(delegate() {
                try {
                    IntPtr handle=IntPtr.Zero;var deadline=Stopwatch.StartNew();while(handle==IntPtr.Zero&&deadline.ElapsedMilliseconds<5000){handle=NativeMenu(own);Thread.Sleep(30);}
                    if(handle==IntPtr.Zero){result.Error="Own native context-menu window not found";return;}
                    lock(attempt.InputGate) {
                        if(Interlocked.CompareExchange(ref attempt.Cancelled,0,0)!=0||!attempt.Active){result.Error="Menu attempt cancelled before UIA query";return;}
                        attempt.Handle=handle;
                    }
                    progress("Found native menu "+handle+"; querying menu subtree only");
                    AutomationElement menu=AutomationElement.FromHandle(handle);var items=menu.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.MenuItem));
                    AutomationElement selected=null;
                    foreach(AutomationElement item in items)if((item.Current.Name??"").StartsWith("鼠标水波",StringComparison.Ordinal)){selected=item;break;}
                    if(selected==null){result.Error="鼠标水波 menu item not found";return;}
                    result.Found=true;result.Name=selected.Current.Name;
                    object pattern;bool? checkedState=null;
                    if(selected.TryGetCurrentPattern(TogglePattern.Pattern,out pattern))checkedState=((TogglePattern)pattern).Current.ToggleState==ToggleState.On;
                    bool nameOn=result.Name.Contains("已开启"),nameOff=result.Name.Contains("已关闭");
                    result.StateMatches=expectedOn?(nameOn||checkedState==true):(nameOff||checkedState==false);
                    progress("Ripple menu: "+result.Name+"; checked="+(checkedState.HasValue?checkedState.ToString():"not exposed")+"; expectedOn="+expectedOn);
                    var bounds=selected.Current.BoundingRectangle;if(bounds.IsEmpty)throw new InvalidOperationException("Ripple menu item has no visible bounds");
                    Point target=new Point((int)(bounds.Left+bounds.Width/2),(int)(bounds.Top+bounds.Height/2));
                    // The watchdog and caller's timeout wait for this short input section.
                    // A blocked UIA query may finish later, but cannot move/click after return.
                    lock(attempt.InputGate) {
                        if(Interlocked.CompareExchange(ref attempt.Cancelled,0,0)!=0||!attempt.Active||!OwnMenu(handle,own)){result.Error="Menu attempt cancelled or original menu disappeared before pointer move";return;}
                        if(!Native.SetCursorPos(target.X,target.Y)){result.Error="Could not move pointer to original menu";return;}
                        Thread.Sleep(35);
                        if(Interlocked.CompareExchange(ref attempt.Cancelled,0,0)!=0||!attempt.Active||!OwnMenuHit(handle,own,target)){result.Error="Menu attempt cancelled or original menu no longer owns target point; no mouse-down sent";return;}
                        bool down=false;
                        try {Native.mouse_event(2,0,0,0,UIntPtr.Zero);down=true;}
                        finally {
                            // Do not yield between down/up. The cancellation path cannot close
                            // the original menu or restore the pointer while this gate is held.
                            if(down)Native.mouse_event(4,0,0,0,UIntPtr.Zero);
                        }
                        result.Clicked=true;
                    }
                }catch(Exception error){result.Error=error.ToString();}
                finally{progress("Ripple menu worker done: "+(result.Error??"normal"));done.Set();}
            });worker.IsBackground=true;worker.SetApartmentState(ApartmentState.MTA);worker.Start();
            progress("Actual right-click at "+icon);bool rightDown=false;
            try {Native.mouse_event(8,0,0,0,UIntPtr.Zero);rightDown=true;Pump(35);}
            finally{if(rightDown)Native.mouse_event(16,0,0,0,UIntPtr.Zero);}
            var clock=Stopwatch.StartNew();while(!done.IsSet&&clock.ElapsedMilliseconds<15000)Pump(20);
            bool timedOut=!done.IsSet;
            if(timedOut){Interlocked.Exchange(ref attempt.Cancelled,1);result.Error="Menu worker timeout";}
            guard.Dispose();
            bool wasCancelled=Interlocked.CompareExchange(ref attempt.Cancelled,0,0)!=0;
            Interlocked.Exchange(ref attempt.Cancelled,1);
            lock(attempt.InputGate) {
                // Synchronize completion with any already-started down/up pair, then retire
                // the attempt so a queued watchdog cannot close a later menu with a reused HWND.
                if((wasCancelled||result.Error!=null)&&OwnMenu(attempt.Handle,own))PostMessage(attempt.Handle,0x10,IntPtr.Zero,IntPtr.Zero);
                attempt.Active=false;
            }
            if(wasCancelled){result.Clicked=false;if(result.Error==null)result.Error="Menu attempt cancelled by watchdog";}
            Pump(100);return result;
        }
        static bool OwnMenu(IntPtr handle,uint process) {
            if(handle==IntPtr.Zero||!Native.IsWindowVisible(handle))return false;
            uint pid;GetWindowThreadProcessId(handle,out pid);if(pid!=process)return false;
            string cls=Native.ClassName(handle);return cls.StartsWith("WindowsForms10.Window",StringComparison.Ordinal)||cls=="#32768";
        }
        static bool OwnMenuHit(IntPtr handle,uint process,Point target) {
            if(!OwnMenu(handle,process)||Cursor.Position!=target)return false;
            IntPtr under=Native.WindowFromPoint(new Native.POINT(target.X,target.Y));return under==handle||Native.GetAncestor(under,2)==handle;
        }
        static IntPtr NativeMenu(uint process) {
            IntPtr found=IntPtr.Zero;EnumWindows(delegate(IntPtr hwnd,IntPtr unused){uint pid;GetWindowThreadProcessId(hwnd,out pid);if(pid!=process||!Native.IsWindowVisible(hwnd))return true;
                string cls=Native.ClassName(hwnd);if(!cls.StartsWith("WindowsForms10.Window",StringComparison.Ordinal)&&cls!="#32768")return true;
                var title=new StringBuilder(512);UIntPtr result;if(SendMessageTimeout(hwnd,0xD,new IntPtr(title.Capacity),title,2,250,out result)!=IntPtr.Zero&&title.Length==0){found=hwnd;return false;}return true;},IntPtr.Zero);return found;
        }
        static double ReadStrength(string root){var store=new Store(root);store.Load();return store.State.RippleStrength;}
        static void Log(StringBuilder report,string stage,MenuResult result){report.AppendLine(stage+": found="+result.Found+"; stateMatches="+result.StateMatches+"; clicked="+result.Clicked+"; name="+result.Name+"; error="+(result.Error??"none"));}
        static void Move(Point point){Native.SetCursorPos(point.X,point.Y);}
        static void Pump(int milliseconds){var clock=Stopwatch.StartNew();while(clock.ElapsedMilliseconds<milliseconds){Application.DoEvents();Thread.Sleep(8);}}
    }
}

