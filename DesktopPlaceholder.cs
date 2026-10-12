using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClearFolder {
    // A visible .lnk is the Explorer-owned grid reservation. The glass window covers it.
    internal static class DesktopPlaceholder {
        internal static void Ensure(Store store,Deck deck,string executable) {
            if(store==null||deck==null)throw new ArgumentNullException();
            if(string.IsNullOrWhiteSpace(deck.PlaceholderPath))
                deck.PlaceholderPath=Path.Combine(store.DesktopDirectory,"ClearFolder - "+deck.Id+".lnk");
            string path=Path.GetFullPath(deck.PlaceholderPath);
            string parent=Path.GetDirectoryName(path);
            if(!string.Equals(parent.TrimEnd('\\'),store.DesktopDirectory.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("桌面占位快捷方式必须位于当前用户桌面。");
            string displayPath=Path.Combine(store.DesktopDirectory,BlankName(deck.Id)+".lnk");
            if(!File.Exists(path)&&File.Exists(displayPath)) {
                if(!HasIdentity(displayPath,deck.Id))throw new IOException("空白占位名称冲突，已保留原文件。");
                path=displayPath;deck.PlaceholderPath=path;
            }
            string icon=Path.Combine(store.Root,"placeholder-transparent.ico");
            WriteTransparentIcon(icon);
            CreateShortcut(path,executable,icon,deck.Id);
            Point? prior=null;
            if(!string.Equals(path,displayPath,StringComparison.OrdinalIgnoreCase)) {
                if(File.Exists(displayPath))throw new IOException("空白占位名称已被其他文件占用，原占位保持不变。");
                prior=DesktopGrid.Read(path).PlaceholderPosition;
                File.Move(path,displayPath);
                SHChangeNotify(1,0x0005,path,displayPath);
                path=displayPath;deck.PlaceholderPath=path;
            }
            for(int attempt=0;attempt<20;attempt++) {
                var grid=DesktopGrid.Read(path);
                if(grid.PlaceholderPosition.HasValue){Point position=prior.HasValue?DesktopGrid.PositionPlaceholder(path,prior.Value):grid.PlaceholderPosition.Value;DesktopGrid.ClearPlaceholderSelection(path);deck.X=position.X;deck.Y=position.Y;return;}
                Thread.Sleep(100);
            }
            throw new InvalidOperationException("Explorer 未能确认桌面占位位置，请稍后重启 ClearFolder。");
        }
        static Point? FindNew(System.Collections.Generic.IList<Point> before,System.Collections.Generic.IList<Point> after) {
            foreach(Point candidate in after) {
                bool old=false;foreach(Point existing in before)
                    if(Math.Abs(existing.X-candidate.X)<=2&&Math.Abs(existing.Y-candidate.Y)<=2){old=true;break;}
                if(!old)return candidate;
            }
            return null;
        }
        internal static void WriteTransparentIcon(string path) {
            using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream)) {
                writer.Write((ushort)0);writer.Write((ushort)1);writer.Write((ushort)1);
                writer.Write((byte)32);writer.Write((byte)32);writer.Write((byte)0);writer.Write((byte)0);
                writer.Write((ushort)1);writer.Write((ushort)32);writer.Write(4264);writer.Write(22);
                writer.Write(40);writer.Write(32);writer.Write(64);writer.Write((ushort)1);writer.Write((ushort)32);
                writer.Write(0);writer.Write(4096);writer.Write(0);writer.Write(0);writer.Write(0);writer.Write(0);
                writer.Write(new byte[4096]);for(int i=0;i<128;i++)writer.Write((byte)255);
                writer.Flush();File.WriteAllBytes(path,stream.ToArray());
            }
        }
        [DllImport("shell32.dll",CharSet=CharSet.Unicode)] static extern void SHChangeNotify(uint change,uint flags,string oldPath,string newPath);
        internal static string BlankName(string id) {
            var name=new System.Text.StringBuilder("\u2800");
            foreach(char c in id){int value=Convert.ToInt32(c.ToString(),16);for(int bit=3;bit>=0;bit--)name.Append((value&(1<<bit))==0?'\u200b':'\u200c');}
            return name.ToString();
        }
        internal static bool HasIdentity(string path,string id) {
            object instance=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046")));
            try { ((System.Runtime.InteropServices.ComTypes.IPersistFile)instance).Load(path,0);var description=new System.Text.StringBuilder(1024);((IShellLinkW)instance).GetDescription(description,description.Capacity);return description.ToString()=="ClearFolder 桌面网格占位 "+id; }
            finally { Marshal.FinalReleaseComObject(instance); }
        }
        internal static void CreateShortcut(string path,string executable,string icon,string id) {
            if(string.IsNullOrWhiteSpace(executable)||!File.Exists(executable))throw new FileNotFoundException("找不到 ClearFolder 程序。",executable);
            object instance=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046")));
            try {
                var file=(System.Runtime.InteropServices.ComTypes.IPersistFile)instance;var link=(IShellLinkW)instance;
                if(File.Exists(path)) {
                    file.Load(path,0);var description=new System.Text.StringBuilder(1024);link.GetDescription(description,description.Capacity);
                    bool legacy=description.ToString()=="ClearFolder 桌面网格占位"&&Path.GetFileName(path)=="ClearFolder - "+id+".lnk";
                    if(description.ToString()!="ClearFolder 桌面网格占位 "+id&&!legacy)throw new IOException("同名快捷方式不属于 ClearFolder，已保留原文件。");
                }
                link.SetPath(executable);link.SetWorkingDirectory(Path.GetDirectoryName(executable));link.SetIconLocation(icon,0);link.SetDescription("ClearFolder 桌面网格占位 "+id);file.Save(path,true);
            } finally { Marshal.FinalReleaseComObject(instance); }
        }
        [ComImport,Guid("000214F9-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellLinkW {
            void GetPath([Out,MarshalAs(UnmanagedType.LPWStr)]System.Text.StringBuilder path,int count,IntPtr findData,uint flags);
            void GetIDList(out IntPtr pidl);void SetIDList(IntPtr pidl);
            void GetDescription([Out,MarshalAs(UnmanagedType.LPWStr)]System.Text.StringBuilder text,int count);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)]string text);
            void GetWorkingDirectory([Out,MarshalAs(UnmanagedType.LPWStr)]System.Text.StringBuilder text,int count);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)]string text);
            void GetArguments([Out,MarshalAs(UnmanagedType.LPWStr)]System.Text.StringBuilder text,int count);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)]string text);
            void GetHotkey(out short key);void SetHotkey(short key);void GetShowCmd(out int command);void SetShowCmd(int command);
            void GetIconLocation([Out,MarshalAs(UnmanagedType.LPWStr)]System.Text.StringBuilder path,int count,out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)]string path,int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)]string path,uint reserved);void Resolve(IntPtr hwnd,uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)]string path);
        }
    }
}
