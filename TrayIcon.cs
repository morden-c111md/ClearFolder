using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ClearFolder {
    internal static class TrayIcon {
        internal static Icon Create() {
            using(var bitmap=Render(32)) {
                IntPtr handle=bitmap.GetHicon();
                try{return (Icon)Icon.FromHandle(handle).Clone();}
                finally{Native.DestroyIcon(handle);}
            }
        }
        internal static Bitmap Render(int size) {
            var bitmap=new Bitmap(size,size,PixelFormat.Format32bppArgb);
            using(var graphics=Graphics.FromImage(bitmap)) {
                graphics.SmoothingMode=SmoothingMode.AntiAlias;graphics.ScaleTransform(size/32f,size/32f);
                using(var tab=Layout.Round(new RectangleF(4,6,13,10),3))
                using(var brush=new SolidBrush(Color.FromArgb(255,71,185,224)))graphics.FillPath(brush,tab);
                using(var body=Layout.Round(new RectangleF(3,10,26,18),3.5f))
                using(var glass=new LinearGradientBrush(new PointF(6,10),new PointF(25,28),Color.FromArgb(255,93,223,239),Color.FromArgb(255,28,113,212)))
                using(var outline=new Pen(Color.FromArgb(210,173,244,255),1)) {
                    graphics.FillPath(glass,body);graphics.DrawPath(outline,body);
                }
                using(var light=new Pen(Color.FromArgb(100,255,255,255),1))graphics.DrawLine(light,7,12,25,12);
                using(var tile=new SolidBrush(Color.FromArgb(210,235,253,255)))
                    for(int row=0;row<2;row++)for(int column=0;column<2;column++)
                        using(var square=Layout.Round(new RectangleF(11+column*5.5f,16+row*5.5f,3.7f,3.7f),.8f))graphics.FillPath(tile,square);
            }
            return bitmap;
        }
    }
}
