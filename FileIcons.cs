using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ClearFolder {
    internal static class FileIcons {
        // Returns an owned bitmap. Shortcut contents are read only, never executed.
        internal static Bitmap Load(string path) {
            try {
                string ext=Path.GetExtension(path);
                if(ext.Equals(".url",StringComparison.OrdinalIgnoreCase)) {
                    string file;int index;
                    if(ReadInternetIcon(path,out file,out index)){Bitmap b=TryExtract(file,index);if(b!=null)return b;}
                }
                if(ext.Equals(".lnk",StringComparison.OrdinalIgnoreCase)) {
                    Bitmap b=Thread.CurrentThread.GetApartmentState()==ApartmentState.STA?TryShortcutIcon(path):LoadShortcutSta(path);
                    if(b!=null)return b;
                }
                Bitmap shell=TryShell(path,false);if(shell!=null)return shell;
                shell=TryShell(path,true);if(shell!=null)return shell;
            }catch(Exception ex){if(ex is OutOfMemoryException||ex is AccessViolationException)throw;}
            return SystemIcons.Application.ToBitmap();
        }
        internal static bool ReadInternetIcon(string path,out string file,out int index) {
            file=null;index=0;
            if(!File.Exists(path)||new FileInfo(path).Length>1024*1024)return false;
            string raw=null;bool section=false;
            string text;
            try{using(var reader=new StreamReader(path,new UTF8Encoding(false,true),true))text=reader.ReadToEnd();}
            catch(DecoderFallbackException){using(var reader=new StreamReader(path,Encoding.Default,true))text=reader.ReadToEnd();}
            using(var reader=new StringReader(text)) {
                string line;
                while((line=reader.ReadLine())!=null) {
                    line=line.Trim();if(line.StartsWith("[")){section=line.Equals("[InternetShortcut]",StringComparison.OrdinalIgnoreCase);continue;}
                    if(!section||line.StartsWith(";")||line.StartsWith("#"))continue;
                    int split=line.IndexOf('=');if(split<0)continue;
                    string key=line.Substring(0,split).Trim(),value=line.Substring(split+1).Trim();
                    if(key.Equals("IconFile",StringComparison.OrdinalIgnoreCase))raw=value;
                    else if(key.Equals("IconIndex",StringComparison.OrdinalIgnoreCase))int.TryParse(value,out index);
                }
            }
            file=Resolve(raw,Path.GetDirectoryName(Path.GetFullPath(path)));return !string.IsNullOrEmpty(file)&&File.Exists(file);
        }
        static Bitmap LoadShortcutSta(string path) {
            object gate=new object();Bitmap result=null;bool abandoned=false;
            var thread=new Thread(delegate(){Bitmap loaded=null;try{loaded=ShortcutIcon(path);}catch(Exception ex){if(ex is OutOfMemoryException||ex is AccessViolationException)throw;}
                lock(gate){if(abandoned){if(loaded!=null)loaded.Dispose();}else result=loaded;}});
            thread.IsBackground=true;thread.SetApartmentState(ApartmentState.STA);thread.Start();
            if(thread.Join(2500)){lock(gate)return result;}
            lock(gate){abandoned=true;if(result!=null){result.Dispose();result=null;}}return null;
        }
        static Bitmap ShortcutIcon(string path) {
            object shell=null,link=null;
            try {
                shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                link=shell.GetType().InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[]{path});
                string location=Convert.ToString(link.GetType().InvokeMember("IconLocation",BindingFlags.GetProperty,null,link,null));
                string target=Convert.ToString(link.GetType().InvokeMember("TargetPath",BindingFlags.GetProperty,null,link,null));
                string file=location;int index=0;int comma=location.LastIndexOf(',');
                if(comma>=0){int parsed;if(int.TryParse(location.Substring(comma+1).Trim(),out parsed)){file=location.Substring(0,comma);index=parsed;}}
                file=Resolve(file,Path.GetDirectoryName(Path.GetFullPath(path)));
                Bitmap b=TryExtract(file,index);if(b!=null)return b;
                target=Resolve(target,Path.GetDirectoryName(Path.GetFullPath(path)));if(!string.IsNullOrEmpty(target)){b=TryExtract(target,0);if(b!=null)return b;b=TryShell(target,false);if(b!=null)return b;}
                return null;
            }finally {if(link!=null&&Marshal.IsComObject(link))Marshal.FinalReleaseComObject(link);if(shell!=null&&Marshal.IsComObject(shell))Marshal.FinalReleaseComObject(shell);}
        }
        static Bitmap TryShortcutIcon(string path){try{return ShortcutIcon(path);}catch(Exception ex){if(ex is OutOfMemoryException||ex is AccessViolationException)throw;return null;}}
        static string Resolve(string value,string directory) {
            if(string.IsNullOrWhiteSpace(value))return null;value=Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            if(value.StartsWith("@"))value=value.Substring(1);
            try{return Path.GetFullPath(Path.IsPathRooted(value)?value:Path.Combine(directory,value));}
            catch(ArgumentException){return null;}catch(NotSupportedException){return null;}catch(IOException){return null;}
        }
        static Bitmap TryExtract(string file,int index){try{return Extract(file,index);}catch(Exception ex){if(ex is OutOfMemoryException||ex is AccessViolationException)throw;return null;}}
        static Bitmap TryShell(string file,bool attributesOnly){try{return Shell(file,attributesOnly);}catch(Exception ex){if(ex is OutOfMemoryException||ex is AccessViolationException)throw;return null;}}
        static Bitmap Extract(string file,int index) {
            if(string.IsNullOrEmpty(file)||!File.Exists(file))return null;
            string extension=Path.GetExtension(file);
            // Retain the largest native ICO image so high-DPI/WPF scaling does not enlarge
            // the old 48px frame. Do not manufacture pixels by upscaling small-only icons.
            if(extension.Equals(".ico",StringComparison.OrdinalIgnoreCase))return ReadIconBitmap(file,256);
            if(extension.Equals(".png",StringComparison.OrdinalIgnoreCase)) {
                if(new FileInfo(file).Length>16*1024*1024)return null;
                using(var stream=File.OpenRead(file))return DecodePng(stream,0,0);
            }
            IntPtr[] large={IntPtr.Zero},small={IntPtr.Zero};
            try {
                uint count=ExtractIconEx(file,index,large,small,1);IntPtr handle=large[0]!=IntPtr.Zero?large[0]:small[0];
                if(count==0||handle==IntPtr.Zero)return null;
                using(var icon=(Icon)Icon.FromHandle(handle).Clone())return icon.ToBitmap();
            }finally {if(large[0]!=IntPtr.Zero)DestroyIcon(large[0]);if(small[0]!=IntPtr.Zero&&small[0]!=large[0])DestroyIcon(small[0]);}
        }
        internal static Bitmap ReadIconBitmap(string file,int desiredSize) {
            if(desiredSize<1||desiredSize>512)throw new ArgumentOutOfRangeException("desiredSize");
            if(!File.Exists(file)||new FileInfo(file).Length>16*1024*1024)return null;
            byte[] bytes=File.ReadAllBytes(file);if(bytes.Length<6||BitConverter.ToUInt16(bytes,0)!=0||BitConverter.ToUInt16(bytes,2)!=1)return null;
            int count=BitConverter.ToUInt16(bytes,4);if(count<1||count>256||6L+count*16L>bytes.Length)return null;
            var frames=new List<IconFrame>();
            for(int i=0;i<count;i++) {
                int entry=6+i*16,width=bytes[entry]==0?256:bytes[entry],height=bytes[entry+1]==0?256:bytes[entry+1];
                uint length=BitConverter.ToUInt32(bytes,entry+8),offset=BitConverter.ToUInt32(bytes,entry+12);
                if(length==0||offset<6+count*16||(long)offset+length>bytes.Length)continue;
                frames.Add(new IconFrame{Entry=entry,Width=width,Height=height,Offset=(int)offset,Length=(int)length});
            }
            // Prefer an exact image, then a larger image for downsampling, then smaller images.
            frames.Sort(delegate(IconFrame a,IconFrame b){return FrameScore(a,desiredSize).CompareTo(FrameScore(b,desiredSize));});
            foreach(IconFrame frame in frames) {
                try {
                    if(frame.Length>=8&&bytes[frame.Offset]==137&&bytes[frame.Offset+1]==80&&bytes[frame.Offset+2]==78&&bytes[frame.Offset+3]==71) {
                        using(var stream=new MemoryStream(bytes,frame.Offset,frame.Length,false)) {
                            Bitmap decoded=DecodePng(stream,frame.Width,frame.Height);if(decoded!=null)return decoded;
                        }
                    }else {
                        if(!ValidDib(bytes,frame))continue;
                        using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream)) {
                            writer.Write((ushort)0);writer.Write((ushort)1);writer.Write((ushort)1);writer.Write(bytes,frame.Entry,12);writer.Write(22);writer.Write(bytes,frame.Offset,frame.Length);writer.Flush();stream.Position=0;
                            using(var icon=new Icon(stream)) {Bitmap bitmap=icon.ToBitmap();if(HasVisiblePixel(bitmap))return bitmap;bitmap.Dispose();}
                        }
                    }
                // GDI+ also reports malformed bounded icon payloads as OutOfMemoryException.
                }catch(ArgumentException){}catch(ExternalException){}catch(IOException){}catch(OutOfMemoryException){}
            }
            return null;
        }
        static int FrameScore(IconFrame frame,int desired){int size=Math.Max(frame.Width,frame.Height);return size>=desired?size-desired:1000+desired-size;}
        static bool ValidDib(byte[] bytes,IconFrame frame) {
            if(frame.Length<12)return false;uint header=BitConverter.ToUInt32(bytes,frame.Offset);
            if(header==12)return BitConverter.ToUInt16(bytes,frame.Offset+4)==frame.Width&&BitConverter.ToUInt16(bytes,frame.Offset+6)==frame.Height*2;
            if(header<40||header>frame.Length)return false;
            return BitConverter.ToInt32(bytes,frame.Offset+4)==frame.Width&&BitConverter.ToInt32(bytes,frame.Offset+8)==frame.Height*2;
        }
        static Bitmap DecodePng(Stream stream,int width,int height) {
            // Inspect dimensions before GDI+ decoding to bound compressed-image allocations.
            long origin=stream.Position;byte[] header=new byte[24];int read=stream.Read(header,0,header.Length);stream.Position=origin;
            if(read<24||header[0]!=137||header[1]!=80||header[2]!=78||header[3]!=71)return null;
            uint actualWidth=(uint)header[16]<<24|(uint)header[17]<<16|(uint)header[18]<<8|header[19],actualHeight=(uint)header[20]<<24|(uint)header[21]<<16|(uint)header[22]<<8|header[23];
            if(actualWidth<1||actualWidth>512||actualHeight<1||actualHeight>512||width>0&&actualWidth!=width||height>0&&actualHeight!=height)return null;
            using(var decoded=new Bitmap(stream)) {
                var bitmap=decoded.Clone(new Rectangle(0,0,decoded.Width,decoded.Height),PixelFormat.Format32bppArgb);
                if(HasVisiblePixel(bitmap))return bitmap;bitmap.Dispose();return null;
            }
        }
        static bool HasVisiblePixel(Bitmap bitmap){for(int y=0;y<bitmap.Height;y++)for(int x=0;x<bitmap.Width;x++)if(bitmap.GetPixel(x,y).A>0)return true;return false;}
        sealed class IconFrame {internal int Entry,Width,Height,Offset,Length;}
        static Bitmap Shell(string path,bool attributesOnly) {
            var info=new Info();uint flags=0x100|(attributesOnly?0x10u:0u);
            IntPtr result=SHGetFileInfo(path,0x80,ref info,(uint)Marshal.SizeOf(typeof(Info)),flags);
            if(result==IntPtr.Zero||info.Icon==IntPtr.Zero)return null;
            try{using(var icon=(Icon)Icon.FromHandle(info.Icon).Clone())return icon.ToBitmap();}finally{DestroyIcon(info.Icon);}
        }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct Info {public IntPtr Icon;public int Index;public uint Attributes;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string Display;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=80)]public string Type;}
        [DllImport("shell32.dll",CharSet=CharSet.Unicode)]static extern uint ExtractIconEx(string path,int index,IntPtr[] large,IntPtr[] small,uint count);
        [DllImport("shell32.dll",CharSet=CharSet.Unicode)]static extern IntPtr SHGetFileInfo(string path,uint attrs,ref Info info,uint size,uint flags);
        [DllImport("user32.dll")]static extern bool DestroyIcon(IntPtr icon);
    }
}
