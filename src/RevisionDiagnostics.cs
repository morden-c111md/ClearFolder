using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ClearFolder {
    internal static class RevisionDiagnostics {
        [DllImport("dwmapi.dll")] static extern int DwmFlush();
        public static bool Run(string root) {
            string artifacts=Path.Combine(root,"artifacts");Directory.CreateDirectory(artifacts);
            string testRoot=Path.Combine(artifacts,"test-revision-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff",CultureInfo.InvariantCulture));
            var log=new List<string>();bool passed=true;Point oldCursor=Cursor.Position;IntPtr oldForeground=Native.GetForegroundWindow();
            FolderWindow window=null;Form cover=null;bool affinityChanged=false,desktopToggled=false;
            try {
                var store=new Store(testRoot);store.Load();var deck=store.State.Decks[0];
                Rectangle work=Screen.PrimaryScreen.WorkingArea;deck.X=work.Left+work.Width/3;deck.Y=work.Top+work.Height/3;
                deck.Name="Synthetic shader test";deck.Entries.Clear();store.State.Contrast=1;store.State.RippleStrength=0;
                window=new FolderWindow(store,deck){ForceVisible=true};
                window.HoverTrace=log.Add;
                Layout layout=window.CurrentLayout;
                using(var bitmap=Checker(layout.Bounds.Size))window.SetDiagnosticBackground(bitmap);
                log.Add("Renderer: "+window.RendererDescription);log.Add("Grid: "+window.GridDescription);
                using(var g=Graphics.FromHwnd(IntPtr.Zero))log.Add("Desktop Graphics DPI: "+g.DpiX.ToString(CultureInfo.InvariantCulture)+" x "+g.DpiY.ToString(CultureInfo.InvariantCulture));
                log.Add("Collapsed icon physical pixels: "+layout.Icon.Width+" x "+layout.Icon.Height+"; exact image-content alignment not independently verified");
                Point icon=window.PointToScreen(Center(layout.Icon));Point outside=new Point(work.Right-10,work.Bottom-10);
                Move(outside);Pump(150);Move(icon);
                var opening=new List<double>();var openingTimes=new List<double>();Sample(window,opening,650,openingTimes);
                Check(window.IsExpanded&&window.Expansion>.98,"actual hover opens panel",log,ref passed);
                int intermediate=opening.Count(v=>v>.03&&v<.97);double openingJump=MaxStep(opening);
                log.Add("Opening samples="+opening.Count+"; intermediate="+intermediate+"; largest observed expansion step="+Number(openingJump));
                Check(intermediate>=3&&openingJump<.65,"opening has continuous intermediate values",log,ref passed);
                Trace("Cold opening",opening,openingTimes,log);

                Move(outside);Pump(40);
                var closing=new List<double>();Sample(window,closing,40);double before=window.Expansion;
                Move(icon);Native.mouse_event(2,0,0,0,UIntPtr.Zero);Native.mouse_event(4,0,0,0,UIntPtr.Zero);Pump(25);double after=window.Expansion;
                Pump(450);
                log.Add("Close interruption before="+Number(before)+"; after="+Number(after));
                Check(before>.03&&before<.98&&Math.Abs(after-before)<.40&&window.Expansion>.98,"closing reverses from current position after reactivation",log,ref passed);

                for(int cycle=1;cycle<=3;cycle++) {
                    Move(outside);Pump(750);int callbackStart=window.FrameIntervals().Length;
                    Move(icon);var values=new List<double>();var times=new List<double>();Sample(window,values,650,times);
                    Trace("Warm opening "+cycle,values,times,log);
                    double[] active=window.FrameIntervals().Skip(callbackStart).Where(v=>v>0).OrderBy(v=>v).ToArray();
                    if(active.Length>0)log.Add("Warm opening "+cycle+" callback count="+active.Length+"; p95 ms="+Number(Percentile(active,.95))+"; max ms="+Number(active[active.Length-1]));
                    Check(window.Expansion>.98&&values.Count(v=>v>.03&&v<.97)>=3&&MaxStep(values)<.65,"warm opening "+cycle+" retains continuous intermediate values",log,ref passed);
                }

                affinityChanged=Native.SetWindowDisplayAffinity(window.Handle,0);
                Check(affinityChanged,"own synthetic window can be sampled",log,ref passed);
                Rectangle interior=new Rectangle(layout.Panel.X+(int)(26*layout.Scale),layout.Panel.Y+(int)(48*layout.Scale),layout.Panel.Width-(int)(52*layout.Scale),layout.Panel.Height-(int)(70*layout.Scale));
                if(interior.Width<20||interior.Height<20)throw new InvalidOperationException("Synthetic panel interior is too small for a valid shader sample.");
                Rectangle screenInterior=interior;screenInterior.Offset(layout.Bounds.Location);
                Pump(160);
                using(Bitmap baseline=CaptureOwned(window,screenInterior)) {
                    store.State.RippleStrength=2.2;
                    Point first=window.PointToScreen(new Point(interior.Left+interior.Width/3,interior.Top+interior.Height/2));
                    Move(first);Pump(110);Move(new Point(first.X+24,first.Y+6));Pump(100);
                    int changed,secondChanged;
                    using(Bitmap active=CaptureOwned(window,screenInterior)) {
                        changed=Changed(baseline,active);
                        active.Save(Path.Combine(artifacts,"synthetic-water-active.png"),ImageFormat.Png);
                        Pump(120);
                        using(Bitmap later=CaptureOwned(window,screenInterior))secondChanged=Changed(active,later);
                    }
                    baseline.Save(Path.Combine(artifacts,"synthetic-water-idle.png"),ImageFormat.Png);
                    log.Add("Synthetic shader changed pixels idle->motion: "+changed+" / "+(interior.Width*interior.Height));
                    log.Add("Synthetic shader changed pixels while ring ages: "+secondChanged);
                    Check(changed>Math.Max(10,interior.Width*interior.Height/100)&&secondChanged>10,"mouse creates visible evolving refraction on synthetic checkerboard",log,ref passed);
                }
                TestDarkWater(window,store,layout,interior,screenInterior,Color.FromArgb(22,27,34),"dark solid",log,ref passed);
                TestDarkWater(window,store,layout,interior,screenInterior,Color.FromArgb(2,3,5),"near-black solid",log,ref passed);
                Native.SetWindowDisplayAffinity(window.Handle,0x11);affinityChanged=false;

                Move(outside);Pump(60);Check(!window.IsExpanded&&window.Expansion<.999,"leave begins closing without delay",log,ref passed);Pump(350);
                Check(!window.IsExpanded&&window.Expansion<.02,"close animation ends with complete collapse",log,ref passed);
                window.ForceVisible=false;
                cover=new Form {Text="ClearFolder synthetic coverage test",StartPosition=FormStartPosition.Manual,Location=new Point(icon.X-45,icon.Y-45),Size=new Size(180,150),ShowInTaskbar=false,BackColor=Color.DarkSlateBlue};
                cover.Show();cover.Activate();Native.SetForegroundWindow(cover.Handle);Pump(250);
                Check(Native.IsWindowVisible(window.Handle),"entry window remains visible when another application activates",log,ref passed);
                var under=Native.WindowFromPoint(new Native.POINT(icon.X,icon.Y));
                Check(under==cover.Handle||Native.GetAncestor(under,2)==cover.Handle,"ordinary application covers entry instead of permanent topmost overlay",log,ref passed);
                cover.Close();cover.Dispose();cover=null;Pump(200);
                Check(Native.IsWindowVisible(window.Handle),"entry window survives foreground window closure",log,ref passed);
                if(!Native.IsDesktop(Native.GetForegroundWindow())){ToggleDesktop();desktopToggled=true;Pump(550);}
                else Pump(150);
                IntPtr desktopHit=Native.WindowFromPoint(new Native.POINT(icon.X,icon.Y));
                log.Add("Win+D diagnostic icon point: "+icon);
                DumpWindow("Own entry",window.Handle,log);
                DumpWindow("Foreground",Native.GetForegroundWindow(),log);
                IntPtr desktopRoot=Native.DesktopRoot();DumpWindow("Desktop root",desktopRoot,log);
                DumpWindow("Hit",desktopHit,log);DumpWindow("Hit ancestor",Native.GetAncestor(desktopHit,2),log);
                DumpNeighbors("Own entry",window.Handle,log);DumpNeighbors("Desktop root",desktopRoot,log);
                Check(desktopHit==window.Handle||Native.GetAncestor(desktopHit,2)==window.Handle,"entry is actually reachable above desktop wallpaper after Win+D",log,ref passed);
                if(desktopToggled){ToggleDesktop();desktopToggled=false;Pump(150);}
                double[] intervals=window.FrameIntervals().Where(v=>v>0&&!double.IsInfinity(v)&&!double.IsNaN(v)).OrderBy(v=>v).ToArray();
                log.Add("CompositionTarget.Rendering callback intervals only; these are not measured presented FPS.");
                if(intervals.Length>0)log.Add("Callback samples="+intervals.Length+"; median ms="+Number(Percentile(intervals,.5))+"; p95 ms="+Number(Percentile(intervals,.95))+"; max ms="+Number(intervals[intervals.Length-1]));
                Check(intervals.Length>=10,"composition callback interval sample collected",log,ref passed);
            }catch(Exception error){passed=false;log.Add("FAIL: "+error);}
            finally {
                if(window!=null){if(affinityChanged)Native.SetWindowDisplayAffinity(window.Handle,0x11);window.Dispose();}
                if(cover!=null){cover.Close();cover.Dispose();}
                if(desktopToggled)ToggleDesktop();
                Native.mouse_event(4,0,0,0,UIntPtr.Zero);Native.SetCursorPos(oldCursor.X,oldCursor.Y);
                if(oldForeground!=IntPtr.Zero)Native.SetForegroundWindow(oldForeground);
            }
            log.Add("Synthetic data retained: "+testRoot);log.Add("Overall: "+(passed?"PASS":"FAIL"));
            File.WriteAllLines(Path.Combine(artifacts,"revision-test.txt"),log.ToArray());return passed;
        }
        static Bitmap Checker(Size size) {
            var bitmap=new Bitmap(size.Width,size.Height,PixelFormat.Format32bppArgb);
            using(var g=Graphics.FromImage(bitmap))using(var a=new SolidBrush(Color.FromArgb(25,90,175)))using(var b=new SolidBrush(Color.FromArgb(225,195,75)))
                for(int x=0;x<size.Width;x+=9)for(int y=0;y<size.Height;y+=9)g.FillRectangle(((x/9+y/9)%2)==0?a:b,x,y,9,9);
            return bitmap;
        }
        static void TestDarkWater(FolderWindow window,Store store,Layout layout,Rectangle interior,Rectangle screenInterior,Color background,string name,List<string> log,ref bool passed) {
            // A uniform texture cannot reveal refraction alone. Screen pixels here must
            // show the local wave lighting, rather than merely displaced checker edges.
            store.State.RippleStrength=0;
            using(var solid=new Bitmap(layout.Bounds.Width,layout.Bounds.Height,PixelFormat.Format32bppArgb)) {
                using(var graphics=Graphics.FromImage(solid))graphics.Clear(background);
                window.SetDiagnosticBackground(solid);
            }
            Point center=window.PointToScreen(Center(interior));Move(center);Pump(180);
            using(Bitmap baseline=CaptureOwned(window,screenInterior)) {
                store.State.RippleStrength=1.8;Pump(100);
                using(Bitmap active=CaptureOwned(window,screenInterior)) {
                    PixelDifference appeared=Difference(baseline,active);Pump(120);
                    using(Bitmap later=CaptureOwned(window,screenInterior)) {
                        PixelDifference evolving=Difference(active,later);
                        log.Add(name+" wave pixels: changed="+appeared.Changed+" / "+(interior.Width*interior.Height)+"; brighter="+appeared.Brighter+"; darker="+appeared.Darker+"; maximum channel change="+appeared.Maximum+"; mean absolute channel change="+Number(appeared.Mean)+"; changed while aging="+evolving.Changed);
                        Check(appeared.Brighter>10&&appeared.Changed>Math.Max(10,interior.Width*interior.Height/100)&&evolving.Changed>10,name+" has visible evolving local wave lighting",log,ref passed);
                        // A subtle local signed signal must retain unchanged/dimmer regions;
                        // a panel-wide white tint cannot satisfy this and the restoration test.
                        Check(appeared.Maximum<=20&&appeared.Mean<6&&(appeared.Darker>10||appeared.Changed<interior.Width*interior.Height*.8),name+" retains dark background without a uniform white veil",log,ref passed);
                    }
                }
                store.State.RippleStrength=0;Pump(90);
                using(Bitmap disabled=CaptureOwned(window,screenInterior)) {
                    PixelDifference residual=Difference(baseline,disabled);Pump(140);
                    using(Bitmap laterDisabled=CaptureOwned(window,screenInterior)) {
                        PixelDifference drift=Difference(disabled,laterDisabled);
                        log.Add(name+" disabled pixels: residual="+residual.Changed+"; drift="+drift.Changed+" (channel tolerance 1)");
                        Check(residual.Changed==0&&drift.Changed==0,name+" returns to original pixels and remains still after disabling water",log,ref passed);
                    }
                }
            }
        }
        sealed class PixelDifference {internal int Changed,Brighter,Darker,Maximum;internal double Mean;}
        static PixelDifference Difference(Bitmap baseline,Bitmap current) {
            if(baseline.Size!=current.Size)throw new ArgumentException("Synthetic frames must have equal dimensions.");
            var difference=new PixelDifference();long total=0;
            for(int x=0;x<baseline.Width;x++)for(int y=0;y<baseline.Height;y++) {
                Color a=baseline.GetPixel(x,y),b=current.GetPixel(x,y);
                int red=b.R-a.R,green=b.G-a.G,blue=b.B-a.B;
                int maximum=Math.Max(Math.Abs(red),Math.Max(Math.Abs(green),Math.Abs(blue)));
                difference.Maximum=Math.Max(difference.Maximum,maximum);total+=Math.Abs(red)+Math.Abs(green)+Math.Abs(blue);
                if(maximum<=1)continue;
                difference.Changed++;
                if(red+green+blue>3)difference.Brighter++;
                if(red+green+blue< -3)difference.Darker++;
            }
            difference.Mean=total/(baseline.Width*(double)baseline.Height*3);return difference;
        }
        static Bitmap CaptureOwned(FolderWindow window,Rectangle screen) {
            Point[] corners={new Point(screen.Left+1,screen.Top+1),new Point(screen.Right-2,screen.Top+1),new Point(screen.Left+1,screen.Bottom-2),new Point(screen.Right-2,screen.Bottom-2)};
            foreach(Point corner in corners) {
                IntPtr under=Native.WindowFromPoint(new Native.POINT(corner.X,corner.Y));
                if(under!=window.Handle&&Native.GetAncestor(under,2)!=window.Handle)throw new InvalidOperationException("Synthetic crop is not fully owned by the test window; screenshot refused.");
            }
            DwmFlush();var bitmap=new Bitmap(screen.Width,screen.Height,PixelFormat.Format32bppArgb);
            try{using(var g=Graphics.FromImage(bitmap))g.CopyFromScreen(screen.Location,Point.Empty,screen.Size,CopyPixelOperation.SourceCopy);return bitmap;}
            catch{bitmap.Dispose();throw;}
        }
        static int Changed(Bitmap a,Bitmap b){int count=0;for(int x=0;x<a.Width;x++)for(int y=0;y<a.Height;y++)if(a.GetPixel(x,y).ToArgb()!=b.GetPixel(x,y).ToArgb())count++;return count;}
        static void Sample(FolderWindow window,List<double> values,int ms,List<double> times=null){var clock=Stopwatch.StartNew();while(clock.ElapsedMilliseconds<ms){Application.DoEvents();values.Add(window.Expansion);if(times!=null)times.Add(clock.Elapsed.TotalMilliseconds);Thread.Sleep(8);}Application.DoEvents();}
        static void Trace(string title,List<double> values,List<double> times,List<string> log) {
            var trace=new List<string>();double gap=0;
            for(int i=0;i<values.Count;i++){if(i>0)gap=Math.Max(gap,times[i]-times[i-1]);if(i==0||Math.Abs(values[i]-values[i-1])>.0001)trace.Add(Number(times[i])+"ms="+Number(values[i]));}
            log.Add(title+" max observed expansion step="+Number(MaxStep(values))+"; max sampling gap ms="+Number(gap));
            log.Add(title+" observed changed samples: "+string.Join(", ",trace.ToArray()));
        }
        static void Pump(int ms){var clock=Stopwatch.StartNew();while(clock.ElapsedMilliseconds<ms){Application.DoEvents();Thread.Sleep(4);}Application.DoEvents();DwmFlush();}
        static void Move(Point point){Native.SetCursorPos(point.X,point.Y);}
        static void ToggleDesktop(){Native.keybd_event(0x5b,0,0,UIntPtr.Zero);Native.keybd_event(0x44,0,0,UIntPtr.Zero);Native.keybd_event(0x44,0,2,UIntPtr.Zero);Native.keybd_event(0x5b,0,2,UIntPtr.Zero);}
        static string WindowId(IntPtr hwnd){return "0x"+hwnd.ToInt64().ToString("X",CultureInfo.InvariantCulture)+" class="+Native.ClassName(hwnd);}
        static void DumpWindow(string label,IntPtr hwnd,List<string> log) {
            NativeRect bounds;bool rectOkay=GetWindowRect(hwnd,out bounds);
            int cloaked;int cloakResult=DwmGetWindowAttribute(hwnd,14,out cloaked,4);
            log.Add(label+": "+WindowId(hwnd)+" visible="+Native.IsWindowVisible(hwnd)+" iconic="+IsIconic(hwnd)+" cloaked="+(cloakResult>=0?cloaked.ToString():"unavailable")+" bounds="+(rectOkay?bounds.Left+","+bounds.Top+","+bounds.Right+","+bounds.Bottom:"unavailable"));
        }
        static void DumpNeighbors(string label,IntPtr hwnd,List<string> log) {
            if(hwnd==IntPtr.Zero)return;
            for(uint command=2;command<=3;command++) {
                var neighbors=new List<string>();IntPtr current=hwnd;
                for(int n=0;n<15;n++){current=Native.GetWindow(current,command);if(current==IntPtr.Zero)break;neighbors.Add(WindowId(current));}
                log.Add(label+(command==2?" next z-order: ":" previous z-order: ")+string.Join(" | ",neighbors.ToArray()));
            }
        }
        [StructLayout(LayoutKind.Sequential)] struct NativeRect {public int Left,Top,Right,Bottom;}
        [DllImport("user32.dll")]static extern bool GetWindowRect(IntPtr hwnd,out NativeRect rect);
        [DllImport("user32.dll")]static extern bool IsIconic(IntPtr hwnd);
        [DllImport("dwmapi.dll")]static extern int DwmGetWindowAttribute(IntPtr hwnd,int attribute,out int value,int size);
        static Point Center(Rectangle r){return new Point(r.Left+r.Width/2,r.Top+r.Height/2);}
        static double MaxStep(IList<double> values){double max=0;for(int i=1;i<values.Count;i++)max=Math.Max(max,Math.Abs(values[i]-values[i-1]));return max;}
        static double Percentile(double[] sorted,double fraction){return sorted[Math.Min(sorted.Length-1,(int)Math.Ceiling(sorted.Length*fraction)-1)];}
        static string Number(double value){return value.ToString("0.###",CultureInfo.InvariantCulture);}
        static void Check(bool okay,string name,List<string> log,ref bool passed){log.Add((okay?"PASS: ":"FAIL: ")+name);passed&=okay;}
    }
}

