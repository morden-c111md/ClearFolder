using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Threading;

namespace ClearFolder {
    internal sealed class GlassRenderer : IDisposable {
        sealed class CachedIcon {public Bitmap Image;public bool Missing;public bool Pending;public DateTime Updated;}
        readonly Dictionary<string,CachedIcon> icons=new Dictionary<string,CachedIcon>(StringComparer.OrdinalIgnoreCase);
        readonly Bitmap placeholder=SystemIcons.Application.ToBitmap();
        readonly object iconLock=new object();
        bool disposed;
        Bitmap texture;
        byte[] pixels;
        int textureWidth,textureHeight;
        public void SetBackground(Bitmap source,double contrast) {
            if(texture!=null)texture.Dispose();
            texture=new Bitmap(source.Width,source.Height,PixelFormat.Format32bppArgb);
            using(var g=Graphics.FromImage(texture))g.DrawImageUnscaled(source,0,0);
            textureWidth=texture.Width;textureHeight=texture.Height;
            var data=texture.LockBits(new Rectangle(0,0,texture.Width,texture.Height),ImageLockMode.ReadWrite,PixelFormat.Format32bppArgb);
            try {
                pixels=new byte[texture.Width*texture.Height*4];
                for(int y=0;y<texture.Height;y++)System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(data.Scan0,y*data.Stride),pixels,y*texture.Width*4,texture.Width*4);
                for(int i=0;i<pixels.Length;i+=4){
                    // Contrast is applied around mid-gray; no white tint, blur, or luminous rim.
                    pixels[i]=Tone(pixels[i],contrast);pixels[i+1]=Tone(pixels[i+1],contrast);pixels[i+2]=Tone(pixels[i+2],contrast);pixels[i+3]=255;
                }
                for(int y=0;y<texture.Height;y++)System.Runtime.InteropServices.Marshal.Copy(pixels,y*texture.Width*4,IntPtr.Add(data.Scan0,y*data.Stride),texture.Width*4);
            }finally{texture.UnlockBits(data);}
        }
        static byte Tone(byte value,double contrast){return (byte)Math.Max(0,Math.Min(255,(value-127.5)*contrast+127.5));}
        Bitmap IconFor(Entry entry) {
            lock(iconLock) {
                CachedIcon cached;
                if(!icons.TryGetValue(entry.Path,out cached)){cached=new CachedIcon();icons.Add(entry.Path,cached);}
                if(!cached.Pending&&(DateTime.UtcNow-cached.Updated).TotalSeconds>15) {
                    cached.Pending=true;string path=entry.Path;CachedIcon record=cached;
                    ThreadPool.QueueUserWorkItem(delegate {
                        Bitmap loaded=null;bool missing=false;
                        try {missing=!Store.Exists(path);if(!missing)loaded=Native.FileIcon(path);}
                        catch {missing=true;}
                        lock(iconLock) {
                            CachedIcon current;
                            if(disposed||!icons.TryGetValue(path,out current)||current!=record){if(loaded!=null)loaded.Dispose();return;}
                            if(loaded!=null){if(record.Image!=null)record.Image.Dispose();record.Image=loaded;}
                            record.Missing=missing;record.Updated=DateTime.UtcNow;record.Pending=false;
                        }
                    });
                }
                return cached.Image??placeholder;
            }
        }
        bool Missing(Entry entry){lock(iconLock){CachedIcon cached;return icons.TryGetValue(entry.Path,out cached)&&cached.Missing;}}
        public void RefreshIcons(){lock(iconLock){foreach(var icon in icons.Values)if(icon.Image!=null)icon.Image.Dispose();icons.Clear();}}
        internal void PreloadForDiagnostics(Deck deck) {
            foreach(var entry in deck.Entries)lock(iconLock) {
                if(!icons.ContainsKey(entry.Path))icons.Add(entry.Path,new CachedIcon{Image=Native.FileIcon(entry.Path),Updated=DateTime.UtcNow});
            }
        }
        unsafe Bitmap Refract(PointF mouse,double seconds,double age,double strength,Rectangle region) {
            if(texture==null)return null;
            var result=new Bitmap(textureWidth,textureHeight,PixelFormat.Format32bppArgb);
            var data=result.LockBits(new Rectangle(0,0,result.Width,result.Height),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
            try {
                const int step=6;
                region.Intersect(new Rectangle(0,0,textureWidth,textureHeight));
                for(int y=region.Top;y<region.Bottom;y+=step)for(int x=region.Left;x<region.Right;x+=step) {
                    float dx=x+step/2f-mouse.X,dy=y+step/2f-mouse.Y;
                    double distance=Math.Sqrt(dx*dx+dy*dy)+0.001;
                    double front=age*135;
                    double envelope=Math.Exp(-Math.Pow((distance-front)/42,2))*Math.Exp(-age*2.1);
                    if(age>1.6)envelope=0;
                    double wave=Math.Sin(distance*.105-age*12)*envelope*strength;
                    double offsetX=wave*dx/distance+Math.Sin(y*.024+seconds*.7)*.16;
                    double offsetY=wave*dy/distance+Math.Cos(x*.022-seconds*.6)*.16;
                    for(int yy=y;yy<Math.Min(y+step,region.Bottom);yy++) {
                        byte* row=(byte*)data.Scan0+yy*data.Stride;
                        for(int xx=x;xx<Math.Min(x+step,region.Right);xx++) {
                            double fx=Math.Max(0,Math.Min(textureWidth-1.001,xx+offsetX));
                            double fy=Math.Max(0,Math.Min(textureHeight-1.001,yy+offsetY));
                            int sx=(int)fx,sy=(int)fy,sx1=Math.Min(sx+1,textureWidth-1),sy1=Math.Min(sy+1,textureHeight-1);
                            double ax=fx-sx,ay=fy-sy;
                            int a=(sy*textureWidth+sx)*4,b=(sy*textureWidth+sx1)*4,c=(sy1*textureWidth+sx)*4,d=(sy1*textureWidth+sx1)*4;
                            for(int channel=0;channel<3;channel++)row[xx*4+channel]=(byte)((pixels[a+channel]*(1-ax)+pixels[b+channel]*ax)*(1-ay)+(pixels[c+channel]*(1-ax)+pixels[d+channel]*ax)*ay);
                            row[xx*4+3]=255;
                        }
                    }
                }
            }finally{result.UnlockBits(data);}
            return result;
        }
        public Bitmap Render(Layout layout,Deck deck,Settings settings,float progress,PointF mouse,double seconds,double rippleAge,int hover,bool dropHover) {
            var canvas=new Bitmap(layout.Bounds.Width,layout.Bounds.Height,PixelFormat.Format32bppArgb);
            Rectangle sampleRegion=progress>.002f?new Rectangle(0,0,canvas.Width,canvas.Height):layout.Icon;
            using(var g=Graphics.FromImage(canvas))using(var glass=Refract(mouse,seconds,rippleAge,settings.RippleStrength,sampleRegion)) {
                g.SmoothingMode=SmoothingMode.AntiAlias;g.InterpolationMode=InterpolationMode.HighQualityBicubic;
                g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
                using(var shape=Layout.Round(layout.Icon,10*layout.Scale))PaintGlass(g,shape,glass);
                DrawMinis(g,layout,deck);
                Text(g,deck.Name,layout.Label,12*layout.Scale,true,Color.White);
                if(progress>.002f) {
                    using(var panelLayer=new Bitmap(canvas.Width,canvas.Height,PixelFormat.Format32bppArgb)) {
                        using(var pg=Graphics.FromImage(panelLayer)) {
                            pg.SmoothingMode=SmoothingMode.AntiAlias;pg.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
                            using(var path=layout.PanelPath())PaintGlass(pg,path,glass);
                            Color label=LabelColor(layout.Panel);
                            Text(pg,deck.Name,new RectangleF(layout.Panel.X+14*layout.Scale,layout.Panel.Y+5*layout.Scale,layout.Panel.Width-28*layout.Scale,24*layout.Scale),12*layout.Scale,false,label);
                            if(deck.Entries.Count==0) {
                                Text(pg,"拖入文件或快捷方式",new RectangleF(layout.Panel.X+6,layout.Panel.Y+42*layout.Scale,layout.Panel.Width-12,28*layout.Scale),10*layout.Scale,true,label);
                                Text(pg,"右键可添加、重命名",new RectangleF(layout.Panel.X+6,layout.Panel.Y+68*layout.Scale,layout.Panel.Width-12,20*layout.Scale),8*layout.Scale,true,label);
                            }
                            for(int i=0;i<layout.Cells.Length;i++) {
                                Rectangle cell=layout.Cells[i];Entry entry=deck.Entries[layout.Start+i];
                                if(hover==i)using(var hoverPath=Layout.Round(cell,9*layout.Scale))using(var brush=new SolidBrush(Color.FromArgb(24,label)))pg.FillPath(brush,hoverPath);
                                int size=(int)(36*layout.Scale),cx=cell.X+(cell.Width-size)/2;
                                lock(iconLock)pg.DrawImage(IconFor(entry),new Rectangle(cx,cell.Y+(int)(4*layout.Scale),size,size));
                                var tr=new RectangleF(cell.X+2,cell.Y+43*layout.Scale,cell.Width-4,29*layout.Scale);
                                Text(pg,entry.Name,tr,11*layout.Scale,true,label);
                                if(Missing(entry))using(var pen=new Pen(Color.IndianRed,2))pg.DrawLine(pen,cx,cell.Y+size,cx+size,cell.Y+4);
                            }
                            if(layout.Pages>1)Text(pg,"‹    "+(layout.Page+1)+" / "+layout.Pages+"    ›",new RectangleF(layout.Panel.X,layout.Panel.Bottom-23*layout.Scale,layout.Panel.Width,20*layout.Scale),9*layout.Scale,true,label);
                        }
                        float p=Math.Min(1,progress),scale=.18f+.82f*progress;
                        float originX=layout.Left?layout.Panel.Right:layout.Panel.Left,originY=layout.Icon.Y+layout.Icon.Height/2f;
                        var state=g.Save();g.TranslateTransform(originX,originY);g.ScaleTransform(scale,scale);g.TranslateTransform(-originX,-originY);
                        using(var attributes=new ImageAttributes()) {
                            var matrix=new ColorMatrix();matrix.Matrix33=p;attributes.SetColorMatrix(matrix);
                            g.DrawImage(panelLayer,new Rectangle(0,0,panelLayer.Width,panelLayer.Height),0,0,panelLayer.Width,panelLayer.Height,GraphicsUnit.Pixel,attributes);
                        }
                        g.Restore(state);
                    }
                }
                if(dropHover)using(var shape=Layout.Round(layout.Icon,10*layout.Scale))using(var brush=new SolidBrush(Color.FromArgb(45,80,150,230)))g.FillPath(brush,shape);
            }
            return canvas;
        }
        Color LabelColor(Rectangle region) {
            if(pixels==null)return Color.White;
            double sum=0;int count=0;
            for(int y=Math.Max(0,region.Y);y<Math.Min(textureHeight,region.Bottom);y+=12)for(int x=Math.Max(0,region.X);x<Math.Min(textureWidth,region.Right);x+=12){int i=(y*textureWidth+x)*4;sum+=pixels[i]*.0722+pixels[i+1]*.7152+pixels[i+2]*.2126;count++;}
            return count>0&&sum/count>145?Color.FromArgb(24,29,38):Color.White;
        }
        void PaintGlass(Graphics g,GraphicsPath path,Bitmap glass) {
            for(int i=5;i>=1;i--) {
                using(var shadow=(GraphicsPath)path.Clone())using(var transform=new Matrix())using(var brush=new SolidBrush(Color.FromArgb(5,0,0,0))) {
                    transform.Translate(0,i);shadow.Transform(transform);using(var pen=new Pen(brush,i*2))g.DrawPath(pen,shadow);g.FillPath(brush,shadow);
                }
            }
            var state=g.Save();g.SetClip(path);
            if(glass!=null)g.DrawImageUnscaled(glass,0,0);
            else using(var fallback=new SolidBrush(Color.FromArgb(22,25,35,45)))g.FillPath(fallback,path);
            g.Restore(state);
        }
        void DrawMinis(Graphics g,Layout layout,Deck deck) {
            int count=Math.Min(4,deck.Entries.Count);float s=layout.Scale;
            if(count==0) {
                using(var pen=new Pen(Color.FromArgb(180,220,232,240),1.4f*s)) {
                    float cx=layout.Icon.X+layout.Icon.Width/2f,cy=layout.Icon.Y+layout.Icon.Height/2f;
                    g.DrawLine(pen,cx-6*s,cy,cx+6*s,cy);g.DrawLine(pen,cx,cy-6*s,cx,cy+6*s);
                }
            }
            for(int i=0;i<count;i++) {
                int size=(int)(16*s),x=layout.Icon.X+(int)(6*s)+(i%2)*(int)(21*s),y=layout.Icon.Y+(int)(6*s)+(i/2)*(int)(21*s);
                lock(iconLock)g.DrawImage(IconFor(deck.Entries[i]),new Rectangle(x,y,size,size));
            }
        }
        static void Text(Graphics g,string text,RectangleF rect,float size,bool center,Color color) {
            using(var font=new Font("Microsoft YaHei UI",Math.Max(6,size),FontStyle.Regular,GraphicsUnit.Pixel))using(var format=new StringFormat()) {
                format.Alignment=center?StringAlignment.Center:StringAlignment.Near;format.LineAlignment=StringAlignment.Center;format.Trimming=StringTrimming.EllipsisCharacter;
                var shadow=rect;shadow.Offset(0,1);
                using(var brush=new SolidBrush(color.R>128?Color.FromArgb(170,0,0,0):Color.FromArgb(150,255,255,255)))g.DrawString(text,font,brush,shadow,format);
                using(var brush=new SolidBrush(color))g.DrawString(text,font,brush,rect,format);
            }
        }
        public void Dispose(){lock(iconLock){disposed=true;RefreshIcons();placeholder.Dispose();}if(texture!=null)texture.Dispose();}
    }
}
