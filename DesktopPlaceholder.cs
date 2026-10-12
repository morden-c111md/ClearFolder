using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClearFolder {
    // A visible .lnk is the Explorer-owned grid reservation. The glass window covers it.
    internal static class DesktopPlaceholder {
        internal static bool Ensure(Store store,Deck deck,string executable) {
            if(store==null||deck==null)throw new ArgumentNullException();
            if(string.IsNullOrWhiteSpace(deck.PlaceholderPath))
                deck.PlaceholderPath=Path.Combine(store.DesktopDirectory,"ClearFolder - "+deck.Id+".lnk");
            string path=Path.GetFullPath(deck.PlaceholderPath);
            string originalPath=path;
            string parent=Path.GetDirectoryName(path);
            string desktopRoot=store.DesktopDirectory;
            string icon=Path.Combine(store.Root,"placeholder-transparent.ico");
            bool currentPathSelected=false;
            if(!string.Equals(parent.TrimEnd('\\'),desktopRoot.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)) {
                // Windows can change the known-folder path while Explorer keeps the
                // existing desktop view (for example after a redirected desktop is
                // restored). Preserve a previously verified placeholder instead of
                // losing the visible entry or rewriting an unrelated .lnk.
                if(!File.Exists(path)||!HasIdentity(path,deck.Id))
                    throw new InvalidDataException("桌面占位快捷方式必须位于当前用户桌面，且旧位置身份无法验证。");
                // Prefer a new, verified placeholder in the current known
                // desktop. The old file is deliberately retained until the
                // new path has been confirmed, so a redirected desktop cannot
                // make the saved entry point at a missing shortcut.
                string currentPath=Path.Combine(desktopRoot,BlankName(deck.Id)+".lnk");
                bool currentReady=false;
                try {
                    if(!File.Exists(currentPath))CreateShortcut(currentPath,executable,icon,deck.Id);
                    else if(HasIdentity(currentPath,deck.Id)&&!HasIdentity(currentPath,deck.Id,executable,icon))CreateShortcut(currentPath,executable,icon,deck.Id);
                    currentReady=HasIdentity(currentPath,deck.Id,executable,icon);
                } catch { currentReady=false; }
                if(currentReady){path=currentPath;currentPathSelected=true;deck.PlaceholderPath=path;}
                else desktopRoot=parent;
            }
            string displayPath=Path.Combine(desktopRoot,BlankName(deck.Id)+".lnk");
            if(!File.Exists(path)&&File.Exists(displayPath)) {
                if(!HasIdentity(displayPath,deck.Id,executable,icon))throw new IOException("空白占位名称冲突，已保留原文件。");
                path=displayPath;
            }
            WriteTransparentIcon(icon);
            // A verified shortcut may live in a redirected/read-only desktop view.
            // Do not rewrite it just to refresh identical metadata: IPersistFile.Save
            // can be denied even though Explorer can still display and hit the item.
            if(!File.Exists(path)||!HasIdentity(path,deck.Id,currentPathSelected?executable:null,currentPathSelected?icon:null))CreateShortcut(path,executable,icon,deck.Id);
            Point? prior=null;
            bool moved=false;
            if(!string.Equals(path,displayPath,StringComparison.OrdinalIgnoreCase)) {
                if(File.Exists(displayPath))throw new IOException("空白占位名称已被其他文件占用，原占位保持不变。");
                prior=DesktopGrid.Read(path).PlaceholderPosition;
                // Do not move an old shortcut unless Explorer has confirmed its
                // current grid item. This prevents an unconfirmed migration from
                // leaving the saved entry pointing at a missing file.
                if(!prior.HasValue)return false;
                File.Move(path,displayPath);
                moved=true;
                SHChangeNotify(1,0x0005,path,displayPath);
                path=displayPath;
            }
            try {
                for(int attempt=0;attempt<20;attempt++) {
                    var grid=DesktopGrid.Read(path);
                    if(grid.PlaceholderPosition.HasValue){Point position=prior.HasValue?DesktopGrid.PositionPlaceholder(path,prior.Value):grid.PlaceholderPosition.Value;DesktopGrid.ClearPlaceholderSelection(path);deck.PlaceholderPath=path;deck.X=position.X;deck.Y=position.Y;return true;}
                    Thread.Sleep(100);
                }
            } catch {
                if(moved&&!RollbackMove(path,originalPath)) {
                    deck.PlaceholderPath=path;
                    throw new IOException("桌面占位快捷方式迁移失败且无法回滚，已保留实际文件路径："+path);
                }
                throw;
            }
            if(moved&&!RollbackMove(path,originalPath)) {
                deck.PlaceholderPath=path;
                throw new IOException("桌面占位快捷方式迁移失败且无法回滚，已保留实际文件路径："+path);
            }
            return false;
        }
        static bool RollbackMove(string current,string original) {
            if(!File.Exists(current))return false;
            if(File.Exists(original))return false;
            try { File.Move(current,original);SHChangeNotify(1,0x0005,current,original);return true; } catch { return false; }
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
        internal static bool HasIdentity(string path,string id,string executable=null,string icon=null) {
            object instance=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046")));
            try { ((System.Runtime.InteropServices.ComTypes.IPersistFile)instance).Load(path,0);var link=(IShellLinkW)instance;var description=new System.Text.StringBuilder(1024);link.GetDescription(description,description.Capacity);string file=Path.GetFileName(path);bool legalName=string.Equals(file,BlankName(id)+".lnk",StringComparison.OrdinalIgnoreCase)||string.Equals(file,"ClearFolder - "+id+".lnk",StringComparison.OrdinalIgnoreCase);if(!legalName)return false;bool modern=description.ToString()=="ClearFolder 桌面网格占位 "+id;bool legacy=description.ToString()=="ClearFolder 桌面网格占位"&&file=="ClearFolder - "+id+".lnk";if(!modern&&!legacy)return false;if(!string.IsNullOrWhiteSpace(executable)){var target=new System.Text.StringBuilder(32768);link.GetPath(target,target.Capacity,IntPtr.Zero,0);if(!string.Equals(Normalize(target.ToString()),Normalize(executable),StringComparison.OrdinalIgnoreCase))return false;}if(!string.IsNullOrWhiteSpace(icon)){var iconPath=new System.Text.StringBuilder(32768);int index;link.GetIconLocation(iconPath,iconPath.Capacity,out index);if(!string.Equals(Normalize(iconPath.ToString()),Normalize(icon),StringComparison.OrdinalIgnoreCase))return false;}return true; }
            finally { Marshal.FinalReleaseComObject(instance); }
        }
        static string Normalize(string path) { return string.IsNullOrWhiteSpace(path)?string.Empty:Path.GetFullPath(path).TrimEnd('\\'); }
        internal static void CreateShortcut(string path,string executable,string icon,string id) {
            if(string.IsNullOrWhiteSpace(executable)||!File.Exists(executable))throw new FileNotFoundException("找不到 ClearFolder 程序。",executable);
            string fileName=Path.GetFileName(path);
            if(!string.Equals(fileName,BlankName(id)+".lnk",StringComparison.OrdinalIgnoreCase)&&!string.Equals(fileName,"ClearFolder - "+id+".lnk",StringComparison.OrdinalIgnoreCase))throw new IOException("桌面占位快捷方式名称不属于当前收纳文件夹，已保留原文件。");
            object instance=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046")));
            try {
                var file=(System.Runtime.InteropServices.ComTypes.IPersistFile)instance;var link=(IShellLinkW)instance;
                if(File.Exists(path)) {
                    file.Load(path,0);var description=new System.Text.StringBuilder(1024);link.GetDescription(description,description.Capacity);
                    bool legacy=description.ToString()=="ClearFolder 桌面网格占位"&&fileName=="ClearFolder - "+id+".lnk";
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
