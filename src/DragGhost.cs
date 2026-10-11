using System;
using Point = System.Drawing.Point;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClearFolder {
    internal sealed class DragGhost : IDisposable {
        readonly Window window;
        internal DragGhost(BitmapSource icon) {
            window=new Window {WindowStyle=WindowStyle.None,AllowsTransparency=true,Background=Brushes.Transparent,
                ShowInTaskbar=false,ShowActivated=false,Topmost=true,ResizeMode=ResizeMode.NoResize,
                Width=210,Height=100,IsHitTestVisible=false};
            var panel=new StackPanel {Opacity=.85};
            panel.Children.Add(new Image {Source=icon,Width=48,Height=48});
            panel.Children.Add(new TextBlock {Text="松手移到目标文件夹 · Esc 取消",Foreground=Brushes.White,
                Background=new SolidColorBrush(System.Windows.Media.Color.FromArgb(160,20,20,20)),Padding=new Thickness(5),HorizontalAlignment=HorizontalAlignment.Center});
            window.Content=panel;
            var handle=new WindowInteropHelper(window).EnsureHandle();
            Native.SetWindowLongPtr(handle,-20,new IntPtr(Native.GetWindowLongPtr(handle,-20).ToInt64()|0x20|0x80|0x08000000));
            Native.SetWindowDisplayAffinity(handle,0x11);
            HwndSource.FromHwnd(handle).AddHook(delegate(IntPtr h,int m,IntPtr w,IntPtr l,ref bool handled){if(m==0x84){handled=true;return new IntPtr(-1);}return IntPtr.Zero;});
        }
        internal void Move(Point point) {
            var source=PresentationSource.FromVisual(window);
            var local=source==null?new System.Windows.Point(point.X,point.Y):source.CompositionTarget.TransformFromDevice.Transform(new System.Windows.Point(point.X,point.Y));
            window.Left=local.X-24;window.Top=local.Y-24;if(!window.IsVisible)window.Show();
        }
        public void Dispose(){window.Close();}
        internal static Task<DropDestinationResult> ResolveAsync(Point point,IntPtr source) {
            var completion=new TaskCompletionSource<DropDestinationResult>();
            var thread=new Thread(delegate(){try{completion.SetResult(DropDestinations.Resolve(point,source));}catch(Exception e){completion.SetException(e);}});
            thread.IsBackground=true;thread.SetApartmentState(ApartmentState.STA);thread.Start();return completion.Task;
        }
    }
}
