using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ClearFolder {
    internal static class Diagnostics {
        [DllImport("dwmapi.dll")] static extern int DwmFlush();
        [DllImport("user32.dll")] static extern int GetGuiResources(IntPtr process,int flag);
        sealed class ProbeWindow : Form {
            protected override bool ShowWithoutActivation{get{return true;}}
            protected override CreateParams CreateParams{get{var p=base.CreateParams;p.ExStyle|=0x08000000|0x80;return p;}}
        }
        public static bool CaptureProbe(string root) {
            string artifacts=Path.Combine(root,"artifacts");Directory.CreateDirectory(artifacts);
            Point position=new Point(Screen.PrimaryScreen.WorkingArea.Right-150,Screen.PrimaryScreen.WorkingArea.Top+100);
            bool enabled;Color visible,excluded;
            using(var under=new ProbeWindow {FormBorderStyle=FormBorderStyle.None,ShowInTaskbar=false,TopMost=true,StartPosition=FormStartPosition.Manual,Location=position,Size=new Size(100,100),BackColor=Color.FromArgb(20,55,90)})
            using(var over=new ProbeWindow {FormBorderStyle=FormBorderStyle.None,ShowInTaskbar=false,TopMost=true,StartPosition=FormStartPosition.Manual,Location=position,Size=new Size(100,100),BackColor=Color.FromArgb(240,20,30)}) {
                under.Show();over.Show();Pump();visible=CapturePixel(position);
                enabled=Native.SetWindowDisplayAffinity(over.Handle,0x11);Pump();excluded=CapturePixel(position);
            }
            bool pass=enabled&&visible.R>200&&excluded.B>70&&excluded.R<60;
            File.WriteAllText(Path.Combine(artifacts,"capture-test.txt"),"WDA enabled: "+enabled+"\r\nNormal marker RGB: "+visible.R+","+visible.G+","+visible.B+"\r\nExcluded marker RGB: "+excluded.R+","+excluded.G+","+excluded.B+"\r\nCapture exclusion: "+(pass?"PASS":"FAIL")+"\r\n");
            return pass;
        }
        static Color CapturePixel(Point position) {
            using(var bitmap=new Bitmap(1,1))using(var g=Graphics.FromImage(bitmap)) {
                g.CopyFromScreen(position.X+50,position.Y+50,0,0,new Size(1,1));return bitmap.GetPixel(0,0);
            }
        }
        static void Pump(){Application.DoEvents();DwmFlush();Thread.Sleep(120);Application.DoEvents();DwmFlush();}
        static Deck Sample(int count) {
            var deck=new Deck{Name="科研工具",X=190,Y=110};
            string[] shortcuts=new string[0];
            for(int i=0;i<count;i++) {
                string path=i<shortcuts.Length?shortcuts[i]:Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),i%2==0?"notepad.exe":"explorer.exe");
                string name=i<shortcuts.Length?Path.GetFileNameWithoutExtension(path):(i%2==0?"记事本示例":"资源管理器示例");
                deck.Entries.Add(new Entry{Path=path,Name=name});
            }
            return deck;
        }
        public static void RenderPreview(string root) {
            string output=Path.Combine(root,"artifacts");Directory.CreateDirectory(output);
            int[] counts={4,6,12};
            using(var all=new Bitmap(1800,760))using(var g=Graphics.FromImage(all)) {
                g.Clear(Color.FromArgb(15,24,40));
                for(int k=0;k<3;k++) {
                    var deck=Sample(counts[k]);var work=new Rectangle(0,0,600,680);
                    deck.X=35;deck.Y=160;
                    var layout=Layout.Calculate(new Point(deck.X,deck.Y),deck.Entries.Count,0,work,1);
                    using(var background=Backdrop(layout.Bounds))using(var renderer=new GlassRenderer()) {
                        renderer.SetBackground(background,1.12);renderer.PreloadForDiagnostics(deck);
                        using(var frame=renderer.Render(layout,deck,new Settings(),1,new PointF(layout.Panel.X+80,layout.Panel.Y+60),1,.2,-1,false)) {
                            using(var whole=Backdrop(work))g.DrawImageUnscaled(whole,k*600,0);
                            g.DrawImageUnscaled(frame,layout.Bounds.X+k*600,layout.Bounds.Y);
                        }
                    }
                    using(var font=new Font("Microsoft YaHei UI",18))using(var brush=new SolidBrush(Color.White))
                        g.DrawString((k+2)+" × "+(k+2)+" 网格 · "+counts[k]+" 个项目",font,brush,k*600+28,25);
                    using(var pen=new Pen(Color.FromArgb(40,255,255,255)))g.DrawLine(pen,k*600,0,k*600,760);
                }
                using(var font=new Font("Microsoft YaHei UI",13))using(var brush=new SolidBrush(Color.White))g.DrawString("程序实际渲染 · 清透背景 / 无雾白底色 / 无发光描边 / 原位悬停展开",font,brush,28,705);
                all.Save(Path.Combine(output,"grid-preview.png"),ImageFormat.Png);
            }
            string attached=Environment.GetEnvironmentVariable("CLEARFOLDER_PREVIEW_IMAGE");
            if(!string.IsNullOrWhiteSpace(attached)&&File.Exists(attached)) {
                using(var original=new Bitmap(attached))using(var desktop=new Bitmap(1536,960))using(var g=Graphics.FromImage(desktop)) {
                    g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.DrawImage(original,new Rectangle(0,0,desktop.Width,desktop.Height));
                    var deck=Sample(6);deck.X=270;deck.Y=100;
                    var layout=Layout.Calculate(new Point(deck.X,deck.Y),deck.Entries.Count,0,new Rectangle(0,0,1536,910),1);
                    using(var capture=new Bitmap(layout.Bounds.Width,layout.Bounds.Height))using(var cg=Graphics.FromImage(capture))using(var renderer=new GlassRenderer()) {
                        cg.DrawImage(desktop,new Rectangle(0,0,capture.Width,capture.Height),layout.Bounds,GraphicsUnit.Pixel);
                        renderer.SetBackground(capture,1.12);renderer.PreloadForDiagnostics(deck);
                        using(var frame=renderer.Render(layout,deck,new Settings(),1,new PointF(layout.Panel.X+100,layout.Panel.Y+90),2,.25,-1,false))g.DrawImageUnscaled(frame,layout.Bounds.Location);
                    }
                    desktop.Save(Path.Combine(output,"desktop-preview.png"),ImageFormat.Png);
                }
            }
        }
        static Bitmap Backdrop(Rectangle bounds) {
            var image=new Bitmap(bounds.Width,bounds.Height);
            using(var g=Graphics.FromImage(image)) {
                g.TranslateTransform(-bounds.X,-bounds.Y);
                using(var brush=new LinearGradientBrush(new Rectangle(0,0,600,680),Color.FromArgb(25,56,85),Color.FromArgb(112,151,172),35))g.FillRectangle(brush,bounds);
                for(int y=0;y<700;y+=12)using(var pen=new Pen(Color.FromArgb(26,235,246,255),1))g.DrawBezier(pen,-50,y,140,y-85,350,y+100,650,y-25);
                using(var font=new Font("Segoe UI",10))for(int i=0;i<20;i++) {
                    int x=20+(i%5)*110,y=80+(i/5)*125;
                    using(var brush=new SolidBrush(Color.FromArgb(190,40+(i*37)%190,65+(i*23)%150,110+(i*13)%130)))g.FillRectangle(brush,x,y,27,27);
                    using(var brush=new SolidBrush(Color.White))g.DrawString("App "+(i+1),font,brush,x-3,y+30);
                }
            }
            return image;
        }
        public static void Smoke(string root) {
            string directory=Path.Combine(root,"artifacts","test-smoke-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            var store=new Store(directory);store.Load();
            Deck deck=store.State.Decks[0];deck.Name="试运行";deck.X=Screen.PrimaryScreen.WorkingArea.Right-430;deck.Y=120;
            deck.Entries=Sample(6).Entries;
            using(var window=new FolderWindow(store,deck){ForceVisible=true}) {
                int before=GetGuiResources(Process.GetCurrentProcess().Handle,0);
                var watch=Stopwatch.StartNew();TimeSpan cpu=Process.GetCurrentProcess().TotalProcessorTime;
                while(watch.ElapsedMilliseconds<4000){Application.DoEvents();Thread.Sleep(10);}
                int after=GetGuiResources(Process.GetCurrentProcess().Handle,0);
                double cpuSeconds=(Process.GetCurrentProcess().TotalProcessorTime-cpu).TotalSeconds;
                File.WriteAllText(Path.Combine(root,"artifacts","smoke-test.txt"),"Native window ran: "+watch.ElapsedMilliseconds+" ms\r\nGDI objects before/after: "+before+" / "+after+"\r\nCPU seconds: "+cpuSeconds.ToString("F3")+"\r\nStatus: PASS (process/runtime only; manual hover/drop still required)\r\n");
            }
        }
        static void RunFor(int milliseconds) {
            var watch=Stopwatch.StartNew();while(watch.ElapsedMilliseconds<milliseconds){Application.DoEvents();Thread.Sleep(8);}
        }
        public static bool InteractionTest(string root) {
            string directory=Path.Combine(root,"artifacts","test-ui-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            var store=new Store(Path.Combine(directory,"store"));store.Load();Deck deck=store.State.Decks[0];
            deck.Name="交互测试";deck.X=Screen.PrimaryScreen.WorkingArea.Right-460;deck.Y=150;deck.Entries=Sample(6).Entries;
            Point original=Cursor.Position;bool opened=false,bridge=false,grace=false,closed=false,earlyBridge=false;
            var trace=new System.Collections.Generic.List<string>();
            bool wideCorridor=true,clickThrough=true,coveredCloses=true;
            try {
                using(var window=new FolderWindow(store,deck){ForceVisible=true}) {
                    window.HoverTrace=delegate(string message){trace.Add(message);};
                    RunFor(300);var layout=window.CurrentLayout;
                    Point icon=window.PointToScreen(new Point(layout.Icon.X+layout.Icon.Width/2,layout.Icon.Y+layout.Icon.Height/2));
                    Native.SetCursorPos(icon.X,icon.Y);trace.Add("Initial requested="+icon+" actual="+Cursor.Position);RunFor(650);opened=window.IsExpanded&&window.Expansion>.95f;
                    trace.Add("Initial observation pointer="+Cursor.Position+" expanded="+window.IsExpanded+" expansion="+window.Expansion);
                    Point transition=window.PointToScreen(new Point(layout.Left?layout.Icon.Left-5:layout.Icon.Right+5,layout.Icon.Y+layout.Icon.Height/2));
                    Native.SetCursorPos(transition.X,transition.Y);RunFor(500);bridge=window.IsExpanded;
                    Point panel=window.PointToScreen(new Point(layout.Panel.X+layout.Panel.Width/2,layout.Panel.Y+layout.Panel.Height/2));
                    Native.SetCursorPos(panel.X,panel.Y);RunFor(300);
                    window.ExportFrame(Path.Combine(root,"artifacts","native-frame.png"));
                    Native.SetCursorPos(Screen.PrimaryScreen.WorkingArea.Left+15,Screen.PrimaryScreen.WorkingArea.Bottom-15);
                    RunFor(60);grace=!window.IsExpanded&&window.Expansion<.999f;RunFor(350);closed=!window.IsExpanded&&window.Expansion<.01f;
                    Native.SetCursorPos(icon.X,icon.Y);RunFor(260);Native.SetCursorPos(transition.X,transition.Y);RunFor(450);earlyBridge=window.IsExpanded&&window.Expansion>.95f;
                }
                Rectangle work=Screen.PrimaryScreen.WorkingArea;
                foreach(int anchor in new[]{work.Left+work.Width/3,work.Right-150}) {
                    deck.X=anchor;deck.Y=work.Top+work.Height/3;deck.Entries=Sample(16).Entries;
                    using(var window=new FolderWindow(store,deck){ForceVisible=true}) {
                        window.HoverTrace=trace.Add;RunFor(300);var layout=window.CurrentLayout;
                        Point center=new Point(layout.Icon.X+layout.Icon.Width/2,layout.Icon.Y+layout.Icon.Height/2);
                        Native.SetCursorPos(window.PointToScreen(center).X,window.PointToScreen(center).Y);RunFor(650);
                        int from=layout.Left?layout.Icon.Left:layout.Icon.Right,to=layout.Left?layout.Panel.Right:layout.Panel.Left;
                        Point gap=new Point((from+to)/2,center.Y+(int)(18*layout.Scale)),screenGap=window.PointToScreen(gap);
                        Native.SetCursorPos(screenGap.X,screenGap.Y);RunFor(250);
                        bool held=window.IsExpanded&&window.Expansion>.95&&layout.ContainsHover(gap,true)&&!layout.Contains(gap,true);
                        IntPtr hit=Native.WindowFromPoint(new Native.POINT(screenGap.X,screenGap.Y));
                        clickThrough&=hit!=window.Handle&&Native.GetAncestor(hit,2)!=window.Handle;
                        wideCorridor&=held;trace.Add("Wide corridor left="+layout.Left+" held="+held+" clickThrough="+clickThrough);
                        Native.SetCursorPos(window.PointToScreen(center).X,window.PointToScreen(center).Y);RunFor(80);
                        Rectangle last=layout.Cells[layout.Left?15:12];Point target=new Point(last.X+last.Width/2,last.Y+last.Height/2);
                        bool diagonal=true;
                        for(int step=1;step<=20;step++) {
                            Point position=window.PointToScreen(new Point(center.X+(target.X-center.X)*step/20,center.Y+(target.Y-center.Y)*step/20));
                            Native.SetCursorPos(position.X,position.Y);RunFor(30);diagonal&=window.IsExpanded;
                        }
                        wideCorridor&=diagonal;trace.Add("Four-row near-side bottom diagonal route left="+layout.Left+" held="+diagonal);
                        using(var cover=new ProbeWindow {FormBorderStyle=FormBorderStyle.None,ShowInTaskbar=false,TopMost=true,StartPosition=FormStartPosition.Manual,Location=new Point(screenGap.X-30,screenGap.Y-30),Size=new Size(60,60)}) {
                            cover.Show();Native.SetCursorPos(screenGap.X,screenGap.Y);RunFor(80);
                            bool blocked=!window.IsExpanded;coveredCloses&=blocked;trace.Add("Covered corridor closes="+blocked);
                        }
                    }
                }
            }finally{Native.SetCursorPos(original.X,original.Y);}
            bool pass=opened&&bridge&&grace&&closed&&earlyBridge&&wideCorridor&&clickThrough&&coveredCloses;
            File.WriteAllText(Path.Combine(root,"artifacts","interaction-test.txt"),"Hover opens: "+opened+"\r\nConnector keeps open: "+bridge+"\r\nConnector during opening keeps open: "+earlyBridge+"\r\nLeave begins closing without delay: "+grace+"\r\nClose animation completes: "+closed+"\r\nStatus: "+(pass?"PASS":"FAIL")+"\r\n"+String.Join("\r\n",trace));
            File.AppendAllText(Path.Combine(root,"artifacts","interaction-test.txt"),"\r\nWide horizontal/diagonal corridor both directions: "+wideCorridor+"\r\nInvisible corridor passes clicks through: "+clickThrough+"\r\nCovering application closes corridor: "+coveredCloses+"\r\n");
            return pass;
        }
        public static bool DropTest(string root) {
            string directory=Path.Combine(root,"artifacts","test-drop-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            var store=new Store(Path.Combine(directory,"store"));store.Load();Deck deck=store.State.Decks[0];
            Rectangle work=Screen.PrimaryScreen.WorkingArea;
            deck.X=work.Right-500;deck.Y=160;
            string file=Path.Combine(directory,"synthetic-drag.txt");File.WriteAllText(file,"drop-test-content");
            Point original=Cursor.Position;bool added=false,preserved=false;
            try {
                using(var window=new FolderWindow(store,deck){ForceVisible=true})
                using(var source=new ProbeWindow {Text="ClearFolder synthetic OLE drag test",StartPosition=FormStartPosition.Manual,Location=new Point(work.Right-800,work.Top+420),Size=new Size(230,110),TopMost=true})
                using(var button=new Button {Text="Synthetic drag source",Dock=DockStyle.Fill})
                using(var completed=new ManualResetEvent(false)) {
                    source.Controls.Add(button);
                    button.MouseDown+=delegate(object sender,MouseEventArgs e) {
                        if(e.Button==MouseButtons.Left) {
                            var data=new DataObject(DataFormats.FileDrop,new string[]{file});
                            try{button.DoDragDrop(data,DragDropEffects.Move);}finally{completed.Set();}
                        }
                    };
                    source.Show();RunFor(300);Layout layout=window.CurrentLayout;
                    Point target=window.PointToScreen(new Point(layout.Icon.X+layout.Icon.Width/2,layout.Icon.Y+layout.Icon.Height/2));
                    Point origin=button.PointToScreen(new Point(button.Width/2,button.Height/2));
                    var inputThread=new Thread(delegate() {
                        Thread.Sleep(300);Native.SetCursorPos(origin.X+15,origin.Y+8);
                        Thread.Sleep(200);Native.SetCursorPos(target.X,target.Y);
                        Thread.Sleep(700);Native.mouse_event(4,0,0,0,UIntPtr.Zero);
                        if(!completed.WaitOne(1000)) {
                            Native.keybd_event(27,0,0,UIntPtr.Zero);Native.keybd_event(27,0,2,UIntPtr.Zero);
                        }
                    });inputThread.IsBackground=true;
                    Native.SetCursorPos(origin.X,origin.Y);inputThread.Start();Native.mouse_event(2,0,0,0,UIntPtr.Zero);
                    RunFor(2500);Native.mouse_event(4,0,0,0,UIntPtr.Zero);inputThread.Join(1000);
                    added=deck.Entries.Count==1&&deck.Entries[0].Managed&&File.Exists(deck.Entries[0].Path);
                    preserved=!File.Exists(file)&&added&&File.ReadAllText(deck.Entries[0].Path)=="drop-test-content";
                }
            }finally{Native.mouse_event(4,0,0,0,UIntPtr.Zero);Native.SetCursorPos(original.X,original.Y);}
            File.WriteAllText(Path.Combine(root,"artifacts","drop-test.txt"),"Native OLE file drop collects actual file: "+added+"\r\nOriginal moved, stored content intact: "+preserved+"\r\nStatus: "+(added&&preserved?"PASS":"FAIL")+"\r\nSource: synthetic WinForms OLE source, not Explorer itself\r\n");
            return added&&preserved;
        }
    }
}





