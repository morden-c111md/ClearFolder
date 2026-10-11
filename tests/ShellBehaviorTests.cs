using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClearFolder.Tests {
    public static class ShellBehaviorTests {
        static int checks;
        static void Check(bool ok,string name){if(!ok)throw new Exception("FAIL: "+name);checks++;Console.WriteLine("PASS: "+name);}
        public static int Run(string root) {
            checks=0;Directory.CreateDirectory(root);
            Check(DesktopGrid.ScaleIconSize(48,96)==48,"100 percent desktop icon physical size");
            Check(DesktopGrid.ScaleIconSize(48,144)==72,"150 percent desktop icon physical size");
            Check(DesktopGrid.ScaleIconSize(48,192)==96,"200 percent desktop icon physical size");
            var grid=new DesktopGrid(new Size(100,100),new Point(24,4),48,new[]{new Point(24,4),new Point(124,4)},"synthetic");
            Check(grid.Snap(new Point(29,8),new Rectangle(0,0,500,500),null)==new Point(24,104),"snap selects nearest unoccupied lattice cell");
            Check(grid.Snap(new Point(260,8),new Rectangle(0,0,500,500),new[]{new Point(224,4)})==new Point(324,4),"snap avoids other folder anchors");
            Check(grid.Snap(new Point(-180,104),new Rectangle(-500,0,500,500),null)==new Point(-176,104),"grid supports negative monitor coordinates");
            bool threw=false;try{grid.Snap(Point.Empty,new Rectangle(0,0,100,100),null);}catch(InvalidOperationException){threw=true;}
            Check(threw,"full desktop does not silently overlap occupied icons");
            string icon=Path.Combine(root,"sample.ico");using(var stream=File.Create(icon))SystemIcons.Warning.Save(stream);
            string url=Path.Combine(root,"sample.url");File.WriteAllText(url,"[InternetShortcut]\r\nURL=steam://rungameid/0\r\nIconFile=sample.ico\r\nIconIndex=0\r\n");
            string actual;int index;Check(FileIcons.ReadInternetIcon(url,out actual,out index)&&actual==icon&&index==0,"url icon resolves relative file and index without launch");
            using(var expected=new Icon(icon,48,48))using(var e=expected.ToBitmap())using(var loaded=FileIcons.Load(url))Check(Equal(e,loaded),"url renders specified custom icon");
            string unicodeIcon=Path.Combine(root,"自定义.ico");File.Copy(icon,unicodeIcon);
            string unicodeUrl=Path.Combine(root,"unicode.url");File.WriteAllText(unicodeUrl,"[InternetShortcut]\r\nIconFile=自定义.ico\r\nIconIndex=0\r\n");
            Check(FileIcons.ReadInternetIcon(unicodeUrl,out actual,out index)&&actual==unicodeIcon,"UTF8 url icon paths preserve non-ASCII names");
            string invalid=Path.Combine(root,"missing.url");File.WriteAllText(invalid,"[InternetShortcut]\r\nURL=https://example.invalid\r\nIconFile=missing.ico\r\n");
            using(var loaded=FileIcons.Load(invalid))Check(loaded.Width>0&&loaded.Height>0,"missing icon falls back without crashing");
            object shell=null,link=null;string lnk=Path.Combine(root,"sample.lnk");
            try {
                shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));link=shell.GetType().InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[]{lnk});
                link.GetType().InvokeMember("TargetPath",BindingFlags.SetProperty,null,link,new object[]{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"notepad.exe")});
                link.GetType().InvokeMember("IconLocation",BindingFlags.SetProperty,null,link,new object[]{icon+",0"});link.GetType().InvokeMember("Save",BindingFlags.InvokeMethod,null,link,null);
            }finally {if(link!=null)Marshal.FinalReleaseComObject(link);if(shell!=null)Marshal.FinalReleaseComObject(shell);}
            using(var expected=new Icon(icon,48,48))using(var e=expected.ToBitmap())using(var loaded=FileIcons.Load(lnk))Check(Equal(e,loaded),"lnk renders explicit icon without launching target");
            bool mtaOk=false;Exception failure=null;var thread=new Thread(delegate(){try{using(var loaded=FileIcons.Load(lnk))mtaOk=loaded.Width>0;}catch(Exception ex){failure=ex;}});thread.SetApartmentState(ApartmentState.MTA);thread.Start();thread.Join();
            Check(mtaOk&&failure==null,"shortcut loader supports MTA callers through STA resolution");
            byte[] png48=ColorPng(48,Color.FromArgb(128,215,25,165)),png96=ColorPng(96,Color.FromArgb(255,20,190,210));
            string multi=Path.Combine(root,"alternate-frame.ico");WriteIco(multi,new[]{256,96},new[]{new byte[]{1,2,3,4},png96});
            string alternateUrl=Path.Combine(root,"alternate.url");File.WriteAllText(alternateUrl,"[InternetShortcut]\r\nIconFile=alternate-frame.ico\r\n");
            using(var loaded=FileIcons.Load(alternateUrl)){Color pixel=loaded.GetPixel(loaded.Width/2,loaded.Height/2);Check(pixel.R==20&&pixel.G==190&&pixel.B==210,"invalid preferred ICO frame falls back to a valid larger PNG frame");}
            string png=Path.Combine(root,"custom.png");File.WriteAllBytes(png,png48);string pngUrl=Path.Combine(root,"png.url");File.WriteAllText(pngUrl,"[InternetShortcut]\r\nIconFile=custom.png\r\n");
            using(var loaded=FileIcons.Load(pngUrl)){Color pixel=loaded.GetPixel(loaded.Width/2,loaded.Height/2);Check(pixel.R>180&&pixel.B>130&&pixel.G<60,"PNG custom shortcut icon is loaded as an image");Check(pixel.A>=126&&pixel.A<=130,"PNG icon preserves partial alpha");}
            string broken=Path.Combine(root,"broken.ico");File.WriteAllBytes(broken,new byte[]{0,0,1,0,1,0});
            object fallbackShell=null,fallbackLink=null;string fallbackLnk=Path.Combine(root,"bad-icon-valid-target.lnk");
            try {
                fallbackShell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));fallbackLink=fallbackShell.GetType().InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,fallbackShell,new object[]{fallbackLnk});
                fallbackLink.GetType().InvokeMember("TargetPath",BindingFlags.SetProperty,null,fallbackLink,new object[]{icon});fallbackLink.GetType().InvokeMember("IconLocation",BindingFlags.SetProperty,null,fallbackLink,new object[]{broken+",0"});fallbackLink.GetType().InvokeMember("Save",BindingFlags.InvokeMethod,null,fallbackLink,null);
            }finally{if(fallbackLink!=null)Marshal.FinalReleaseComObject(fallbackLink);if(fallbackShell!=null)Marshal.FinalReleaseComObject(fallbackShell);}
            using(var expected=new Icon(icon,48,48))using(var e=expected.ToBitmap())using(var loaded=FileIcons.Load(fallbackLnk))Check(Equal(e,loaded),"corrupt explicit lnk icon still tries valid target icon");
            byte[] png256=ColorPng(256,Color.FromArgb(255,30,170,225));string quality=Path.Combine(root,"high-resolution.ico");WriteIco(quality,new[]{48,256},new[]{png48,png256});
            string qualityUrl=Path.Combine(root,"high-resolution.url");File.WriteAllText(qualityUrl,"[InternetShortcut]\r\nIconFile=high-resolution.ico\r\n");
            using(var loaded=FileIcons.Load(qualityUrl))Check(loaded.Width==256&&loaded.Height==256&&loaded.GetPixel(128,128).B==225,"multi-resolution ICO uses native 256px image for high-DPI downsampling");
            using(var loaded=FileIcons.Load(url))using(var expected=new Icon(icon))Check(loaded.Size==expected.Size,"small-only ICO retains native size without artificial upscaling");
            string transparent=Path.Combine(root,"transparent-preferred.ico");WriteIco(transparent,new[]{256,96},new[]{ColorPng(256,Color.Transparent),png96});
            using(var loaded=FileIcons.ReadIconBitmap(transparent,256))Check(loaded!=null&&loaded.Width==96&&loaded.GetPixel(48,48).G==190,"fully transparent preferred frame falls back to a visible native frame");
            return checks;
        }
        static byte[] ColorPng(int size,Color color){using(var bitmap=new Bitmap(size,size,PixelFormat.Format32bppArgb)){using(var g=Graphics.FromImage(bitmap))g.Clear(color);using(var stream=new MemoryStream()){bitmap.Save(stream,ImageFormat.Png);return stream.ToArray();}}}
        static void WriteIco(string path,int[] sizes,byte[][] frames){using(var stream=File.Create(path))using(var writer=new BinaryWriter(stream)){writer.Write((ushort)0);writer.Write((ushort)1);writer.Write((ushort)frames.Length);int offset=6+16*frames.Length;for(int i=0;i<frames.Length;i++){writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)0);writer.Write((byte)0);writer.Write((ushort)1);writer.Write((ushort)32);writer.Write(frames[i].Length);writer.Write(offset);offset+=frames[i].Length;}foreach(byte[] frame in frames)writer.Write(frame);}}
        static bool Equal(Bitmap a,Bitmap b){if(a.Size!=b.Size)return false;for(int x=0;x<a.Width;x++)for(int y=0;y<a.Height;y++)if(a.GetPixel(x,y)!=b.GetPixel(x,y))return false;return true;}
    }
}
