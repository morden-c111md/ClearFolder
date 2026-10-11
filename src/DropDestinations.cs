using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace ClearFolder {
    internal sealed class DropDestinationResult {
        internal string Directory {get;private set;}
        internal string Description {get;private set;}
        internal bool Allowed {get;private set;}
        internal static DropDestinationResult Accept(string directory,string description) {
            return new DropDestinationResult {Directory=directory,Description=description,Allowed=true};
        }
        internal static DropDestinationResult Reject(string description) {
            return new DropDestinationResult {Description=description,Allowed=false};
        }
    }
    // This resolver only observes the window beneath the release point. It never navigates
    // Explorer, launches shortcuts, creates files, or enumerates the contents of a folder.
    internal static class DropDestinations {
        sealed class Hit {
            internal string ItemName;
            internal bool InItemsView,Interactive,DesktopView;
        }
        internal static DropDestinationResult Resolve(Point screen,IntPtr sourceHandle) {
            try {
                IntPtr hitWindow=Native.WindowFromPoint(new Native.POINT(screen.X,screen.Y));
                if(hitWindow==IntPtr.Zero)return DropDestinationResult.Reject("未识别到接收位置。");
                IntPtr root=Native.GetAncestor(hitWindow,2);
                if(root==IntPtr.Zero)root=hitWindow;
                if(root==sourceHandle||hitWindow==sourceHandle)return DropDestinationResult.Reject("项目仍在原面板内。");
                string name=Native.ClassName(root);
                bool desktop=Native.IsDesktop(hitWindow);
                if(!desktop&&name!="CabinetWClass"&&name!="ExploreWClass")return DropDestinationResult.Reject("请拖到桌面、资源管理器文件区域或真实文件夹上。");
                Hit hit=ReadHit(screen,root,desktop);
                if(hit.Interactive)return DropDestinationResult.Reject("导航栏、按钮、标题栏和虚拟位置不接收收纳项目。");
                if(desktop) {
                    if(hit.ItemName!=null) {
                        if(!hit.DesktopView)return DropDestinationResult.Reject("无法确认桌面图标位置。");
                        string directory=ResolveDesktopName(hit.ItemName,
                            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
                        return directory==null?DropDestinationResult.Reject("此桌面图标不是可接收文件的真实文件夹。"):
                            DropDestinationResult.Accept(directory,"桌面文件夹："+hit.ItemName);
                    }
                    string desktopDirectory=NormalizeDirectory(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
                    return desktopDirectory==null?DropDestinationResult.Reject("当前用户桌面目录不可用。"):
                        DropDestinationResult.Accept(desktopDirectory,"桌面");
                }
                if(!hit.InItemsView)return DropDestinationResult.Reject("请放到资源管理器的文件列表区域。");
                return ResolveExplorer(root,hit.ItemName);
            }catch(Exception ex) {
                if(ex is OutOfMemoryException||ex is AccessViolationException)throw;
                return DropDestinationResult.Reject("无法可靠识别目标位置，项目保留在面板中。（"+ex.GetType().Name+"）");
            }
        }
        static Hit ReadHit(Point screen,IntPtr root,bool desktop) {
            var hit=new Hit();
            AutomationElement node=AutomationElement.FromPoint(new System.Windows.Point(screen.X,screen.Y));
            if(node==null)throw new InvalidOperationException("目标窗口未提供辅助功能信息。");
            bool rootSeen=false;
            for(int depth=0;node!=null&&depth<48;depth++) {
                var current=node.Current;ControlType type=current.ControlType;
                string className=current.ClassName??"",id=current.AutomationId??"";
                if(!hit.InItemsView) {
                    if(type==ControlType.ListItem||type==ControlType.DataItem) {
                        if(hit.ItemName==null)hit.ItemName=current.Name??"";
                    }else if(type==ControlType.Button||type==ControlType.Hyperlink||type==ControlType.TreeItem||
                        type==ControlType.MenuItem||type==ControlType.TabItem||type==ControlType.ScrollBar||
                        type==ControlType.Edit||type==ControlType.HeaderItem||type==ControlType.TitleBar)hit.Interactive=true;
                }
                bool items=className=="UIItemsView"||id=="ItemsView"||className=="SysListView32";
                if(items)hit.InItemsView=true;
                if(desktop&&(className=="SysListView32"||className=="SHELLDLL_DefView"))hit.DesktopView=true;
                if(current.NativeWindowHandle!=0&&new IntPtr(current.NativeWindowHandle)==root){rootSeen=true;break;}
                node=TreeWalker.RawViewWalker.GetParent(node);
            }
            if(!rootSeen)throw new InvalidOperationException("辅助功能命中与目标窗口不一致。");
            return hit;
        }
        internal static string NormalizeDirectory(string value) {
            if(string.IsNullOrWhiteSpace(value)||value.StartsWith("::",StringComparison.Ordinal)||!Path.IsPathRooted(value))return null;
            if(value.StartsWith(@"\\.\",StringComparison.Ordinal)||value.StartsWith(@"\\?\",StringComparison.Ordinal))return null;
            try {
                string root=Path.GetPathRoot(value);
                if(string.IsNullOrEmpty(root)||root==@"\"||root.Length==2&&root[1]==':')return null;
                string full=Path.GetFullPath(value);return System.IO.Directory.Exists(full)?full:null;
            }
            catch(ArgumentException){return null;}catch(NotSupportedException){return null;}catch(PathTooLongException){return null;}
        }
        internal static string ResolveDesktopName(string name,string userDesktop,string publicDesktop) {
            if(string.IsNullOrWhiteSpace(name)||name=="."||name==".."||name.IndexOfAny(Path.GetInvalidFileNameChars())>=0)return null;
            foreach(string virtualName in new[]{"回收站","Recycle Bin","此电脑","This PC","计算机","Computer","网络","Network","控制面板","Control Panel"})
                if(string.Equals(name,virtualName,StringComparison.OrdinalIgnoreCase))return null;
            string result=null;
            foreach(string desktop in new[]{userDesktop,publicDesktop}) {
                if(string.IsNullOrEmpty(desktop))continue;
                string folder=NormalizeDirectory(Path.Combine(desktop,name));
                if(folder==null)continue;
                if(result!=null&&!string.Equals(result,folder,StringComparison.OrdinalIgnoreCase))return null;
                result=folder;
            }
            return result;
        }
        static DropDestinationResult ResolveExplorer(IntPtr target,string itemName) {
            object shell=null,windows=null;var candidates=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var viewPaths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool uncertain=false;
            try {
                Type type=Type.GetTypeFromProgID("Shell.Application");if(type==null)return DropDestinationResult.Reject("Windows Shell 服务不可用。");
                shell=Activator.CreateInstance(type);windows=Get(shell,"Windows",null);
                int count=Convert.ToInt32(Get(windows,"Count",null),CultureInfo.InvariantCulture);
                for(int index=0;index<Math.Min(count,256);index++) {
                    object window=null,document=null,folder=null,self=null,item=null;
                    bool matchedWindow=false;
                    try {
                        window=Get(windows,"Item",new object[]{index});if(window==null)continue;
                        long hwnd=Convert.ToInt64(Get(window,"HWND",null),CultureInfo.InvariantCulture);
                        if(hwnd!=target.ToInt64()&&unchecked((uint)hwnd)!=unchecked((uint)target.ToInt64()))continue;
                        matchedWindow=true;
                        document=Get(window,"Document",null);folder=Get(document,"Folder",null);self=Get(folder,"Self",null);
                        string rawDirectory=Convert.ToString(Get(self,"Path",null),CultureInfo.InvariantCulture);
                        viewPaths.Add(rawDirectory??"");
                        string currentDirectory=NormalizeDirectory(rawDirectory);
                        if(currentDirectory==null)continue;
                        if(itemName==null){candidates.Add(currentDirectory);continue;}
                        if(string.IsNullOrWhiteSpace(itemName))continue;
                        item=Get(folder,"ParseName",new object[]{itemName});if(item==null)continue;
                        if(!Convert.ToBoolean(Get(item,"IsFolder",null),CultureInfo.InvariantCulture))continue;
                        string directory=NormalizeDirectory(Convert.ToString(Get(item,"Path",null),CultureInfo.InvariantCulture));
                        if(directory!=null)candidates.Add(directory);
                    }catch(COMException){if(matchedWindow)uncertain=true;}catch(TargetInvocationException){if(matchedWindow)uncertain=true;}
                    finally{Release(item);Release(self);Release(folder);Release(document);Release(window);}
                }
                if(uncertain)return DropDestinationResult.Reject("资源管理器目标信息暂不可用，项目保留在面板中。");
                if(viewPaths.Count>1)return DropDestinationResult.Reject("多个资源管理器标签页的目标不明确，请使用独立窗口重试。");
                if(candidates.Count==0)return DropDestinationResult.Reject("此位置不是可确认的真实文件夹；请放到文件列表空白处或真实文件夹上。");
                if(candidates.Count!=1)return DropDestinationResult.Reject("目标文件夹不明确，项目保留在面板中。");
                foreach(string candidate in candidates)return DropDestinationResult.Accept(candidate,itemName==null?"当前文件夹":"文件夹："+itemName);
                return DropDestinationResult.Reject("没有可用的目标文件夹。");
            }finally{Release(windows);Release(shell);}
        }
        static object Get(object target,string name,object[] arguments) {
            if(target==null)return null;
            return target.GetType().InvokeMember(name,BindingFlags.GetProperty|BindingFlags.InvokeMethod|BindingFlags.IgnoreCase,null,target,arguments,CultureInfo.InvariantCulture);
        }
        static void Release(object value){if(value!=null&&Marshal.IsComObject(value))Marshal.FinalReleaseComObject(value);}
    }
}
