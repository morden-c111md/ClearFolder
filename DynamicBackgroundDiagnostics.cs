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
    internal static class DynamicBackgroundDiagnostics {
        [DllImport("dwmapi.dll")]static extern int DwmFlush();
        sealed class PaintedColor {internal Color Color;internal double At;}
        internal static bool RunDesktop(string root) {
            string testRoot=Path.Combine(root,"artifacts","test-desktop-background-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff",CultureInfo.InvariantCulture));
            var log=new List<string>();bool passed=true;FolderWindow window=null;Point pointer=Cursor.Position;IntPtr foreground=Native.GetForegroundWindow();
            try {
                var store=new Store(testRoot);store.Load();var deck=store.State.Decks[0];Rectangle work=Screen.PrimaryScreen.WorkingArea;
                deck.X=work.Left+work.Width/3;deck.Y=work.Top+work.Height/3;deck.Name="Capture compatibility diagnostic";
                deck.Entries.Add(new Entry {Name="Synthetic icon",Path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe")});
                store.State.Contrast=1;store.State.RippleStrength=0;
                window=new FolderWindow(store,deck){ForceVisible=true};Layout layout=window.CurrentLayout;
                Point center=new Point(layout.Icon.X+layout.Icon.Width/2,layout.Icon.Y+layout.Icon.Height/2);
                Point screen=window.PointToScreen(center);Native.SetCursorPos(screen.X,screen.Y);Pump(950);
                uint affinity;Check(Native.GetWindowDisplayAffinity(window.Handle,out affinity)&&affinity==0,"real desktop path keeps window capturable",log,ref passed);
                var initial=window.BackgroundStatistics();long updates=window.BackgroundUpdates;var colors=new HashSet<int>();var watch=Stopwatch.StartNew();
                Point probe=new Point(layout.Panel.Right-(int)(25*layout.Scale),layout.Panel.Bottom-(int)(16*layout.Scale));
                while(watch.ElapsedMilliseconds<2200){Pump(15);colors.Add(window.ReadBackgroundPixel(probe).ToArgb());}
                var final=window.BackgroundStatistics();long applied=window.BackgroundUpdates-updates;
                log.Add("Real desktop source uploads="+applied+"; ms="+Number(watch.Elapsed.TotalMilliseconds)+"; uploads/second="+Number(applied*1000.0/watch.Elapsed.TotalMilliseconds)+" (not presented FPS); distinct probe colors="+colors.Count);
                Describe("Real desktop source capture ms",final.CaptureMilliseconds.Skip(initial.CaptureMilliseconds.Length).ToArray(),log);
                Check(applied>=10,"real desktop background continues updating",log,ref passed);
                Check(final.SourceBufferAllocations==initial.SourceBufferAllocations&&final.CaptureBufferAllocations==initial.CaptureBufferAllocations&&final.DisplayBufferAllocations==initial.DisplayBufferAllocations,"full desktop source and ROI/display buffers remain reused",log,ref passed);
                window.PauseBackgroundCapture=true;Pump(180);
                Rectangle icon=layout.Icon;Point iconScreen=window.PointToScreen(icon.Location);
                using(var output=new Bitmap(icon.Width,icon.Height,PixelFormat.Format32bppRgb))using(var graphics=Graphics.FromImage(output)) {
                    DwmFlush();graphics.CopyFromScreen(iconScreen,Point.Empty,icon.Size,CopyPixelOperation.SourceCopy);
                    int different=0,examined=0;
                    for(int y=icon.Height/4;y<icon.Height*3/4;y+=3)for(int x=icon.Width/4;x<icon.Width*3/4;x+=3) {
                        examined++;if(!Near(output.GetPixel(x,y),window.ReadBackgroundPixel(new Point(icon.X+x,icon.Y+y)),12))different++;
                    }
                    log.Add("Screenshot foreground pixels differing from independent source: "+different+" / "+examined);
                    Check(different>=10,"normal screen screenshot includes the synthetic foreground icon excluded from the independent background",log,ref passed);
                }
            }catch(Exception error){passed=false;log.Add("FAIL: "+error);}
            finally {if(window!=null)window.Dispose();Native.SetCursorPos(pointer.X,pointer.Y);if(foreground!=IntPtr.Zero)Native.SetForegroundWindow(foreground);}
            log.Add("Synthetic data retained: "+testRoot);log.Add("Overall: "+(passed?"PASS":"FAIL"));
            File.WriteAllLines(Path.Combine(root,"artifacts","desktop-background-test.txt"),log.ToArray());return passed;
        }
        sealed class MovingBackground : Form {
            readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer {Interval=30};
            readonly Stopwatch clock;
            int phase;
            internal readonly List<PaintedColor> Paints=new List<PaintedColor>();
            internal MovingBackground(Rectangle bounds,Stopwatch watch) {
                clock=watch;FormBorderStyle=FormBorderStyle.None;StartPosition=FormStartPosition.Manual;Bounds=bounds;
                ShowInTaskbar=false;Text="ClearFolder owned dynamic background test";DoubleBuffered=true;
                timer.Tick+=delegate {phase=(phase+1)%16;Invalidate();};timer.Start();
            }
            protected override void OnPaint(PaintEventArgs args) {
                Color color=Color.FromArgb(25+phase*4,40+phase*5,65+phase*6);args.Graphics.Clear(color);
                args.Graphics.FillRectangle(Brushes.Red,9,11,5,5);
                Paints.Add(new PaintedColor {Color=color,At=clock.Elapsed.TotalMilliseconds});
                if(Paints.Count>600)Paints.RemoveAt(0);
            }
            internal void StopAnimation(){timer.Stop();}
            protected override void Dispose(bool disposing){if(disposing)timer.Dispose();base.Dispose(disposing);}
        }
        public static bool Run(string root) {
            string artifacts=Path.Combine(root,"artifacts");Directory.CreateDirectory(artifacts);
            string testRoot=Path.Combine(artifacts,"test-dynamic-background-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff",CultureInfo.InvariantCulture));
            var log=new List<string>();bool passed=true;
            FolderWindow window=null;MovingBackground background=null;Form cover=null;
            Point original=Cursor.Position;IntPtr foreground=Native.GetForegroundWindow();var clock=Stopwatch.StartNew();
            try {
                var store=new Store(testRoot);store.Load();Deck deck=store.State.Decks[0];Rectangle work=Screen.PrimaryScreen.WorkingArea;
                deck.Name="Synthetic dynamic background";deck.Entries.Clear();deck.X=work.Left+work.Width/3;deck.Y=work.Top+work.Height/3;
                store.State.Contrast=1;store.State.RippleStrength=0;
                window=new FolderWindow(store,deck){ForceVisible=true};
                uint affinity;
                Check(Native.GetWindowDisplayAffinity(window.Handle,out affinity)&&affinity==0,"product window permits screenshots without capture exclusion",log,ref passed);
                Layout layout=window.CurrentLayout;Rectangle behind=layout.Bounds;behind.Inflate(12,12);
                background=new MovingBackground(behind,clock);background.Show();background.Activate();Pump(180);
                window.BackgroundSourceOverride=background.Handle;
                using(var sample=new Bitmap(8,8))using(var graphics=Graphics.FromImage(sample))using(var capture=new WindowBackgroundCapture()) {
                    bool refused=false;
                    try{capture.Capture(graphics,layout.Bounds,window.Handle,window.Handle);}catch(InvalidOperationException){refused=true;}
                    Check(refused,"capture provider rejects the glass window as its own background source",log,ref passed);
                    var partial=new Rectangle(behind.Left-4,behind.Top-4,8,8);
                    capture.Capture(graphics,partial,window.Handle,background.Handle);
                    log.Add("Partial crop outside="+sample.GetPixel(0,0)+"; inside="+sample.GetPixel(6,6)+"; expected="+background.Paints.Last().Color);
                    Check(sample.GetPixel(0,0).ToArgb()==Color.Black.ToArgb()&&Near(sample.GetPixel(6,6),background.Paints.Last().Color,2),"partial source overlap clears outside pixels and preserves source coordinate mapping",log,ref passed);
                    capture.Capture(graphics,new Rectangle(behind.Left+9,behind.Top+11,8,8),window.Handle,background.Handle);
                    log.Add("Offset crop marker="+sample.GetPixel(0,0)+"; beside="+sample.GetPixel(6,6));
                    Check(Near(sample.GetPixel(0,0),Color.Red,2)&&Near(sample.GetPixel(6,6),background.Paints.Last().Color,2),"source cropping preserves asymmetric marker coordinates",log,ref passed);
                }
                Point icon=window.PointToScreen(new Point(layout.Icon.X+layout.Icon.Width/2,layout.Icon.Y+layout.Icon.Height/2));
                Native.SetCursorPos(icon.X,icon.Y);Pump(650);
                Point probe=new Point(layout.Panel.Right-(int)(25*layout.Scale),layout.Panel.Bottom-(int)(16*layout.Scale));
                Point probeScreen=window.PointToScreen(probe);Native.SetCursorPos(probeScreen.X,probeScreen.Y);Pump(350);
                Check(window.IsExpanded&&window.Expansion>.98,"own dynamic fixture opens through the product hover path",log,ref passed);
                Check(window.BackgroundUpdates>5,"product independent-window background chain is active",log,ref passed);
                log.Add("Renderer: "+window.RendererDescription);log.Add("Sampling target: 30ms; full physical capture bounds: "+layout.Bounds);
                log.Add("No SetDiagnosticBackground injection; no screen image is written to disk.");
                var initial=window.BackgroundStatistics();long startUpdates=window.BackgroundUpdates;
                var ages=new List<double>();var colors=new HashSet<int>();int observed=0,matched=0;long lastUpdate=startUpdates;
                var process=Process.GetCurrentProcess();TimeSpan cpuStart=process.TotalProcessorTime;double measureStart=clock.Elapsed.TotalMilliseconds;
                for(int phase=0;phase<3;phase++) {
                    double until=clock.Elapsed.TotalMilliseconds+1000;
                    while(clock.Elapsed.TotalMilliseconds<until) {
                        Pump(8);long updates=window.BackgroundUpdates;if(updates==lastUpdate)continue;
                        lastUpdate=updates;observed++;Color sample=window.ReadBackgroundPixel(probe);colors.Add(sample.ToArgb());
                        PaintedColor recent=background.Paints.LastOrDefault(p=>Near(p.Color,sample,2));
                        if(recent!=null&&clock.Elapsed.TotalMilliseconds-recent.At<180){matched++;ages.Add(clock.Elapsed.TotalMilliseconds-recent.At);}
                    }
                    process.Refresh();log.Add("Memory phase "+(phase+1)+": managed bytes="+GC.GetTotalMemory(false)+"; private bytes="+process.PrivateMemorySize64+"; working set bytes="+process.WorkingSet64);
                }
                double measureMs=clock.Elapsed.TotalMilliseconds-measureStart;
                TimeSpan cpu=process.TotalProcessorTime-cpuStart;var final=window.BackgroundStatistics();
                double[] intervals=final.UpdateIntervals.Skip(initial.UpdateIntervals.Length).ToArray();
                double[] copies=final.CaptureMilliseconds.Skip(initial.CaptureMilliseconds.Length).ToArray();
                double[] uploads=final.UploadMilliseconds.Skip(initial.UploadMilliseconds.Length).ToArray();
                long applied=window.BackgroundUpdates-startUpdates;
                log.Add("Measured window ms="+Number(measureMs)+"; applied uploads="+applied+"; upload rate / second="+Number(applied*1000.0/measureMs)+" (not presented FPS)");
                Describe("Applied upload intervals ms",intervals,log);Describe("PrintWindow worker elapsed ms",copies,log);Describe("UI WriteableBitmap upload elapsed ms",uploads,log);
                Describe("Observed painted-color age ms",ages.ToArray(),log);
                log.Add("Process CPU ms="+Number(cpu.TotalMilliseconds)+"; equivalent one-core utilization="+Number(cpu.TotalMilliseconds/measureMs*100)+"%; includes the synthetic painter and diagnostic polling, not isolated capture CPU.");
                log.Add("Observed new uploads="+observed+"; matching recent synthetic paints="+matched+"; distinct uploaded colors="+colors.Count);
                log.Add("Reusable buffers: source "+initial.SourceBufferAllocations+" -> "+final.SourceBufferAllocations+"; capture "+initial.CaptureBufferAllocations+" -> "+final.CaptureBufferAllocations+"; display "+initial.DisplayBufferAllocations+" -> "+final.DisplayBufferAllocations+"; in-flight="+final.InFlight);
                Check(applied>=20&&intervals.Length>=10&&Median(intervals)<120,"dynamic background updates substantially more often than the previous 450ms sampling",log,ref passed);
                Check(observed>=15&&matched>=observed*.9&&colors.Count>=6,"uploaded pixels track actual recent synthetic background paints",log,ref passed);
                Check(final.SourceBufferAllocations==initial.SourceBufferAllocations&&final.CaptureBufferAllocations==initial.CaptureBufferAllocations&&final.DisplayBufferAllocations==initial.DisplayBufferAllocations&&final.InFlight<=1,"same-size source/capture/display buffers are reused with at most one frame pending",log,ref passed);

                // Freeze the fixture, drain normal captures, then briefly allow a single
                // owned output pixel to be sampled while capture is paused (no feedback).
                background.StopAnimation();Pump(150);window.PauseBackgroundCapture=true;Pump(100);
                Color uploaded=window.ReadBackgroundPixel(probe);
                Pump(90);Color output=CaptureOwnedPixel(window,probeScreen);
                Check(Near(uploaded,output,4),"actual visible glass output matches its uploaded synthetic background pixel",log,ref passed);
                log.Add("Frozen uploaded pixel="+uploaded+"; visible glass pixel="+output);
                window.PauseBackgroundCapture=false;

                window.ForceVisible=false;
                cover=new Form {Text="ClearFolder owned capture occlusion test",FormBorderStyle=FormBorderStyle.None,StartPosition=FormStartPosition.Manual,Bounds=behind,ShowInTaskbar=false,TopMost=true,BackColor=Color.MidnightBlue};
                cover.Show();cover.Activate();Pump(180);long beforeCovered=window.BackgroundUpdates;Pump(250);
                Check(window.BackgroundUpdates==beforeCovered,"fully covered inactive entry pauses new background sampling",log,ref passed);
                Native.SetForegroundWindow(Native.DesktopRoot());Pump(180);
                IntPtr coveredHit=Native.WindowFromPoint(new Native.POINT(icon.X,icon.Y));
                bool desktopForeground=Native.IsDesktop(Native.GetForegroundWindow())&&(coveredHit==cover.Handle||Native.GetAncestor(coveredHit,2)==cover.Handle);
                long beforeDesktopCovered=window.BackgroundUpdates;Pump(250);
                Check(desktopForeground&&window.BackgroundUpdates==beforeDesktopCovered,"covered entry stays paused when desktop becomes foreground",log,ref passed);
                cover.Close();cover.Dispose();cover=null;window.ForceVisible=true;Pump(200);
                Check(window.BackgroundUpdates>beforeCovered,"sampling resumes when the entry is exposed",log,ref passed);
            }catch(Exception error){passed=false;log.Add("FAIL: "+error);}
            finally {
                if(window!=null){window.PauseBackgroundCapture=true;window.Dispose();}
                if(cover!=null){cover.Close();cover.Dispose();}if(background!=null){background.Close();background.Dispose();}
                Native.SetCursorPos(original.X,original.Y);if(foreground!=IntPtr.Zero)Native.SetForegroundWindow(foreground);
            }
            log.Add("Sampling and upload statistics are not measured presented FPS or GPU capture; memory samples are basic observations, with buffer reuse checked separately.");
            log.Add("Synthetic data retained: "+testRoot);log.Add("Overall: "+(passed?"PASS":"FAIL"));
            File.WriteAllLines(Path.Combine(artifacts,"dynamic-background-test.txt"),log.ToArray());return passed;
        }
        static Color CaptureOwnedPixel(FolderWindow window,Point point) {
            IntPtr under=Native.WindowFromPoint(new Native.POINT(point.X,point.Y));
            if(under!=window.Handle&&Native.GetAncestor(under,2)!=window.Handle)throw new InvalidOperationException("Output pixel is not owned by the synthetic test window; sampling refused.");
            DwmFlush();using(var pixel=new Bitmap(1,1,PixelFormat.Format32bppRgb)) {
                using(var graphics=Graphics.FromImage(pixel))graphics.CopyFromScreen(point,Point.Empty,new Size(1,1),CopyPixelOperation.SourceCopy);
                return pixel.GetPixel(0,0);
            }
        }
        static bool Near(Color a,Color b,int tolerance){return Math.Abs(a.R-b.R)<=tolerance&&Math.Abs(a.G-b.G)<=tolerance&&Math.Abs(a.B-b.B)<=tolerance;}
        static void Describe(string name,double[] values,List<string> log) {
            double[] ordered=values.OrderBy(v=>v).ToArray();
            log.Add(name+": count="+ordered.Length+(ordered.Length==0?"":"; median="+Number(Median(ordered))+"; p95="+Number(ordered[Math.Min(ordered.Length-1,(int)Math.Ceiling(ordered.Length*.95)-1)])+"; max="+Number(ordered[ordered.Length-1])));
        }
        static double Median(double[] values){if(values.Length==0)return double.PositiveInfinity;double[] sorted=values.OrderBy(v=>v).ToArray();return sorted[sorted.Length/2];}
        static string Number(double value){return value.ToString("0.###",CultureInfo.InvariantCulture);}
        static void Check(bool okay,string name,List<string> log,ref bool passed){log.Add((okay?"PASS: ":"FAIL: ")+name);passed&=okay;}
        static void Pump(int ms){var watch=Stopwatch.StartNew();while(watch.ElapsedMilliseconds<ms){Application.DoEvents();Thread.Sleep(4);}Application.DoEvents();}
    }
}
