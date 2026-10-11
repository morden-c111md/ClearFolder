using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace ClearFolder {
    internal static class Native {
        [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; public POINT(int x,int y){X=x;Y=y;} }
        [StructLayout(LayoutKind.Sequential)] internal struct SIZE { public int X,Y; public SIZE(int x,int y){X=x;Y=y;} }
        [StructLayout(LayoutKind.Sequential,Pack=1)] internal struct BLEND { public byte Op,Flags,Alpha,Format; }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] internal struct SHFILEINFO {
            public IntPtr Icon; public int Index; public uint Attributes;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string Display;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=80)] public string Type;
        }
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
        [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] internal static extern IntPtr GetWindowLongPtr(IntPtr hwnd,int index);
        [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] internal static extern IntPtr SetWindowLongPtr(IntPtr hwnd,int index,IntPtr value);
        [DllImport("user32.dll",SetLastError=true)] internal static extern bool SetWindowDisplayAffinity(IntPtr hwnd,uint affinity);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr hwnd,uint command);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hwnd,int command);
        internal static bool AboveDesktop(IntPtr hwnd,IntPtr desktop){IntPtr cursor=GetWindow(desktop,3);for(int i=0;i<512&&cursor!=IntPtr.Zero;i++,cursor=GetWindow(cursor,3))if(cursor==hwnd)return true;return false;}
        delegate bool EnumWindowProc(IntPtr hwnd,IntPtr param);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowProc callback,IntPtr param);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent,IntPtr after,string className,string title);
        internal static IntPtr DesktopRoot(){IntPtr found=IntPtr.Zero;EnumWindows(delegate(IntPtr hwnd,IntPtr unused){if(FindWindowEx(hwnd,IntPtr.Zero,"SHELLDLL_DefView",null)!=IntPtr.Zero){found=hwnd;return false;}return true;},IntPtr.Zero);return found;}
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd,uint flags);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd,StringBuilder text,int count);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd,IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
        [DllImport("user32.dll",SetLastError=true)] static extern bool UpdateLayeredWindow(IntPtr hwnd,IntPtr dst,ref POINT pos,ref SIZE size,IntPtr src,ref POINT origin,uint key,ref BLEND blend,uint flags);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int cx,int cy,uint flags);
        [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr icon);
        [DllImport("shell32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SHGetFileInfo(string path,uint attr,ref SHFILEINFO info,uint size,uint flags);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x,int y);
        [DllImport("user32.dll")] internal static extern void mouse_event(uint flags,uint dx,uint dy,uint data,UIntPtr extra);
        [DllImport("user32.dll")] internal static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra);

        internal static string ClassName(IntPtr handle) {
            var text=new StringBuilder(256); GetClassName(handle,text,text.Capacity); return text.ToString();
        }
        internal static bool IsDesktop(IntPtr handle) {
            string name=ClassName(handle);
            if(name=="Progman"||name=="WorkerW"||name=="SHELLDLL_DefView") return true;
            IntPtr root=GetAncestor(handle,2); name=ClassName(root);
            return name=="Progman"||name=="WorkerW";
        }
        internal static void Submit(IntPtr handle,Bitmap bitmap,Point position,byte opacity) {
            IntPtr screen=GetDC(IntPtr.Zero),memory=IntPtr.Zero,hBitmap=IntPtr.Zero,old=IntPtr.Zero;
            try {
                memory=CreateCompatibleDC(screen);
                hBitmap=bitmap.GetHbitmap(Color.FromArgb(0)); old=SelectObject(memory,hBitmap);
                var p=new POINT(position.X,position.Y);var size=new SIZE(bitmap.Width,bitmap.Height);var origin=new POINT(0,0);
                var blend=new BLEND { Op=0,Flags=0,Alpha=opacity,Format=1 };
                if(!UpdateLayeredWindow(handle,screen,ref p,ref size,memory,ref origin,0,ref blend,2))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),"透明窗口提交失败");
            } finally {
                if(old!=IntPtr.Zero)SelectObject(memory,old);
                if(hBitmap!=IntPtr.Zero)DeleteObject(hBitmap);
                if(memory!=IntPtr.Zero)DeleteDC(memory);
                if(screen!=IntPtr.Zero)ReleaseDC(IntPtr.Zero,screen);
            }
        }
        internal static Bitmap FileIcon(string path) {
            var info=new SHFILEINFO();
            IntPtr result=SHGetFileInfo(path,0,ref info,(uint)Marshal.SizeOf(typeof(SHFILEINFO)),0x100);
            if(result==IntPtr.Zero||info.Icon==IntPtr.Zero) return SystemIcons.Application.ToBitmap();
            try { using(var icon=(Icon)Icon.FromHandle(info.Icon).Clone()) return icon.ToBitmap(); }
            finally { DestroyIcon(info.Icon); }
        }
    }
}
