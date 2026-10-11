using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Drawing=System.Drawing;

namespace ClearFolder {
    internal sealed class GlassSurface : Window,IDisposable {
        readonly Canvas canvas=new Canvas(),panel=new Canvas(),mini=new Canvas(),foreground=new Canvas();
        readonly Canvas iconGlass=new Canvas(),panelGlass=new Canvas();
        readonly Image iconBackground=new Image(),panelBackground=new Image();
        readonly System.Windows.Shapes.Path iconEdge=new System.Windows.Shapes.Path {IsHitTestVisible=false};
        readonly System.Windows.Shapes.Path panelEdge=new System.Windows.Shapes.Path {IsHitTestVisible=false};
        readonly ScaleTransform panelScale=new ScaleTransform(.08,.08);
        readonly ScaleTransform physicalScale=new ScaleTransform(1,1);
        readonly RippleState ripples=new RippleState();
        readonly Stopwatch clock=Stopwatch.StartNew();
        readonly Dictionary<string,BitmapSource> icons=new Dictionary<string,BitmapSource>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,UIElement[]> entryVisuals=new Dictionary<string,UIElement[]>(StringComparer.Ordinal);
        readonly WaterEffect water;
        readonly BitmapSource placeholder;
        readonly List<double> frameIntervals=new List<double>();
        Layout layout;
        Deck deck;
        TextBlock header;
        string transferMessage,draggedId;
        bool transferBusy;
        BitmapSource background;
        WriteableBitmap backgroundBuffer;
        readonly byte[] backgroundProbe=new byte[4];
        long lastTextToneChange=-1000;
        internal int BackgroundBufferAllocations {get;private set;}
        double contrast=1.12,strength,lastFrame;
        bool expanded,rendering,darkText,compatibilityMaterial,warming,warmed;
        int warmFrames;
        volatile bool disposed;
        volatile int iconGeneration;
        public string RendererDescription {get;private set;}
        public IntPtr NativeHandle {get;private set;}
        internal bool RippleAvailable {get{return water!=null&&!compatibilityMaterial;}}
        public double Expansion {get{return warming?0:Math.Max(0,(panelScale.ScaleX-.08)/.92);}}
        public GlassSurface() {
            WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;AllowsTransparency=true;
            Background=Brushes.Transparent;ShowInTaskbar=false;ShowActivated=false;Focusable=false;
            AllowDrop=true;canvas.Background=Brushes.Transparent;Content=canvas;
            canvas.RenderTransform=physicalScale;
            string description;water=WaterEffect.Create(out description);RendererDescription=description;
            using(var bitmap=Drawing.SystemIcons.Application.ToBitmap())placeholder=Upload(bitmap);
            iconGlass.Children.Add(iconBackground);panelGlass.Children.Add(panelBackground);
            if(water!=null){iconBackground.Effect=water;panelBackground.Effect=water;}
            panel.Children.Add(panelGlass);panel.Children.Add(panelEdge);panel.Children.Add(foreground);panel.RenderTransform=panelScale;panel.Opacity=0;
            // Static icons and text can be reused by the compositor while glass continues
            // to refract separately. Do not cache the glass/shader subtree.
            foreground.CacheMode=new BitmapCache(1);mini.CacheMode=new BitmapCache(1);
            iconGlass.Background=panelGlass.Background=new SolidColorBrush(Color.FromArgb(18,15,25,35));
            panel.Visibility=Visibility.Collapsed;canvas.Children.Add(iconGlass);canvas.Children.Add(iconEdge);canvas.Children.Add(panel);canvas.Children.Add(mini);
            NativeHandle=new WindowInteropHelper(this).EnsureHandle();
            Closed+=delegate {DisposeResources();};
        }
        public void SetBounds(Drawing.Rectangle bounds) {
            var source=HwndSource.FromHwnd(NativeHandle);var transform=source.CompositionTarget.TransformFromDevice;
            var origin=transform.Transform(new Point(bounds.X,bounds.Y));
            var size=transform.Transform(new Vector(bounds.Width,bounds.Height));
            Left=origin.X;Top=origin.Y;Width=Math.Max(1,size.X);Height=Math.Max(1,size.Y);
            physicalScale.ScaleX=transform.M11;physicalScale.ScaleY=transform.M22;
        }
        public void Configure(Layout value,Deck group,Settings options) {
            layout=value;deck=group;
            canvas.Width=value.Bounds.Width;canvas.Height=value.Bounds.Height;
            panel.Width=canvas.Width;panel.Height=canvas.Height;mini.Width=canvas.Width;mini.Height=canvas.Height;
            foreground.Width=canvas.Width;foreground.Height=canvas.Height;
            iconGlass.Width=panelGlass.Width=canvas.Width;iconGlass.Height=panelGlass.Height=canvas.Height;
            iconBackground.Width=panelBackground.Width=canvas.Width;iconBackground.Height=panelBackground.Height=canvas.Height;
            using(var path=ClearFolder.Layout.Round(value.Icon,10*value.Scale))iconGlass.Clip=ConvertPath(path);
            using(var path=value.PanelPath())panelGlass.Clip=ConvertPath(path);
            ConfigureEdge(iconEdge,iconGlass.Clip);
            ConfigureEdge(panelEdge,panelGlass.Clip);
            double originX=value.Left?value.Icon.Left:value.Icon.Right;
            panelScale.CenterX=originX;panelScale.CenterY=value.Icon.Y+value.Icon.Height/2.0;
            RebuildForeground();RefreshIcons();UpdateShader(clock.Elapsed.TotalSeconds);
        }
        static void ConfigureEdge(System.Windows.Shapes.Path edge,Geometry geometry) {
            edge.Data=geometry;edge.StrokeThickness=1;edge.StrokeLineJoin=PenLineJoin.Round;
            var brush=new LinearGradientBrush {StartPoint=new Point(0,0),EndPoint=new Point(1,1)};
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(65,255,255,255),0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(18,255,255,255),.5));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(38,255,255,255),1));
            brush.Freeze();edge.Stroke=brush;
        }
        public void SetBackground(Drawing.Bitmap source,double value) {
            if(disposed||compatibilityMaterial)return;
            contrast=Math.Max(1,Math.Min(1.3,value));
            if(source.PixelFormat!=System.Drawing.Imaging.PixelFormat.Format32bppRgb&&source.PixelFormat!=System.Drawing.Imaging.PixelFormat.Format32bppArgb) {
                using(var converted=new Drawing.Bitmap(source.Width,source.Height,System.Drawing.Imaging.PixelFormat.Format32bppRgb)) {
                    using(var graphics=Drawing.Graphics.FromImage(converted))graphics.DrawImageUnscaled(source,0,0);
                    SetBackground(converted,value);
                }
                return;
            }
            if(backgroundBuffer==null||backgroundBuffer.PixelWidth!=source.Width||backgroundBuffer.PixelHeight!=source.Height) {
                // Screen captures are opaque BGR pixels. Bgr32 deliberately ignores the
                // undefined GDI alpha byte, avoiding a per-pixel alpha conversion pass.
                backgroundBuffer=new WriteableBitmap(source.Width,source.Height,96,96,PixelFormats.Bgr32,null);
                BackgroundBufferAllocations++;background=backgroundBuffer;
                iconBackground.Source=panelBackground.Source=background;
            }
            var rectangle=new Drawing.Rectangle(0,0,source.Width,source.Height);
            var bits=source.LockBits(rectangle,ImageLockMode.ReadOnly,source.PixelFormat);
            try {
                if(bits.Stride<=0)throw new InvalidDataException("背景缓冲的行步长必须为正。");
                backgroundBuffer.WritePixels(new Int32Rect(0,0,source.Width,source.Height),bits.Scan0,checked(bits.Stride*source.Height),bits.Stride);
            }finally{source.UnlockBits(bits);}
            iconGlass.Background=panelGlass.Background=Brushes.Transparent;
            bool nextDark=BackgroundIsBright();
            if(nextDark!=darkText&&clock.ElapsedMilliseconds-lastTextToneChange>=500){darkText=nextDark;lastTextToneChange=clock.ElapsedMilliseconds;RebuildForeground();}
            UpdateShader(clock.Elapsed.TotalSeconds);
            if(!expanded)Prewarm();
        }
        public void SetCompatibilityMaterial() {
            compatibilityMaterial=true;background=null;backgroundBuffer=null;iconBackground.Source=panelBackground.Source=null;
            iconGlass.Background=panelGlass.Background=new SolidColorBrush(Color.FromArgb(72,18,26,35));
            RendererDescription+="；屏幕采样保护不可用，使用透明兼容底板（无背景折射）";
            darkText=false;RebuildForeground();
            if(!expanded)Prewarm();
        }
        void Prewarm() {
            if(warmed||warming||layout==null)return;
            // Prepare the actual product subtree before its first opening. Full-size glass
            // and foreground cache are submitted at opacity below one 8-bit alpha step.
            // The controller sees Expansion zero and cannot hit-test this hidden panel.
            warming=true;warmFrames=0;panelScale.ScaleX=panelScale.ScaleY=1;
            panel.Opacity=.001;panel.Visibility=Visibility.Visible;StartRendering();
        }
        static BitmapSource Upload(Drawing.Bitmap bitmap) {
            var rectangle=new Drawing.Rectangle(0,0,bitmap.Width,bitmap.Height);
            using(var converted=new Drawing.Bitmap(bitmap.Width,bitmap.Height,System.Drawing.Imaging.PixelFormat.Format32bppArgb)) {
                using(var graphics=Drawing.Graphics.FromImage(converted))graphics.DrawImageUnscaled(bitmap,0,0);
                var bits=converted.LockBits(rectangle,ImageLockMode.ReadOnly,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try {
                    int stride=bitmap.Width*4;byte[] pixels=new byte[stride*bitmap.Height];
                    for(int y=0;y<bitmap.Height;y++)Marshal.Copy(IntPtr.Add(bits.Scan0,y*bits.Stride),pixels,y*stride,stride);
                    var image=BitmapSource.Create(bitmap.Width,bitmap.Height,96,96,PixelFormats.Bgra32,null,pixels,stride);image.Freeze();return image;
                }finally{converted.UnlockBits(bits);}
            }
        }
        public void SetExpanded(bool open,bool immediate=false) {
            expanded=open;
            if(open&&!immediate&&!warmed&&!warming)Prewarm();
            if(immediate&&open){warming=false;warmed=true;}
            if(warming)return;
            double currentScale=panelScale.ScaleX,currentOpacity=panel.Opacity,target=open?1:.08;
            panelScale.BeginAnimation(ScaleTransform.ScaleXProperty,null);panelScale.BeginAnimation(ScaleTransform.ScaleYProperty,null);
            panel.BeginAnimation(UIElement.OpacityProperty,null);
            panelScale.ScaleX=panelScale.ScaleY=currentScale;panel.Opacity=currentOpacity;
            if(immediate) {
                panelScale.ScaleX=panelScale.ScaleY=target;panel.Opacity=open?1:0;
                panel.Visibility=open?Visibility.Visible:Visibility.Collapsed;return;
            }
            panel.Visibility=Visibility.Visible;
            double seconds=(open?.32:.24)*Math.Max(.28,Math.Abs(target-currentScale)/.92);
            var duration=TimeSpan.FromSeconds(seconds);
            var scale=new DoubleAnimationUsingKeyFrames {Duration=duration};
            scale.KeyFrames.Add(new DiscreteDoubleKeyFrame(currentScale,KeyTime.FromTimeSpan(TimeSpan.Zero)));
            if(open) {
                scale.KeyFrames.Add(new SplineDoubleKeyFrame(1.025,KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds*.78)),new KeySpline(.22,0,.22,1)));
                scale.KeyFrames.Add(new SplineDoubleKeyFrame(1,KeyTime.FromTimeSpan(duration),new KeySpline(.25,0,.4,1)));
            }else scale.KeyFrames.Add(new SplineDoubleKeyFrame(target,KeyTime.FromTimeSpan(duration),new KeySpline(.45,0,.7,.35)));
            var opacity=new DoubleAnimation(currentOpacity,open?1:0,TimeSpan.FromSeconds(seconds*(open?.55:1)));
            opacity.EasingFunction=new CubicEase {EasingMode=open?EasingMode.EaseOut:EasingMode.EaseIn};
            scale.Completed+=delegate {if(!expanded){panel.Visibility=Visibility.Collapsed;}StopRenderingIfIdle();};
            panelScale.BeginAnimation(ScaleTransform.ScaleXProperty,scale,HandoffBehavior.SnapshotAndReplace);
            panelScale.BeginAnimation(ScaleTransform.ScaleYProperty,scale.Clone(),HandoffBehavior.SnapshotAndReplace);
            panel.BeginAnimation(UIElement.OpacityProperty,opacity,HandoffBehavior.SnapshotAndReplace);
            StartRendering();
        }
        public void UpdatePointer(Drawing.Point point,double now,bool inside,double rippleStrength) {
            if(disposed||layout==null)return;
            // Use one monotonic clock for wave births, expiry and callback intervals.
            // The controller's clock can start at a different instant during construction.
            now=clock.Elapsed.TotalSeconds;strength=Math.Max(0,Math.Min(6,rippleStrength*2.2));
            // More than one outgoing ring remains visible while the pointer moves.
            ripples.Pointer(point.X,point.Y,now,inside,strength>0);
            if(strength==0)ripples.Clear();
            if(ripples.Waves.Count>0)StartRendering();else UpdateShader(now);
        }
        void StartRendering() {if(rendering||disposed)return;rendering=true;lastFrame=0;CompositionTarget.Rendering+=OnRendering;}
        void StopRenderingIfIdle() {
            if(!rendering||warming||ripples.Waves.Count>0||panelScale.HasAnimatedProperties&&Math.Abs(Expansion-(expanded?1:0))>.002)return;
            CompositionTarget.Rendering-=OnRendering;rendering=false;
        }
        void OnRendering(object sender,EventArgs args) {
            if(disposed)return;
            double now=clock.Elapsed.TotalSeconds;
            if(lastFrame>0){frameIntervals.Add((now-lastFrame)*1000);if(frameIntervals.Count>600)frameIntervals.RemoveAt(0);}
            lastFrame=now;ripples.Advance(now);UpdateShader(now);
            if(warming&&++warmFrames>=3) {
                warming=false;warmed=true;panelScale.ScaleX=panelScale.ScaleY=.08;
                panel.Opacity=0;panel.Visibility=Visibility.Collapsed;
                if(expanded)SetExpanded(true);
            }
            StopRenderingIfIdle();
        }
        void UpdateShader(double now) {
            if(water==null||layout==null)return;
            water.Parameters=new Point4D(layout.Bounds.Width,layout.Bounds.Height,strength,contrast);
            water.WaveA=Wave(0,now);water.WaveB=Wave(1,now);water.WaveC=Wave(2,now);
        }
        Point4D Wave(int index,double now) {
            if(index>=ripples.Waves.Count)return new Point4D(0,0,10,0);
            var wave=ripples.Waves[index];return new Point4D(wave.X/layout.Bounds.Width,wave.Y/layout.Bounds.Height,Math.Max(0,now-wave.Born),1);
        }
        public double[] FrameIntervals(){return frameIntervals.ToArray();}
        public void RefreshIcons() {
            if(disposed||deck==null)return;
            int generation=++iconGeneration;var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var entry in deck.Entries)paths.Add(entry.Path);
            var worker=new Thread(delegate() {
                foreach(string path in paths) {
                    if(disposed||generation!=iconGeneration)return;
                    BitmapSource loaded;
                    try {using(var bitmap=FileIcons.Load(path))loaded=Upload(bitmap);}catch{loaded=placeholder;}
                    if(disposed||Dispatcher.HasShutdownStarted)return;
                    string key=path;BitmapSource result=loaded;
                    Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(delegate {
                        if(disposed||generation!=iconGeneration)return;
                        icons[key]=result;RebuildForeground();
                    }));
                }
            });worker.IsBackground=true;worker.SetApartmentState(ApartmentState.STA);worker.Start();
        }
        BitmapSource IconFor(Entry item){BitmapSource image;return icons.TryGetValue(item.Path,out image)?image:placeholder;}
        internal BitmapSource DragImage(Entry item){return item==null?placeholder:IconFor(item);}
        public void ShowTransferStatus(string message,bool busy) {
            if(disposed)return;
            transferMessage=message;transferBusy=busy;UpdateTransferHeader();
        }
        void UpdateTransferHeader() {
            if(header==null||deck==null)return;
            header.Text=string.IsNullOrWhiteSpace(transferMessage)?deck.Name:transferMessage;
            header.ToolTip=string.IsNullOrWhiteSpace(transferMessage)?null:transferMessage;
            header.FontWeight=transferBusy?FontWeights.SemiBold:FontWeights.Normal;
        }
        public void SetDraggedEntry(Entry item,bool dragging) {
            if(disposed)return;
            draggedId=dragging&&item!=null?item.Id:null;
            foreach(var pair in entryVisuals)foreach(var visual in pair.Value)visual.Opacity=pair.Key==draggedId ? .28 : 1;
        }
        void RebuildForeground() {
            if(layout==null||deck==null)return;
            mini.Children.Clear();foreground.Children.Clear();entryVisuals.Clear();
            double scale=layout.Scale;
            AddText(mini,deck.Name,layout.Label.X,layout.Label.Y,layout.Label.Width,layout.Label.Height,12*scale,true,Brushes.White,true);
            int miniCount=Math.Min(4,deck.Entries.Count);
            if(miniCount==0)AddText(mini,"+",layout.Icon.X,layout.Icon.Y,layout.Icon.Width,layout.Icon.Height,23*scale,true,Brushes.White);
            for(int i=0;i<miniCount;i++)AddIcon(mini,IconFor(deck.Entries[i]),layout.Icon.X+layout.Icon.Width*.12+(i%2)*layout.Icon.Width*.44,layout.Icon.Y+layout.Icon.Height*.12+(i/2)*layout.Icon.Height*.44,layout.Icon.Width*.34);
            var textBrush=ForegroundBrush();
            header=AddText(foreground,deck.Name,layout.Panel.X+14*scale,layout.Panel.Y+5*scale,layout.Panel.Width-28*scale,24*scale,12*scale,false,textBrush);
            header.TextWrapping=TextWrapping.NoWrap;UpdateTransferHeader();
            if(deck.Entries.Count==0) {
                AddText(foreground,"拖入文件或快捷方式",layout.Panel.X+6,layout.Panel.Y+42*scale,layout.Panel.Width-12,28*scale,11*scale,true,textBrush);
                AddText(foreground,"右键可添加、重命名",layout.Panel.X+6,layout.Panel.Y+70*scale,layout.Panel.Width-12,20*scale,10*scale,true,textBrush);
            }
            for(int i=0;i<layout.Cells.Length;i++) {
                Drawing.Rectangle cell=layout.Cells[i];var item=deck.Entries[layout.Start+i];double size=36*scale;
                var hover=new Border {Width=cell.Width,Height=cell.Height,CornerRadius=new CornerRadius(8*scale),Background=Brushes.Transparent};
                hover.MouseEnter+=delegate {hover.Background=new SolidColorBrush(Color.FromArgb(24,128,150,180));};hover.MouseLeave+=delegate {hover.Background=Brushes.Transparent;};
                Canvas.SetLeft(hover,cell.X);Canvas.SetTop(hover,cell.Y);foreground.Children.Add(hover);
                Image image=AddIcon(foreground,IconFor(item),cell.X+(cell.Width-size)/2,cell.Y+4*scale,size);
                TextBlock label=AddText(foreground,item.Name,cell.X+2,cell.Y+43*scale,cell.Width-4,29*scale,11*scale,true,textBrush);
                entryVisuals[item.Id]=new UIElement[]{hover,image,label};
                if(item.Id==draggedId){hover.Opacity=image.Opacity=label.Opacity=.28;}
            }
            if(layout.Pages>1)AddText(foreground,"‹    "+(layout.Page+1)+" / "+layout.Pages+"    ›",layout.Panel.X,layout.Panel.Bottom-23*scale,layout.Panel.Width,20*scale,11*scale,true,textBrush);
        }
        bool BackgroundIsBright() {
            if(background==null||layout==null)return false;
            int x=Math.Max(0,Math.Min(background.PixelWidth-1,layout.Panel.X+layout.Panel.Width/2));
            int y=Math.Max(0,Math.Min(background.PixelHeight-1,layout.Panel.Y+layout.Panel.Height/2));
            Drawing.Color color=ReadBackgroundPixel(new Drawing.Point(x,y));
            double luminance=color.B*.0722+color.G*.7152+color.R*.2126;
            return luminance>(darkText?140:160);
        }
        internal Drawing.Color ReadBackgroundPixel(Drawing.Point point) {
            if(background==null||point.X<0||point.Y<0||point.X>=background.PixelWidth||point.Y>=background.PixelHeight)throw new InvalidOperationException("背景像素尚不可读。");
            background.CopyPixels(new Int32Rect(point.X,point.Y,1,1),backgroundProbe,4,0);
            return Drawing.Color.FromArgb(backgroundProbe[2],backgroundProbe[1],backgroundProbe[0]);
        }
        Brush ForegroundBrush(){return darkText?new SolidColorBrush(Color.FromRgb(24,29,38)):Brushes.White;}
        static Image AddIcon(Canvas target,BitmapSource source,double x,double y,double size) {
            var image=new Image {Source=source,Width=size,Height=size,Stretch=Stretch.Uniform,IsHitTestVisible=false};
            RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);Canvas.SetLeft(image,x);Canvas.SetTop(image,y);target.Children.Add(image);return image;
        }
        static TextBlock AddText(Canvas target,string text,double x,double y,double width,double height,double size,bool centered,Brush brush,bool shadow=false) {
            var label=new TextBlock {Text=text??"",Width=Math.Max(1,width),Height=Math.Max(1,height),FontFamily=new FontFamily("Microsoft YaHei UI"),FontSize=Math.Max(8,size),Foreground=brush,
                TextAlignment=centered?TextAlignment.Center:TextAlignment.Left,TextTrimming=TextTrimming.CharacterEllipsis,TextWrapping=TextWrapping.Wrap,IsHitTestVisible=false};
            if(shadow)label.Effect=new System.Windows.Media.Effects.DropShadowEffect {Color=Colors.Black,Opacity=.35,BlurRadius=2,ShadowDepth=1};
            Canvas.SetLeft(label,x);Canvas.SetTop(label,y);target.Children.Add(label);return label;
        }
        static Geometry ConvertPath(Drawing.Drawing2D.GraphicsPath path) {
            var geometry=new StreamGeometry {FillRule=FillRule.Nonzero};var points=path.PathPoints;var types=path.PathTypes;
            using(var context=geometry.Open())for(int i=0;i<points.Length;i++) {
                var point=new Point(points[i].X,points[i].Y);int kind=types[i]&7;
                if(kind==0)context.BeginFigure(point,true,true);
                else if(kind==1)context.LineTo(point,true,false);
                else if(kind==3&&i+2<points.Length){context.BezierTo(point,new Point(points[i+1].X,points[i+1].Y),new Point(points[i+2].X,points[i+2].Y),true,false);i+=2;}
            }
            geometry.Freeze();return geometry;
        }
        public void ExportFrame(string path) {
            if(layout==null)throw new InvalidOperationException("尚未配置布局。");
            canvas.UpdateLayout();var bitmap=new RenderTargetBitmap(layout.Bounds.Width,layout.Bounds.Height,96,96,PixelFormats.Pbgra32);
            // Diagnostic output is explicitly requested; normal background samples stay in memory.
            Transform previous=canvas.RenderTransform;canvas.RenderTransform=Transform.Identity;
            // RenderTargetBitmap uses software rendering; HardwareOnly PS3 cannot be captured
            // through it. This frame records geometry/icons/clear background, not GPU ripples.
            iconBackground.Effect=panelBackground.Effect=null;
            try {canvas.UpdateLayout();bitmap.Render(canvas);}finally{canvas.RenderTransform=previous;iconBackground.Effect=panelBackground.Effect=water;}
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(path))encoder.Save(stream);
        }
        void DisposeResources() {
            if(disposed)return;disposed=true;iconGeneration++;CompositionTarget.Rendering-=OnRendering;rendering=false;
            icons.Clear();entryVisuals.Clear();background=null;backgroundBuffer=null;iconBackground.Source=panelBackground.Source=null;ripples.Clear();
        }
        public void Dispose(){if(!disposed){DisposeResources();Close();}}
    }
}
