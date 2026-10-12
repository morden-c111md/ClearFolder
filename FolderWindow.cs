using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Interop;

namespace ClearFolder {
    internal sealed class FolderWindow : IDisposable {
        readonly Store store;
        internal readonly Deck Deck;
        readonly GlassSurface surface;
        readonly HoverState hover=new HoverState();
        readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer {Interval=20};
        readonly Stopwatch clock=Stopwatch.StartNew();
        DesktopGrid grid;
        Layout layout;
        int page;
        long lastCapture=-1000,locateUntil;
        Point downMouse,downAnchor;
        bool pressed,dragging,menuOpen,dropHover,manualHidden,shownExpanded;
        volatile bool disposed;
        bool warnedCapture,warnedTick;
        IntPtr lastForeground;
        IntPtr lastDesktop;
        long lastDesktopCheck;
        bool? lastTopmost;
        int captureInFlight,generation,menuActionDepth;
        bool diagnosticBackground;
        readonly object captureBufferGate=new object();
        Bitmap captureBitmap;
        Graphics captureGraphics;
        readonly WindowBackgroundCapture backgroundCapture=new WindowBackgroundCapture();
        internal IntPtr BackgroundSourceOverride;
        int captureBufferAllocations;
        long captureStarted,captureCompleted,captureApplied,lastApplied=-1,captureRetryAfter;
        readonly List<double> captureCosts=new List<double>(),uploadCosts=new List<double>(),backgroundIntervals=new List<double>();
        Rectangle lastCaptureBounds;
        internal bool PauseBackgroundCapture;
        Entry pressedEntry;
        DragGhost ghost;
        bool transferUiBusy,ignoreMouseUp;
        public bool ForceVisible;
        internal Action<string> HoverTrace;
        internal sealed class BackgroundCaptureStatistics {
            internal long Started,Completed,Applied;
            internal int InFlight,CaptureBufferAllocations,DisplayBufferAllocations,SourceBufferAllocations;
            internal Rectangle Bounds;
            internal double[] CaptureMilliseconds,UploadMilliseconds,UpdateIntervals;
        }
        public FolderWindow(Store data,Deck deck) {
            store=data;Deck=deck;grid=DesktopGrid.Read(Deck.PlaceholderPath);
            surface=new GlassSurface();
            Recalculate();
            var hwnd=Handle;
            Native.SetWindowLongPtr(hwnd,-20,new IntPtr(Native.GetWindowLongPtr(hwnd,-20).ToInt64()|0x80|0x08000000));
            HwndSource.FromHwnd(hwnd).AddHook(WindowHook);
            surface.PreviewMouseDown+=delegate(object sender,System.Windows.Input.MouseButtonEventArgs e) {
                if(e.ChangedButton==System.Windows.Input.MouseButton.Left)OnDown(null,new MouseEventArgs(MouseButtons.Left,1,MouseLocal.X,MouseLocal.Y,0));
            };
            surface.PreviewMouseMove+=delegate{OnMove(null,new MouseEventArgs(MouseButtons.None,0,MouseLocal.X,MouseLocal.Y,0));};
            surface.PreviewMouseUp+=delegate(object sender,System.Windows.Input.MouseButtonEventArgs e) {
                var p=MouseLocal;
                OnUp(null,new MouseEventArgs(e.ChangedButton==System.Windows.Input.MouseButton.Right?MouseButtons.Right:MouseButtons.Left,1,p.X,p.Y,0));
            };
            surface.PreviewMouseWheel+=delegate(object sender,System.Windows.Input.MouseWheelEventArgs e) {if(hover.Open&&layout.Pages>1){ChangePage(e.Delta<0?1:-1);e.Handled=true;}};
            surface.AllowDrop=true;
            surface.DragEnter+=OnFileEnter;surface.DragOver+=OnFileEnter;
            surface.DragLeave+=delegate{dropHover=false;};
            surface.Drop+=OnFileDrop;
            surface.LostMouseCapture+=delegate{if(pressed)CancelDrag();};
            surface.Show();timer.Tick+=Tick;timer.Start();
        }
        internal IntPtr Handle {get{return surface.NativeHandle;}}
        internal bool Visible {get{return surface.IsVisible;}}
        internal bool IsTransferBusy {get{return transferUiBusy;}}
        internal string RendererDescription {get{return surface.RendererDescription+"；独立桌面窗口背景采集，支持系统截图/录屏";}}
        Point MouseLocal {get{return PointToClient(Cursor.Position);}}
        public Point PointToClient(Point point){return new Point(point.X-layout.Bounds.X,point.Y-layout.Bounds.Y);}
        public Point PointToScreen(Point point){return new Point(point.X+layout.Bounds.X,point.Y+layout.Bounds.Y);}
        internal void Recalculate() {
            Point desired=new Point(Deck.X,Deck.Y);Screen screen=Screen.FromPoint(desired);
            var reserved=store.State.Decks.Where(d=>d!=Deck).Select(d=>new Point(d.X,d.Y));
            grid=DesktopGrid.Read(Deck.PlaceholderPath);
            Point anchor=grid.PlaceholderPosition.HasValue?grid.PlaceholderPosition.Value:grid.Snap(desired,screen.WorkingArea,reserved);Deck.X=anchor.X;Deck.Y=anchor.Y;
            float scale;using(var graphics=Graphics.FromHwnd(IntPtr.Zero))scale=graphics.DpiX/96f;
            scale=Math.Max(.75f,Math.Min(2.5f,scale));
            layout=ClearFolder.Layout.Calculate(anchor,Deck.Entries.Count,page,screen.WorkingArea,scale,grid.Cell,grid.IconSize);
            page=layout.Page;surface.SetBounds(layout.Bounds);surface.Configure(layout,Deck,store.State);
            generation++;lastCapture=-1000;
        }
        void Tick(object sender,EventArgs e) {
            if(disposed||manualHidden)return;
            try {
                long now=clock.ElapsedMilliseconds;
                if(surface.WindowState==System.Windows.WindowState.Minimized)surface.WindowState=System.Windows.WindowState.Normal;
                if(!surface.IsVisible){surface.Show();lastCapture=-1000;}
                if(Native.IsIconic(Handle)||!Native.IsWindowVisible(Handle))Native.ShowWindow(Handle,4);
                IntPtr foreground=Native.GetForegroundWindow();
                IntPtr desktop=lastDesktop;
                if(now-lastDesktopCheck>1000||lastDesktopCheck==0){desktop=Native.DesktopRoot();lastDesktopCheck=now;}
                bool top=ForceVisible||now<locateUntil||menuOpen||pressed||dropHover||foreground==Handle;
                bool placementLost=!top&&desktop!=IntPtr.Zero&&!Native.AboveDesktop(Handle,desktop);
                if(!menuOpen&&menuActionDepth==0&&(lastTopmost!=top||lastForeground!=foreground||lastDesktop!=desktop||placementLost)) {
                    if(top)Native.SetWindowPos(Handle,new IntPtr(-1),0,0,0,0,0x13);
                    else {
                        Native.SetWindowPos(Handle,new IntPtr(-2),0,0,0,0,0x13);
                        IntPtr before=desktop==IntPtr.Zero?new IntPtr(1):Native.GetWindow(desktop,3);
                        while(before==Handle)before=Native.GetWindow(before,3);
                        Native.SetWindowPos(Handle,before,0,0,0,0,0x13);
                    }
                    lastTopmost=top;lastForeground=foreground;lastDesktop=desktop;
                }
                Point mouse=MouseLocal;
                IntPtr under=Native.WindowFromPoint(new Native.POINT(Cursor.Position.X,Cursor.Position.Y));
                bool receives=under==Handle||Native.GetAncestor(under,2)==Handle;
                bool openingBridge=hover.Open&&(surface.Expansion<.999||layout.InHoverCorridor(mouse))&&IsAboveTarget(under);
                bool inside=(receives||openingBridge)&&layout.ContainsHover(mouse,hover.Open||surface.Expansion>.05);
                if(HoverTrace!=null&&!menuOpen&&menuActionDepth==0&&!inside&&layout.Icon.Contains(mouse))HoverTrace("Missed entry pointer="+Cursor.Position+" own="+Handle+" hit="+under+"/"+Native.ClassName(under)+" foreground="+foreground+" top="+top+" visible="+Native.IsWindowVisible(Handle));
                bool priorOpen=hover.Open;
                hover.Update(now,inside,menuOpen||pressed||dropHover||transferUiBusy);
                if(Native.GetAsyncKeyState(27)<0){if(pressedEntry!=null)CancelDrag();if(hover.Open)hover.Close();}
                if(priorOpen!=hover.Open&&HoverTrace!=null)HoverTrace("Hover="+hover.Open+" pointer="+Cursor.Position+" local="+mouse+" contains="+layout.Contains(mouse,true)+" hit="+under+"/"+Native.ClassName(under)+" receives="+receives+" bridge="+openingBridge+" expansion="+surface.Expansion+" escape="+Native.GetAsyncKeyState(27));
                if(shownExpanded!=hover.Open){shownExpanded=hover.Open;surface.SetExpanded(shownExpanded);}
                surface.UpdatePointer(mouse,now/1000.0,inside,store.State.RippleStrength);
                bool visibleDesktop=top||receives||IsEntryExposed();
                // A 30ms sampling target is limited by timer scheduling and actual copy/
                // upload cost. There is only one frame in capture or awaiting UI upload.
                if(ghost==null&&!diagnosticBackground&&!PauseBackgroundCapture&&visibleDesktop&&now>=captureRetryAfter&&now-lastCapture>=30&&Interlocked.CompareExchange(ref captureInFlight,1,0)==0)CaptureBackground();
            }catch(Exception error) {
                if(!warnedTick){warnedTick=true;MessageBox.Show("入口仍保留，正在重试显示。\n"+error.Message,"ClearFolder 显示恢复",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
            }
        }
        bool IsAboveTarget(IntPtr target) {
            if(target==IntPtr.Zero)return false;
            IntPtr root=Native.GetAncestor(target,2);if(root==IntPtr.Zero)root=target;
            for(int count=0;root!=IntPtr.Zero&&count<512;count++,root=Native.GetWindow(root,3))
                if(root==Handle)return true;
            return false;
        }
        bool IsEntryExposed() {
            Point center=PointToScreen(new Point(layout.Icon.X+layout.Icon.Width/2,layout.Icon.Y+layout.Icon.Height/2));
            IntPtr hit=Native.WindowFromPoint(new Native.POINT(center.X,center.Y));
            if(hit==Handle||Native.GetAncestor(hit,2)==Handle)return true;
            if(surface.Expansion>.05) {
                center=PointToScreen(new Point(layout.Panel.X+layout.Panel.Width/2,layout.Panel.Y+layout.Panel.Height/2));
                hit=Native.WindowFromPoint(new Native.POINT(center.X,center.Y));
                return hit==Handle||Native.GetAncestor(hit,2)==Handle;
            }
            return false;
        }
        void CaptureBackground() {
            lastCapture=clock.ElapsedMilliseconds;
            Rectangle bounds=layout.Bounds;int current=generation;captureStarted++;lastCaptureBounds=bounds;
            ThreadPool.QueueUserWorkItem(delegate {
                Bitmap bitmap=null;Exception failure=null;double captureCost=0;
                try {
                    var elapsed=Stopwatch.StartNew();
                    lock(captureBufferGate) {
                        if(disposed)throw new ObjectDisposedException("FolderWindow");
                        if(captureBitmap==null||captureGraphics==null||captureBitmap.Size!=bounds.Size) {
                            DisposeCaptureBuffer();
                            captureBitmap=new Bitmap(bounds.Width,bounds.Height,PixelFormat.Format32bppRgb);
                            captureGraphics=Graphics.FromImage(captureBitmap);Interlocked.Increment(ref captureBufferAllocations);
                        }
                        bitmap=captureBitmap;
                        backgroundCapture.Capture(captureGraphics,bounds,Handle,BackgroundSourceOverride);
                    }
                    captureCost=elapsed.Elapsed.TotalMilliseconds;
                }catch(Exception error){failure=error;}
                if(disposed){lock(captureBufferGate)DisposeCaptureBuffer();Interlocked.Exchange(ref captureInFlight,0);return;}
                try {
                    surface.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(delegate {
                        try {
                            captureCompleted++;
                            if(disposed||current!=generation)return;
                            if(failure==null&&bitmap!=null) {
                                var elapsed=Stopwatch.StartNew();
                                lock(captureBufferGate)surface.SetBackground(bitmap,store.State.Contrast);
                                AddMetric(captureCosts,captureCost);AddMetric(uploadCosts,elapsed.Elapsed.TotalMilliseconds);
                                long applied=clock.ElapsedMilliseconds;if(lastApplied>=0)AddMetric(backgroundIntervals,applied-lastApplied);
                                lastApplied=applied;captureApplied++;
                            }else {
                                captureRetryAfter=clock.ElapsedMilliseconds+1000;
                                if(!warnedCapture){warnedCapture=true;MessageBox.Show("背景采样暂不可用，入口会继续保留并重试。\n"+(failure==null?"未返回背景缓冲。":failure.Message),"ClearFolder",MessageBoxButtons.OK,MessageBoxIcon.Information);}
                            }
                        }catch(Exception error) {
                            captureRetryAfter=clock.ElapsedMilliseconds+1000;
                            if(!disposed&&!warnedCapture){warnedCapture=true;MessageBox.Show("背景上传暂不可用，入口会继续保留并重试。\n"+error.Message,"ClearFolder",MessageBoxButtons.OK,MessageBoxIcon.Information);}
                        }finally{Interlocked.Exchange(ref captureInFlight,0);}
                    }));
                }catch {Interlocked.Exchange(ref captureInFlight,0);}
            });
        }
        static void AddMetric(List<double> values,double value){if(values.Count>=600)values.RemoveAt(0);values.Add(value);}
        void DisposeCaptureBuffer(){if(captureGraphics!=null){captureGraphics.Dispose();captureGraphics=null;}if(captureBitmap!=null){captureBitmap.Dispose();captureBitmap=null;}backgroundCapture.Dispose();}
        internal long BackgroundUpdates {get{return captureApplied;}}
        internal Color ReadBackgroundPixel(Point point){return surface.ReadBackgroundPixel(point);}
        internal BackgroundCaptureStatistics BackgroundStatistics() {
            return new BackgroundCaptureStatistics {Started=captureStarted,Completed=captureCompleted,Applied=captureApplied,InFlight=Interlocked.CompareExchange(ref captureInFlight,0,0),
                CaptureBufferAllocations=Interlocked.CompareExchange(ref captureBufferAllocations,0,0),DisplayBufferAllocations=surface.BackgroundBufferAllocations,SourceBufferAllocations=backgroundCapture.BufferAllocations,Bounds=lastCaptureBounds,
                CaptureMilliseconds=captureCosts.ToArray(),UploadMilliseconds=uploadCosts.ToArray(),UpdateIntervals=backgroundIntervals.ToArray()};
        }
        int HitCell(Point point) {
            if(surface.Expansion<.95)return -1;
            for(int i=0;i<layout.Cells.Length;i++)if(layout.Cells[i].Contains(point))return i;
            return -1;
        }
        void OnDown(object sender,MouseEventArgs e) {
            if(e.Button!=MouseButtons.Left||transferUiBusy||store.HasPendingTransfer)return;
            ignoreMouseUp=false;
            int index=HitCell(e.Location);
            if(index>=0){pressedEntry=Deck.Entries[layout.Start+index];pressed=true;downMouse=Cursor.Position;surface.CaptureMouse();return;}
            if(layout.EntrySlot.Contains(e.Location)) {
                grid=DesktopGrid.Read();pressed=true;downMouse=Cursor.Position;downAnchor=new Point(Deck.X,Deck.Y);surface.CaptureMouse();
            }
        }
        void OnMove(object sender,MouseEventArgs e) {
            surface.Cursor=HitCell(e.Location)>=0?System.Windows.Input.Cursors.Hand:System.Windows.Input.Cursors.Arrow;
            if(!pressed)return;Point current=Cursor.Position;
            if(Math.Abs(current.X-downMouse.X)+Math.Abs(current.Y-downMouse.Y)>5)dragging=true;
            if(pressedEntry!=null) {
                if(dragging){if(ghost==null){ghost=new DragGhost(surface.DragImage(pressedEntry));surface.SetDraggedEntry(pressedEntry,true);}ghost.Move(current);}
                return;
            }
            if(dragging) {
                Point desired=new Point(downAnchor.X+current.X-downMouse.X,downAnchor.Y+current.Y-downMouse.Y),snapped;
                try{grid=DesktopGrid.Read(Deck.PlaceholderPath);snapped=grid.Snap(desired,Screen.FromPoint(desired).WorkingArea,store.State.Decks.Where(d=>d!=Deck).Select(d=>new Point(d.X,d.Y)));}
                catch(InvalidOperationException){return;}
                if(snapped==new Point(Deck.X,Deck.Y))return;
                Point previous=new Point(Deck.X,Deck.Y);Deck.X=snapped.X;Deck.Y=snapped.Y;
                try{if(!string.IsNullOrEmpty(Deck.PlaceholderPath))DesktopGrid.PositionPlaceholder(Deck.PlaceholderPath,snapped);hover.Close();shownExpanded=false;surface.SetExpanded(false,true);Recalculate();}
                catch(Exception error){Deck.X=previous.X;Deck.Y=previous.Y;RunSafe(delegate{throw new InvalidOperationException("不能放置到该格子，保留原位置。",error);});}
            }
        }
        async void OnUp(object sender,MouseEventArgs e) {
            if(HoverTrace!=null)HoverTrace("Managed mouse up button="+e.Button+" point="+e.Location+" pending="+store.HasPendingTransfer);
            if(e.Button==MouseButtons.Right){ShowMenu(e.Location);return;}
            if(e.Button!=MouseButtons.Left)return;
            if(ignoreMouseUp){ignoreMouseUp=false;return;}
            if(transferUiBusy||store.HasPendingTransfer)return;
            if(pressedEntry!=null) {
                var item=pressedEntry;bool moved=dragging;Point destination=Cursor.Position;
                EndDrag();
                if(!moved){OpenEntry(item);return;}
                if(layout.Contains(e.Location,true))return;
                transferUiBusy=true;surface.ShowTransferStatus("正在识别目标文件夹…",true);
                try {
                    var resolving=DragGhost.ResolveAsync(destination,Handle);
                    if(await System.Threading.Tasks.Task.WhenAny(resolving,System.Threading.Tasks.Task.Delay(8000))!=resolving)
                        throw new TimeoutException("目标窗口响应超时，项目未移动。请在资源管理器文件区域重新拖放。");
                    var target=await resolving;
                    if(!target.Allowed)throw new InvalidOperationException(target.Description);
                    surface.ShowTransferStatus("正在移到 "+target.Description+"…",true);
                    await store.ReleaseToFolderAsync(Deck,item,target.Directory);
                }catch(Exception error){MessageBox.Show(error.Message,"项目保留与移动结果",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
                finally{transferUiBusy=false;if(!disposed){surface.ShowTransferStatus(null,false);RefreshContents();}}
                return;
            }
            bool wasDragging=dragging;pressed=false;dragging=false;surface.ReleaseMouseCapture();
            if(wasDragging){RunSafe(delegate{store.Save();});return;}
            int index=HitCell(e.Location);
            if(index>=0){OpenEntry(Deck.Entries[layout.Start+index]);return;}
            if(layout.Icon.Contains(e.Location)||layout.Label.Contains(e.Location))hover.Open=true;
            else if(hover.Open&&layout.Pages>1&&e.Y>=layout.Panel.Bottom-24*layout.Scale)ChangePage(e.X<layout.Panel.X+layout.Panel.Width/2?-1:1);
        }
        void EndDrag() {
            var item=pressedEntry;pressedEntry=null;pressed=false;dragging=false;
            if(ghost!=null){ghost.Dispose();ghost=null;}
            if(item!=null)surface.SetDraggedEntry(item,false);
            surface.ReleaseMouseCapture();
        }
        void CancelDrag(){ignoreMouseUp=true;EndDrag();}
        void OpenEntry(Entry entry) {
            RunSafe(delegate {
                if(!Store.Exists(entry.Path))throw new FileNotFoundException("这个项目已不存在，请右键移除引用或重新添加。",entry.Path);
                Process.Start(new ProcessStartInfo(entry.Path){UseShellExecute=true});hover.Close();
            });
        }
        void ChangePage(int change){page=(page+change+layout.Pages)%layout.Pages;Recalculate();}
        void OnFileEnter(object sender,System.Windows.DragEventArgs e) {
            var paths=e.Data.GetData(System.Windows.DataFormats.FileDrop) as string[];
            if(!transferUiBusy&&!store.HasPendingTransfer&&paths!=null&&paths.Length>0) {
                e.Effects=(e.AllowedEffects&System.Windows.DragDropEffects.Move)!=0&&paths.All(store.IsCollectablePath)?System.Windows.DragDropEffects.Move:System.Windows.DragDropEffects.None;
                dropHover=e.Effects!=System.Windows.DragDropEffects.None;if(dropHover)hover.Open=true;
            }else{e.Effects=System.Windows.DragDropEffects.None;dropHover=false;}
            e.Handled=true;
        }
        void OnFileDrop(object sender,System.Windows.DragEventArgs e) {
            dropHover=false;
            var paths=e.Data.GetData(System.Windows.DataFormats.FileDrop) as string[];if(paths==null)return;
            e.Handled=true;e.Effects=System.Windows.DragDropEffects.None;
            if(transferUiBusy||store.HasPendingTransfer||(e.AllowedEffects&System.Windows.DragDropEffects.Move)==0)return;
            var operation=CollectPathsAsync(paths);
            if(!operation.IsCompleted) {
                var frame=new System.Windows.Threading.DispatcherFrame();
                operation.GetAwaiter().OnCompleted(delegate{frame.Continue=false;});
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }
            if(operation.GetAwaiter().GetResult())e.Effects=System.Windows.DragDropEffects.Move;
        }
        async System.Threading.Tasks.Task<bool> CollectPathsAsync(string[] paths) {
            transferUiBusy=true;
            var failures=new System.Collections.Generic.List<string>();
            try {
                for(int i=0;i<paths.Length;i++) {
                    surface.ShowTransferStatus("正在收纳 "+(i+1)+"/"+paths.Length+"："+Path.GetFileName(paths[i]),true);
                    try{await store.CollectAsync(Deck,paths[i]);}catch(Exception error){failures.Add(Path.GetFileName(paths[i])+"："+error.Message);}
                    if(!disposed)RefreshContents();
                    if(store.HasPendingTransfer)break;
                }
            }finally{transferUiBusy=false;if(!disposed)surface.ShowTransferStatus(null,false);}
            if(failures.Count>0)MessageBox.Show(string.Join("\n",failures),"部分项目未完成收纳",MessageBoxButtons.OK,MessageBoxIcon.Warning);
            return failures.Count==0;
        }
        void RefreshContents(){surface.RefreshIcons();Recalculate();hover.Open=true;}
        void ShowMenu(Point local) {
            if(transferUiBusy||store.HasPendingTransfer)return;
            int selected=HitCell(local);
            Entry entry=selected>=0?Deck.Entries[layout.Start+selected]:null;
            var menu=new ContextMenuStrip();
            if(entry!=null) {
                AddMenuAction(menu,"打开 "+entry.Name,delegate{OpenEntry(entry);});
                AddMenuAction(menu,"在资源管理器中显示",delegate{RunSafe(delegate{Process.Start("explorer.exe","/select,\""+entry.Path+"\"");});});
                if(entry.Managed)AddMenuAction(menu,"恢复到原桌面位置",delegate{RunSafe(delegate{store.Restore(Deck,entry);RefreshContents();});});
                else {
                    AddMenuAction(menu,"移除引用（保留原文件）",delegate{RunSafe(delegate{store.RemoveReference(Deck,entry);RefreshContents();});});
                    AddMenuAction(menu,"收纳此桌面项目（移动原文件）…",delegate {
                        if(MessageBox.Show("将这个桌面项目移入 ClearFolder 的存储目录后，原桌面图标会消失。\n可通过“恢复到原桌面位置”还原。\n\n是否移动："+entry.Name+"？","收纳桌面项目",MessageBoxButtons.YesNo,MessageBoxIcon.Question)==DialogResult.Yes)
                            RunSafe(delegate{store.MoveIntoStorage(Deck,entry);RefreshContents();});
                    });
                }
                menu.Items.Add(new ToolStripSeparator());
            }
            AddMenuAction(menu,"添加文件或快捷方式引用（保留原文件）…",delegate{AddFiles();});
            AddMenuAction(menu,"添加文件夹引用…",delegate{AddDirectory();});
            AddMenuAction(menu,"重命名文件夹…",delegate{Rename();});
            AddMenuAction(menu,"恢复本组所有已移动项目…",delegate{RestoreAll();});
            menu.Items.Add(new ToolStripSeparator());
            AddMenuAction(menu,"玻璃更透亮",delegate{AdjustContrast(.03);});
            AddMenuAction(menu,"玻璃更自然",delegate{AdjustContrast(-.03);});
            var rippleItem=AddMenuAction(menu,surface.RippleAvailable?"鼠标水波（"+(store.State.RippleStrength>0?"已开启":"已关闭，点击开启")+"）":"鼠标水波（当前环境不可用）",delegate{SetRippleEnabled(store.State.RippleStrength<=0);});
            rippleItem.Checked=surface.RippleAvailable&&store.State.RippleStrength>0;
            rippleItem.Enabled=surface.RippleAvailable;
            rippleItem.ToolTipText=surface.RippleAvailable?"勾选表示已开启；鼠标经过玻璃时折射背景，纯色区域变化较轻。":surface.RendererDescription;
            AddMenuAction(menu,"隐藏此文件夹（托盘可恢复）",delegate{manualHidden=true;surface.Hide();});
            menuOpen=true;
            menu.Closed+=delegate{
                menuOpen=false;
                var cleanup=new System.Windows.Forms.Timer {Interval=50};
                cleanup.Tick+=delegate{if(menuActionDepth>0||menuOpen&&!disposed)return;cleanup.Stop();cleanup.Dispose();menu.Dispose();};
                cleanup.Start();
            };menu.Show(PointToScreen(local));
        }
        ToolStripMenuItem AddMenuAction(ContextMenuStrip menu,string text,EventHandler action) {
            return (ToolStripMenuItem)menu.Items.Add(text,null,delegate(object sender,EventArgs args){
                menuActionDepth++;
                try{action(sender,args);}finally{menuActionDepth--;}
            });
        }
        void SetRippleEnabled(bool enabled) {
            RunSafe(delegate {
                if(!surface.RippleAvailable)throw new InvalidOperationException(surface.RendererDescription);
                double prior=store.State.RippleStrength;store.State.RippleStrength=enabled?1.8:0;
                try{store.Save();}catch{store.State.RippleStrength=prior;throw;}
                surface.Configure(layout,Deck,store.State);
                surface.UpdatePointer(MouseLocal,clock.Elapsed.TotalSeconds,false,store.State.RippleStrength);
            });
        }
        void AdjustContrast(double delta){RunSafe(delegate{store.State.Contrast=Math.Max(1,Math.Min(1.3,store.State.Contrast+delta));store.Save();lastCapture=-1000;surface.Configure(layout,Deck,store.State);});}
        internal void AddFiles() {
            if(transferUiBusy||store.HasPendingTransfer)return;
            menuOpen=true;
            try {using(var dialog=new OpenFileDialog {Multiselect=true,Title="添加引用：原文件保留原位",CheckFileExists=true})if(dialog.ShowDialog()==DialogResult.OK)RunSafe(delegate{foreach(string path in dialog.FileNames)store.AddReference(Deck,path);RefreshContents();});}
            finally{menuOpen=false;}
        }
        void AddDirectory() {
            menuOpen=true;
            try {using(var dialog=new FolderBrowserDialog {Description="添加文件夹引用，原文件夹保留原位",ShowNewFolderButton=false})if(dialog.ShowDialog()==DialogResult.OK)RunSafe(delegate{store.AddReference(Deck,dialog.SelectedPath);RefreshContents();});}
            finally{menuOpen=false;}
        }
        void Rename() {
            menuOpen=true;
            try {
                string name=InputDialog.Ask("文件夹名称",Deck.Name,Handle);
                if(name==null)return;
                string prior=Deck.Name;Deck.Name=name;
                RunSafe(delegate{try{store.Save();}catch{Deck.Name=prior;throw;}surface.Configure(layout,Deck,store.State);});
            }finally{menuOpen=false;}
        }
        void RestoreAll() {
            if(MessageBox.Show("恢复本组所有已经移动的项目到原位置？同名冲突不会覆盖。","恢复项目",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
            RunSafe(delegate{foreach(var entry in Deck.Entries.Where(e=>e.Managed).ToArray())store.Restore(Deck,entry);RefreshContents();});
        }
        void RunSafe(Action action){try{action();}catch(Exception error){MessageBox.Show(error.Message,"ClearFolder",MessageBoxButtons.OK,MessageBoxIcon.Warning);}}
        internal void Reveal(){manualHidden=false;surface.Configure(layout,Deck,store.State);timer.Start();}
        internal void ExportFrame(string path){surface.ExportFrame(path);}
        internal Layout CurrentLayout{get{return layout;}}
        internal float Expansion{get{return (float)surface.Expansion;}}
        internal bool IsExpanded{get{return hover.Open;}}
        internal void Locate(){Reveal();locateUntil=clock.ElapsedMilliseconds+4500;lastTopmost=null;}
        internal double[] FrameIntervals(){return surface.FrameIntervals();}
        internal string GridDescription{get{return grid.Source+"; cell="+grid.Cell+"; origin="+grid.Origin+"; icon="+grid.IconSize;}}
        internal void SetDiagnosticBackground(Bitmap bitmap){diagnosticBackground=true;generation++;surface.SetBackground(bitmap,store.State.Contrast);}
        IntPtr WindowHook(IntPtr hwnd,int msg,IntPtr wParam,IntPtr lParam,ref bool handled) {
            if(HoverTrace!=null&&(msg==0x204||msg==0x205))HoverTrace("Native right mouse message="+msg+" cursor="+Cursor.Position);
            if(msg==0x84&&layout!=null) {
                long value=lParam.ToInt64();var p=PointToClient(new Point((short)(value&0xffff),(short)((value>>16)&0xffff)));
                handled=true;return layout.Contains(p,hover.Open||surface.Expansion>.05)?new IntPtr(1):new IntPtr(-1);
            }
            if(msg==0x21){handled=true;return new IntPtr(3);}
            return IntPtr.Zero;
        }
        public void Dispose(){if(disposed)return;EndDrag();disposed=true;timer.Stop();timer.Dispose();if(Monitor.TryEnter(captureBufferGate)){try{DisposeCaptureBuffer();}finally{Monitor.Exit(captureBufferGate);}}surface.Close();}
    }
    internal static class InputDialog {
        sealed class Owner : System.Windows.Forms.IWin32Window {readonly IntPtr handle;internal Owner(IntPtr value){handle=value;}public IntPtr Handle{get{return handle;}}}
        public static string Ask(string title,string initial,IntPtr owner=default(IntPtr)) {
            using(var form=new Form {Text=title,Width=330,Height=150,FormBorderStyle=FormBorderStyle.FixedDialog,StartPosition=FormStartPosition.CenterScreen,MaximizeBox=false,MinimizeBox=false})
            using(var input=new TextBox {Left=18,Top=18,Width=275,Text=initial,MaxLength=32})
            using(var ok=new Button {Text="确定",Left=133,Top=57,Width=75,DialogResult=DialogResult.OK})
            using(var cancel=new Button {Text="取消",Left=218,Top=57,Width=75,DialogResult=DialogResult.Cancel}) {
                form.Controls.AddRange(new Control[]{input,ok,cancel});form.AcceptButton=ok;form.CancelButton=cancel;
                form.Shown+=delegate{form.Activate();input.Focus();input.SelectAll();};
                var result=owner==IntPtr.Zero?form.ShowDialog():form.ShowDialog(new Owner(owner));
                return result==DialogResult.OK&&!string.IsNullOrWhiteSpace(input.Text)?input.Text.Trim():null;
            }
        }
    }
}


