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
            if(!File.Exists(path)) {
                var before=DesktopGrid.Read().Occupied;
                CreateShortcut(path,executable);
                Thread.Sleep(350);
                var after=DesktopGrid.Read().Occupied;
                Point? created=FindNew(before,after);
                if(created.HasValue){deck.X=created.Value.X;deck.Y=created.Value.Y;}
            }
        }
        static Point? FindNew(System.Collections.Generic.IList<Point> before,System.Collections.Generic.IList<Point> after) {
            foreach(Point candidate in after) {
                bool old=false;foreach(Point existing in before)
                    if(Math.Abs(existing.X-candidate.X)<=2&&Math.Abs(existing.Y-candidate.Y)<=2){old=true;break;}
                if(!old)return candidate;
            }
            return null;
        }
        static void CreateShortcut(string path,string executable) {
            if(string.IsNullOrWhiteSpace(executable)||!File.Exists(executable))
                throw new FileNotFoundException("找不到 ClearFolder 程序，无法创建桌面占位快捷方式。",executable);
            Type shellType=Type.GetTypeFromProgID("WScript.Shell");
            if(shellType==null)throw new PlatformNotSupportedException("Windows Script Host 不可用。");
            object shell=Activator.CreateInstance(shellType),shortcut=null;
            try {
                shortcut=shellType.InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[]{path});
                Type type=shortcut.GetType();
                Set(type,shortcut,"TargetPath",executable);
                Set(type,shortcut,"WorkingDirectory",Path.GetDirectoryName(executable));
                Set(type,shortcut,"IconLocation",executable+",0");
                Set(type,shortcut,"Description","ClearFolder 桌面网格占位");
                type.InvokeMember("Save",BindingFlags.InvokeMethod,null,shortcut,null);
            } finally {
                if(shortcut!=null&&Marshal.IsComObject(shortcut))Marshal.FinalReleaseComObject(shortcut);
                if(shell!=null&&Marshal.IsComObject(shell))Marshal.FinalReleaseComObject(shell);
            }
        }
        static void Set(Type type,object target,string name,object value) {
            type.InvokeMember(name,BindingFlags.SetProperty,null,target,new object[]{value});
        }
    }
}
