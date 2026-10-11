using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ClearFolder {
    // Coordinates are screen pixels and refer to the visible icon's upper-left corner.
    // This is an overlay lattice only; no Explorer item is created or repositioned.
    public sealed class DesktopGrid {
        public string Source { get; private set; }
        public Size Cell { get; private set; }
        public Point Origin { get; private set; }
        public int IconSize { get; private set; }
        public IList<Point> Occupied { get; private set; }
        public DesktopGrid(Size cell,Point origin,int iconSize,IEnumerable<Point> occupied,string source) {
            if(cell.Width<1||cell.Height<1||iconSize<1)throw new ArgumentOutOfRangeException("cell");
            Cell=cell;Origin=origin;IconSize=iconSize;Occupied=new List<Point>(occupied??new Point[0]).AsReadOnly();Source=source;
        }
        public Point Snap(Point desired,Rectangle work,IEnumerable<Point> reserved) {
            var busy=new HashSet<long>();
            foreach(Point p in Occupied)busy.Add(Key(p));
            if(reserved!=null)foreach(Point p in reserved)busy.Add(Key(p));
            int firstX=(int)Math.Ceiling((work.Left-Origin.X)/(double)Cell.Width);
            int lastX=(int)Math.Floor((work.Right-IconSize-Origin.X)/(double)Cell.Width);
            int firstY=(int)Math.Ceiling((work.Top-Origin.Y)/(double)Cell.Height);
            int lastY=(int)Math.Floor((work.Bottom-Math.Max(IconSize+26,Cell.Height-4)-Origin.Y)/(double)Cell.Height);
            Point best=Point.Empty;double distance=double.MaxValue;bool found=false;
            for(int x=firstX;x<=lastX;x++)for(int y=firstY;y<=lastY;y++) {
                var p=new Point(Origin.X+x*Cell.Width,Origin.Y+y*Cell.Height);
                if(busy.Contains(Key(p)))continue;
                double dx=(double)p.X-desired.X,dy=(double)p.Y-desired.Y,d=dx*dx+dy*dy;
                if(d<distance){distance=d;best=p;found=true;}
            }
            if(!found)throw new InvalidOperationException("此屏幕没有空闲的桌面图标格子，请换到其他屏幕或腾出一个位置。");
            return best;
        }
        long Key(Point p) {
            int x=(int)Math.Round((p.X-Origin.X)/(double)Cell.Width),y=(int)Math.Round((p.Y-Origin.Y)/(double)Cell.Height);
            return ((long)x<<32)|(uint)y;
        }
        public static DesktopGrid Read() {
            try{return ReadShell();}
            catch(Exception ex) {
                if(ex is OutOfMemoryException||ex is AccessViolationException)throw;
                var area=Screen.PrimaryScreen.WorkingArea;int icon=GetSystemMetrics(11);
                int w=Math.Max(icon+28,GetSystemMetrics(38)),h=Math.Max(icon+42,GetSystemMetrics(39));
                return new DesktopGrid(new Size(w,h),new Point(area.Left+(w-icon)/2,area.Top+4),icon,new Point[0],"System metrics fallback: "+ex.GetType().Name);
            }
        }
        static DesktopGrid ReadShell() {
            object windows=null,desktop=null;IntPtr browser=IntPtr.Zero,view=IntPtr.Zero,folder=IntPtr.Zero,folder2=IntPtr.Zero;
            try {
                windows=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")));
                object[] args={0,null,8,0,1};
                desktop=windows.GetType().InvokeMember("FindWindowSW",BindingFlags.InvokeMethod,null,windows,args);
                if(desktop==null)throw new InvalidOperationException("Desktop Shell view unavailable");
                var service=(IServiceProvider)desktop;Guid sid=new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837"),iid=new Guid("000214E2-0000-0000-C000-000000000046");
                Check(service.QueryService(ref sid,ref iid,out browser));
                Check(Call<GetInterface>(browser,15)(browser,out view));
                iid=new Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE");Check(Marshal.QueryInterface(view,ref iid,out folder));
                P spacing=new P();Check(Call<GetPoint>(folder,12)(folder,ref spacing));
                if(spacing.X<16||spacing.Y<16)throw new InvalidOperationException("Invalid desktop spacing");
                int icon=GetSystemMetrics(11),mode,logicalIcon=0;
                iid=new Guid("1AF3A467-214F-4298-908E-06B03E0B39F9");
                if(Marshal.QueryInterface(view,ref iid,out folder2)>=0){Check(Call<GetModeSize>(folder2,36)(folder2,out mode,out logicalIcon));}
                IntPtr hwnd;Check(Call<GetWindow>(view,3)(view,out hwnd));P offset=new P();
                uint dpi=96;
                try{dpi=GetDpiForWindow(hwnd);}catch(EntryPointNotFoundException){using(var g=Graphics.FromHwnd(hwnd))dpi=(uint)Math.Round(g.DpiX);}
                if(dpi==0)using(var g=Graphics.FromHwnd(hwnd))dpi=(uint)Math.Round(g.DpiX);
                if(logicalIcon>0)icon=ScaleIconSize(logicalIcon,dpi);
                if(!ClientToScreen(hwnd,ref offset))throw new InvalidOperationException("Desktop client coordinates unavailable");
                int count;Check(Call<GetCount>(folder,7)(folder,2,out count));var points=new List<Point>();
                // Folder view positions are icon origins in icon view, in the view's client coordinates.
                for(int n=0;n<Math.Min(count,20000);n++) {
                    IntPtr pidl=IntPtr.Zero;
                    try {Check(Call<GetItem>(folder,6)(folder,n,out pidl));P p=new P();Check(Call<GetPosition>(folder,11)(folder,pidl,out p));points.Add(new Point(p.X+offset.X,p.Y+offset.Y));}
                    finally {if(pidl!=IntPtr.Zero)Marshal.FreeCoTaskMem(pidl);}
                }
                Point origin=points.Count>0?points[0]:new Point(offset.X+(spacing.X-icon)/2,offset.Y+4);
                return new DesktopGrid(new Size(spacing.X,spacing.Y),origin,Math.Max(16,Math.Min(1024,icon)),points,"Explorer IFolderView"+(folder2!=IntPtr.Zero?"2":"")+"; logicalIcon="+logicalIcon+"; viewDpi="+dpi);
            } finally {
                if(folder2!=IntPtr.Zero)Marshal.Release(folder2);if(folder!=IntPtr.Zero)Marshal.Release(folder);if(view!=IntPtr.Zero)Marshal.Release(view);if(browser!=IntPtr.Zero)Marshal.Release(browser);
                if(desktop!=null&&Marshal.IsComObject(desktop))Marshal.FinalReleaseComObject(desktop);
                if(windows!=null&&Marshal.IsComObject(windows))Marshal.FinalReleaseComObject(windows);
            }
        }
        static void Check(int hr){Marshal.ThrowExceptionForHR(hr);}
        internal static int ScaleIconSize(int logical,uint dpi){if(logical<1||dpi<1)throw new ArgumentOutOfRangeException("logical");return checked((int)Math.Round(logical*(dpi/96.0),MidpointRounding.AwayFromZero));}
        static T Call<T>(IntPtr obj,int slot) where T:class {return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj),slot*IntPtr.Size),typeof(T)) as T;}
        [StructLayout(LayoutKind.Sequential)] struct P {public int X,Y;}
        [ComImport,Guid("6D5140C1-7436-11CE-8034-00AA006009FA"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IServiceProvider {[PreserveSig]int QueryService(ref Guid service,ref Guid iid,out IntPtr result);}
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetInterface(IntPtr self,out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetWindow(IntPtr self,out IntPtr hwnd);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetPoint(IntPtr self,ref P p);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetCount(IntPtr self,uint flags,out int count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetItem(IntPtr self,int index,out IntPtr pidl);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetPosition(IntPtr self,IntPtr pidl,out P point);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetModeSize(IntPtr self,out int mode,out int size);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd,ref P p);
    }
}
