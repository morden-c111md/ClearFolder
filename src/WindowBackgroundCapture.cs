using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace ClearFolder {
    // Render the source window itself, rather than the composed screen containing
    // our glass. PrintWindow is synchronous and is called only by the capture worker.
    internal sealed class WindowBackgroundCapture : IDisposable {
        IntPtr desktop;
        Bitmap frame;
        Graphics frameGraphics;
        internal int BufferAllocations {get;private set;}
        internal void Capture(Graphics target,Rectangle bounds,IntPtr overlay,IntPtr sourceOverride) {
            IntPtr source=sourceOverride;
            RECT rect;
            if(source==IntPtr.Zero) {
                if(desktop==IntPtr.Zero||!IsWindowVisible(desktop)||!GetWindowRect(desktop,out rect)||!rect.Intersects(bounds))desktop=FindDesktop(bounds);
                source=desktop;
            }
            if(source==IntPtr.Zero||source==overlay||IsChild(source,overlay)||!GetWindowRect(source,out rect))
                throw new InvalidOperationException("独立桌面背景窗口暂不可用，已保留最后一帧并重试；没有采集包含收纳窗口的屏幕。");
            int width=rect.Right-rect.Left,height=rect.Bottom-rect.Top;
            if(width<=0||height<=0)throw new InvalidOperationException("背景窗口大小无效。");
            if(frame==null||frame.Width!=width||frame.Height!=height) {
                Dispose();frame=new Bitmap(width,height,System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                frameGraphics=Graphics.FromImage(frame);BufferAllocations++;
            }
            IntPtr dc=frameGraphics.GetHdc();
            try {
                // Full-content rendering is empirically supported by this desktop host;
                // a successful return alone does not guarantee every wallpaper engine.
                if(!PrintWindow(source,dc,2))throw new Win32Exception(Marshal.GetLastWin32Error(),"独立桌面背景绘制失败。");
            } finally {
                frameGraphics.ReleaseHdc(dc);
            }
            // PrintWindow's redirected rendering can ignore the supplied DC viewport
            // and clip. Crop its completed source bitmap ourselves, in physical pixels.
            target.Clear(Color.Black);
            target.DrawImageUnscaled(frame,rect.Left-bounds.Left,rect.Top-bounds.Top);
        }
        public void Dispose(){if(frameGraphics!=null){frameGraphics.Dispose();frameGraphics=null;}if(frame!=null){frame.Dispose();frame=null;}}
        static IntPtr FindDesktop(Rectangle bounds) {
            IntPtr primary=IntPtr.Zero,worker=IntPtr.Zero;
            EnumWindows(delegate(IntPtr hwnd,IntPtr unused) {
                RECT rect;if(!IsWindowVisible(hwnd)||!GetWindowRect(hwnd,out rect)||!rect.Intersects(bounds))return true;
                var name=new StringBuilder(64);GetClassName(hwnd,name,name.Capacity);
                if(name.ToString()=="Progman")primary=hwnd;
                else if(name.ToString()=="WorkerW"&&worker==IntPtr.Zero)worker=hwnd;
                return true;
            },IntPtr.Zero);
            return primary!=IntPtr.Zero?primary:worker;
        }
        [StructLayout(LayoutKind.Sequential)] struct RECT {
            internal int Left,Top,Right,Bottom;
            internal bool Intersects(Rectangle bounds){return Left<bounds.Right&&Top<bounds.Bottom&&Right>bounds.Left&&Bottom>bounds.Top;}
        }
        delegate bool EnumProc(IntPtr hwnd,IntPtr unused);
        [DllImport("user32.dll")]static extern bool EnumWindows(EnumProc callback,IntPtr unused);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetClassName(IntPtr hwnd,StringBuilder text,int count);
        [DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")]static extern bool IsChild(IntPtr parent,IntPtr child);
        [DllImport("user32.dll")]static extern bool GetWindowRect(IntPtr hwnd,out RECT rect);
        [DllImport("user32.dll",SetLastError=true)]static extern bool PrintWindow(IntPtr hwnd,IntPtr dc,uint flags);
    }
}
