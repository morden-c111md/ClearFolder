using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace ClearFolder {
    public sealed class Layout {
        public Rectangle Bounds,Icon,Label,Panel;
        public Rectangle[] Cells;
        public bool Left;
        public int Columns,Page,Pages,Start;
        public float Scale;
        public static int ColumnCount(int count){return count<=4?2:count<=9?3:4;}
        public static Layout Calculate(Point anchor,int count,int page,Rectangle work,float scale) {
            return Calculate(anchor,count,page,work,scale,Size.Empty,0);
        }
        public static Layout Calculate(Point anchor,int count,int page,Rectangle work,float scale,Size desktopCell,int desktopIcon) {
            int columns=ColumnCount(count),pages=Math.Max(1,(count+15)/16);
            int fullRows=Math.Max(1,(Math.Min(count,16)+columns-1)/columns);
            float maxY=work.Height/(float)(fullRows*78+46+(pages>1?22:0)+24);
            float maxX=work.Width/(float)(columns*78+28+76+24+24);
            scale=Math.Min(scale,Math.Min(maxX,maxY));
            var v=new Layout { Scale=scale,Columns=columns,Pages=pages };
            v.Page=Math.Max(0,Math.Min(v.Pages-1,page));v.Start=v.Page*16;
            int shown=Math.Min(16,Math.Max(0,count-v.Start));
            int cell=(int)(78*scale),pad=(int)(14*scale),header=(int)(32*scale);
            int rows=Math.Max(1,(shown+v.Columns-1)/v.Columns);
            int width=v.Columns*cell+pad*2,height=rows*cell+header+pad+(v.Pages>1?(int)(22*scale):0);
            int icon=(int)(48*scale*1.2f),slot=(int)(76*scale),gap=(int)(24*scale),margin=(int)(12*scale);
            bool nativeGrid=desktopIcon>0&&desktopCell.Width>0;
            if(nativeGrid){icon=Math.Min((int)Math.Round(desktopIcon*1.2),Math.Min(desktopCell.Width,desktopCell.Height));slot=desktopCell.Width;}
            int ax=Math.Max(work.Left+margin,Math.Min(work.Right-slot-margin,anchor.X));
            int totalHeight=icon+(int)(3*scale)+(int)(26*scale);
            int ay=Math.Max(work.Top+margin,Math.Min(work.Bottom-totalHeight-margin,anchor.Y));
            int iconX=ax+(slot-icon)/2,iconY=ay;
            if(nativeGrid){
                iconX=Math.Max(work.Left,Math.Min(work.Right-icon,anchor.X-(icon-desktopIcon)/2));
                iconY=Math.Max(work.Top,Math.Min(work.Bottom-icon-(int)(29*scale),anchor.Y-(icon-desktopIcon)/2));
                ax=iconX-(slot-icon)/2;
            }
            v.Left=iconX+icon+gap+width+margin>work.Right;
            int px=v.Left?iconX-gap-width:iconX+icon+gap;
            px=Math.Max(work.Left+margin,Math.Min(work.Right-width-margin,px));
            int py=Math.Max(work.Top+margin,Math.Min(work.Bottom-height-margin,iconY+icon/2-header-pad));
            var ir=new Rectangle(iconX,iconY,icon,icon);
            var lr=new Rectangle(ax,iconY+icon+(int)(3*scale),slot,(int)(26*scale));
            if(nativeGrid)lr.Intersect(work);
            var pr=new Rectangle(px,py,width,height);
            var bounds=Rectangle.Union(Rectangle.Union(ir,lr),pr);bounds.Inflate(margin,margin);
            v.Bounds=bounds;
            v.Icon=Offset(ir,-bounds.X,-bounds.Y);v.Label=Offset(lr,-bounds.X,-bounds.Y);v.Panel=Offset(pr,-bounds.X,-bounds.Y);
            v.Cells=new Rectangle[shown];
            for(int i=0;i<shown;i++)v.Cells[i]=new Rectangle(v.Panel.X+pad+(i%v.Columns)*cell,v.Panel.Y+header+(i/v.Columns)*cell,cell,cell);
            return v;
        }
        static Rectangle Offset(Rectangle r,int x,int y){r.Offset(x,y);return r;}
        public GraphicsPath PanelPath() {
            var p=new GraphicsPath();float r=13*Scale;
            float x=Panel.X,y=Panel.Y,w=Panel.Width,h=Panel.Height;
            float center=Math.Max(y+r+10*Scale,Math.Min(y+h-r-10*Scale,Icon.Y+Icon.Height/2f));
            float tip=Left?Icon.Left+2*Scale:Icon.Right-2*Scale;
            float length=Math.Abs(tip-(Left?x+w:x));
            float half=Math.Max(3*Scale,(float)Math.Tan(8*Math.PI/180)*length);
            float root=half+5*Scale;
            p.StartFigure();p.AddArc(x,y,r*2,r*2,180,90);
            p.AddLine(x+r,y,x+w-r,y);p.AddArc(x+w-2*r,y,2*r,2*r,270,90);
            if(Left) {
                p.AddLine(x+w,y+r,x+w,center-root);
                p.AddBezier(x+w,center-root,x+w,center-half,tip-7*Scale,center-1*Scale,tip,center);
                p.AddBezier(tip,center,tip-7*Scale,center+1*Scale,x+w,center+half,x+w,center+root);
                p.AddLine(x+w,center+root,x+w,y+h-r);
            } else p.AddLine(x+w,y+r,x+w,y+h-r);
            p.AddArc(x+w-2*r,y+h-2*r,2*r,2*r,0,90);
            p.AddLine(x+w-r,y+h,x+r,y+h);p.AddArc(x,y+h-2*r,2*r,2*r,90,90);
            if(!Left) {
                p.AddLine(x,y+h-r,x,center+root);
                p.AddBezier(x,center+root,x,center+half,tip+7*Scale,center+1*Scale,tip,center);
                p.AddBezier(tip,center,tip+7*Scale,center-1*Scale,x,center-half,x,center-root);
                p.AddLine(x,center-root,x,y+r);
            }else p.AddLine(x,y+h-r,x,y+r);
            p.CloseFigure();return p;
        }
        public static GraphicsPath Round(RectangleF r,float radius) {
            var p=new GraphicsPath();float d=Math.Min(radius*2,Math.Min(r.Width,r.Height));
            p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);
            p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;
        }
        public bool Contains(Point local,bool expanded) {
            if(Icon.Contains(local)||Label.Contains(local))return true;
            if(!expanded)return false;
            using(var path=PanelPath())return path.IsVisible(local);
        }
        public bool InHoverCorridor(Point local) {
            if(local.X<0||local.Y<0||local.X>=Bounds.Width||local.Y>=Bounds.Height)return false;
            float from=Left?Math.Max(Icon.Right,Label.Right):Math.Min(Icon.Left,Label.Left),to=Left?Panel.Right:Panel.Left;
            if(Math.Abs(to-from)<1)return false;
            float progress=(local.X-from)/(to-from);
            if(progress<0||progress>1)return false;
            float padding=12*Scale;
            float top=Icon.Top-padding+(Panel.Top+13*Scale-(Icon.Top-padding))*progress;
            float entryBottom=Math.Max(Icon.Bottom,Label.Bottom)+padding;
            float bottom=entryBottom+(Panel.Bottom-13*Scale-entryBottom)*progress;
            return local.Y>=top&&local.Y<=bottom;
        }
        public bool ContainsHover(Point local,bool expanded) {
            return Contains(local,expanded)||(expanded&&InHoverCorridor(local));
        }
    }
    public sealed class HoverState {
        public bool Open;
        long enter=-1;
        public bool Update(long now,bool inside,bool blocked) {
            bool previous=Open;
            if(blocked){enter=-1;return false;}
            if(inside) {
                if(enter<0)enter=now;
                if(now-enter>=220)Open=true;
            }else {
                enter=-1;Open=false;
            }
            return previous!=Open;
        }
        public void Close(){Open=false;enter=-1;}
    }
}
