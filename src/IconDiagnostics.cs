using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms=System.Windows.Forms;
using Image=System.Windows.Controls.Image;

namespace ClearFolder {
    internal static class IconDiagnostics {
        public static bool Run(string root) {
            string artifacts=Path.Combine(root,"artifacts"),testRoot=Path.Combine(artifacts,"test-icon-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(testRoot);var log=new List<string>();bool passed=false;GlassSurface surface=null;
            try {
                string original=Environment.GetEnvironmentVariable("CLEARFOLDER_ICON_SHORTCUT");
                if(string.IsNullOrWhiteSpace(original)||!File.Exists(original))throw new FileNotFoundException("Set CLEARFOLDER_ICON_SHORTCUT to an explicit .url shortcut with a native 256px PNG ICO frame before running --icon-test.");
                string iconFile;int iconIndex;if(!FileIcons.ReadInternetIcon(original,out iconFile,out iconIndex))throw new InvalidDataException("Shortcut does not resolve an existing custom icon.");
                string shortcut=Path.Combine(testRoot,"Icon fixture.url");
                File.WriteAllText(shortcut,"[InternetShortcut]\r\nURL=about:blank\r\nIconFile="+iconFile+"\r\nIconIndex="+iconIndex+"\r\n",System.Text.Encoding.UTF8);
                var store=new Store(Path.Combine(testRoot,"data"));store.Load();Deck deck=store.State.Decks[0];deck.Name="图标清晰度验证";Entry entry=store.AddReference(deck,shortcut);
                Rectangle work=Forms.Screen.PrimaryScreen.WorkingArea;float scale;using(var g=Graphics.FromHwnd(IntPtr.Zero))scale=g.DpiX/96f;
                Layout layout=Layout.Calculate(new Point(work.Left+work.Width/3,work.Top+work.Height/3),1,0,work,scale);
                surface=new GlassSurface();surface.SetBounds(layout.Bounds);surface.Configure(layout,deck,store.State);
                using(var background=new Bitmap(layout.Bounds.Width,layout.Bounds.Height)) {
                    using(var g=Graphics.FromImage(background))using(var a=new SolidBrush(System.Drawing.Color.FromArgb(38,65,90)))using(var b=new SolidBrush(System.Drawing.Color.FromArgb(60,90,110)))
                        for(int x=0;x<background.Width;x+=32)for(int y=0;y<background.Height;y+=32)g.FillRectangle(((x/32+y/32)%2)==0?a:b,x,y,32,32);
                    surface.SetBackground(background,1);
                }
                surface.SetExpanded(true,true);surface.Show();
                var field=typeof(GlassSurface).GetField("icons",BindingFlags.Instance|BindingFlags.NonPublic);
                var icons=(Dictionary<string,BitmapSource>)field.GetValue(surface);BitmapSource cached=null;var clock=Stopwatch.StartNew();
                while(clock.ElapsedMilliseconds<4000&&!icons.TryGetValue(entry.Path,out cached)){Forms.Application.DoEvents();Thread.Sleep(5);}
                if(cached==null)throw new TimeoutException("The asynchronous WPF icon source cache was not populated.");
                log.Add("Cached WPF BitmapSource: "+cached.PixelWidth+" x "+cached.PixelHeight+"; frozen="+cached.IsFrozen+"; format="+cached.Format);
                log.Add("Renderer: "+surface.RendererDescription);log.Add("Desktop DPI scale: "+scale);
                var foreground=(Canvas)typeof(GlassSurface).GetField("foreground",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(surface);
                var cache=foreground.CacheMode as BitmapCache;log.Add("Foreground BitmapCache RenderAtScale: "+(cache==null?"none":cache.RenderAtScale.ToString()));
                foreach(System.Windows.UIElement child in foreground.Children){var image=child as Image;if(image!=null)log.Add("Panel image logical physical-layout size: "+image.Width+" x "+image.Height+"; scaling="+RenderOptions.GetBitmapScalingMode(image));}
                using(var expected=DirectLargestPng(iconFile)) {
                    if(expected==null)throw new InvalidDataException("No valid native 256px PNG frame was found in the original ICO.");
                    bool dimensions=cached.PixelWidth==256&&cached.PixelHeight==256&&expected.Width==256&&expected.Height==256;
                    log.Add((dimensions?"PASS: ":"FAIL: ")+"actual WPF cache retains original 256px image rather than a 48px thumbnail");
                    int stride=cached.PixelWidth*4;byte[] actual=new byte[stride*cached.PixelHeight];cached.CopyPixels(actual,stride,0);
                    int alphaDifferences=0,rawVisibleDifferences=0,maxPremultipliedDifference=0;
                    if(dimensions)for(int y=0;y<256;y++)for(int x=0;x<256;x++) {
                        System.Drawing.Color source=expected.GetPixel(x,y);int at=y*stride+x*4;
                        if(actual[at+3]!=source.A)alphaDifferences++;
                        if(source.A>0&&(actual[at]!=source.B||actual[at+1]!=source.G||actual[at+2]!=source.R||actual[at+3]!=source.A))rawVisibleDifferences++;
                        int[] components={source.B,source.G,source.R};
                        for(int c=0;c<3;c++)maxPremultipliedDifference=Math.Max(maxPremultipliedDifference,Math.Abs((actual[at+c]*actual[at+3]+127)/255-(components[c]*source.A+127)/255));
                    }
                    log.Add("Direct ICO PNG vs WPF source alpha differences: "+alphaDifferences);
                    log.Add("Raw visible-color differences after GDI+ upload: "+rawVisibleDifferences);
                    log.Add("Maximum premultiplied color-channel error: "+maxPremultipliedDifference+" / 255 (transparent RGB does not affect display)");
                    bool colors=dimensions&&alphaDifferences==0&&maxPremultipliedDifference<=2;log.Add((colors?"PASS: ":"FAIL: ")+"native source transparency and displayed colors survive WPF upload");passed=colors;
                }
                var wait=Stopwatch.StartNew();while(wait.ElapsedMilliseconds<250){Forms.Application.DoEvents();Thread.Sleep(5);}
                surface.ExportFrame(Path.Combine(artifacts,"wallpaper-panel-preview.png"));log.Add("Export: own WPF visual with synthetic background; no desktop screenshot or user layout modification.");
            }catch(Exception error){log.Add("FAIL: "+error);passed=false;}
            finally{if(surface!=null)surface.Dispose();}
            log.Add("Synthetic data retained: "+testRoot);log.Add("Overall: "+(passed?"PASS":"FAIL"));File.WriteAllLines(Path.Combine(artifacts,"icon-test.txt"),log.ToArray());return passed;
        }
        static Bitmap DirectLargestPng(string file) {
            byte[] bytes=File.ReadAllBytes(file);if(bytes.Length<6)return null;int count=BitConverter.ToUInt16(bytes,4);
            for(int i=0;i<count&&6L+(i+1)*16L<=bytes.Length;i++) {
                int p=6+i*16,width=bytes[p]==0?256:bytes[p],height=bytes[p+1]==0?256:bytes[p+1];uint offset=BitConverter.ToUInt32(bytes,p+12),length=BitConverter.ToUInt32(bytes,p+8);
                if(width!=256||height!=256||length<24||(long)offset+length>bytes.Length||bytes[offset]!=137||bytes[offset+1]!=80)continue;
                using(var stream=new MemoryStream(bytes,(int)offset,(int)length,false))using(var bitmap=new Bitmap(stream))return bitmap.Clone(new Rectangle(0,0,bitmap.Width,bitmap.Height),System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            }
            return null;
        }
    }
}

