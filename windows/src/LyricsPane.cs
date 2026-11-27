using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class LyricsPane : Form
{
    readonly Func<Rectangle> shelfBounds;
    readonly Timer poll=new Timer {Interval=90}, slide=new Timer {Interval=15};
    readonly float scale;
    readonly Font heading,verse,small;
    readonly StringFormat format=new StringFormat {Trimming=StringTrimming.Word};
    string[] lines=new string[0];
    float[] offsets=new float[]{0};
    int measuredWidth=-1, first=-1;
    float scroll,target;
    MusicClient.LyricTracker tracker=new MusicClient.LyricTracker();
    MusicClient.Lyrics snapshot,pressedSheet;
    int pressed=-1,seekVersion,seekTarget=-1;
    Point pressPoint;
    DateTime seekUntil;
    bool manualScroll;
    string song="",edge="bottom";
    double alignment=.5;
    Point dragPointer,dragOrigin,free;
    bool moving,fetching,openToggle=true,attached=true,reduce;
    readonly MusicClient.LyricsAccess access=new MusicClient.LyricsAccess();
    double reveal;
    DateTime revealStart,manualUntil;
    protected override bool ShowWithoutActivation {get{return true;}}
    internal bool Coupled {get{return attached;}}
    internal bool IsPinned {get{return Desktop.IsTopmost(Handle);}}
    internal LyricsPane(Func<Rectangle> shelf,double dpiScale,bool reduced)
    {
        shelfBounds=shelf;scale=(float)dpiScale;reduce=reduced;
        FormBorderStyle=FormBorderStyle.None;StartPosition=FormStartPosition.Manual;ShowInTaskbar=false;
        AutoScaleMode=AutoScaleMode.None;BackColor=Color.White;DoubleBuffered=true;
        Text=Edition.Text("歌词","Lyrics")+" · "+"Self Coverflow";
        heading=new Font("Segoe UI",11,FontStyle.Bold);verse=new Font("Segoe UI",13,FontStyle.Bold);small=new Font("Segoe UI",9);
        var area=Screen.FromRectangle(shelf()).WorkingArea;
        if(area.Bottom-shelf().Bottom<Math.Min(Px(210),shelf().Top-area.Top))edge="top";
        SetBoundsFromShelf();
        poll.Tick+=async delegate{await UpdateText();};
        slide.Tick+=delegate{
            reveal=reduce?1:1-Math.Pow(1-Math.Min(1,(DateTime.UtcNow-revealStart).TotalMilliseconds/230),3);
            if(attached)SetBoundsFromShelf();
            scroll+=(target-scroll)*.52f;
            if(Math.Abs(target-scroll)<.65f){scroll=target;if(reveal>=1)slide.Stop();}
            Invalidate();
        };
        FormClosed+=delegate{
            poll.Dispose();slide.Dispose();
            if(!fetching && access.opened.Count>0)Task.Run((Action)CloseSource);
        };
    }
    int Px(int value){return (int)Math.Round(value*scale);}
    internal void Start(bool pinned,Form shelfOwner){
        revealStart=DateTime.UtcNow;reveal=reduce?1:.02;SetBoundsFromShelf();
        Show();SetMode(pinned,shelfOwner);ShowRequested();slide.Start();poll.Start();var pending=UpdateText();
    }
    internal void ShowRequested(){
        if(WindowState==FormWindowState.Minimized)WindowState=FormWindowState.Normal;
        if(!Visible)Show();
        if(!attached && !Screen.AllScreens.Any(s=>s.WorkingArea.IntersectsWith(Desktop.Bounds(Handle))))attached=true;
        SetBoundsFromShelf();
        Desktop.Raise(Handle);
        openToggle=true;
    }
    internal void SetSong(string title){if(song!=title){song=title;seekVersion++;seekTarget=-1;snapshot=pressedSheet=null;tracker=new MusicClient.LyricTracker();lines=new string[0];first=-1;target=scroll=0;measuredWidth=-1;Invalidate();}}
    internal void SetMode(bool pinned,Form shelfOwner){
        Owner=shelfOwner;
        Desktop.SetPinned(Handle,pinned);
        if(attached)SetBoundsFromShelf();
    }
    Rectangle At(string side,Rectangle shelf)
    {
        var area=Screen.FromRectangle(shelf).WorkingArea;
        int w=Math.Min(Px(360),area.Width),h=Math.Min(Px(248),area.Height);
        int x=shelf.Left+(int)Math.Round((shelf.Width-w)*alignment),y=shelf.Top+(int)Math.Round((shelf.Height-h)*alignment);
        int gap=Px(3);
        if(side=="top")y=shelf.Top-h-gap;
        if(side=="bottom")y=shelf.Bottom+gap;
        if(side=="left")x=shelf.Left-w-gap;
        if(side=="right")x=shelf.Right+gap;
        return new Rectangle(x,y,w,h);
    }
    internal void SetBoundsFromShelf(){
        if(!attached || moving)return;
        var shelf=shelfBounds();var area=Screen.FromRectangle(shelf).WorkingArea;
        var rect=At(edge,shelf);
        if(!area.Contains(rect)){
            foreach(var candidate in new[]{"bottom","top","right","left"}){
                var fit=At(candidate,shelf);
                if(area.Contains(fit)){edge=candidate;rect=fit;break;}
            }
        }
        rect.X=Math.Max(area.Left,Math.Min(rect.X,area.Right-rect.Width));
        rect.Y=Math.Max(area.Top,Math.Min(rect.Y,area.Bottom-rect.Height));
        int h=Math.Max(1,(int)(rect.Height*reveal));
        if(edge=="top")rect.Y+=rect.Height-h;
        rect.Height=h;
        if(IsHandleCreated)Desktop.Place(Handle,rect);else Bounds=rect;
    }
    void CloseSource(){try{access.Close();}catch(InvalidOperationException){}catch(System.Runtime.InteropServices.COMException){}}
    async Task UpdateText(){
        if(fetching||IsDisposed)return;
        fetching=true;var source=tracker;int revision=seekVersion;string before=song;bool firstRead=openToggle;openToggle=false;
        try{
            var data=await Task.Run(()=>MusicClient.ReadLyrics(source,firstRead,access));
            if(IsDisposed || before!=song || revision!=seekVersion)return;
            snapshot=data;
            bool newLines=!lines.SequenceEqual(data.Lines);
            if(newLines){lines=data.Lines;measuredWidth=-1;}
            if(seekTarget>=0){
                if(data.First==seekTarget || DateTime.UtcNow>=seekUntil)seekTarget=-1;
                else return;
            }
            bool resumeFollow=manualScroll && DateTime.UtcNow>=manualUntil;
            if(resumeFollow)manualScroll=false;
            if(newLines || first!=data.First || measuredWidth<0 || resumeFollow){
                MeasureLines();first=data.First;
                if(!manualScroll)ScrollTo(first);
                Invalidate();
            }
        }catch(InvalidOperationException){}catch(System.Runtime.InteropServices.COMException){}
        finally{fetching=false;if(IsDisposed && access.opened.Count>0)System.Threading.ThreadPool.QueueUserWorkItem(delegate{CloseSource();});}
    }
    void MeasureLines(){
        if(measuredWidth==Width)return;
        using(var g=CreateGraphics()){
            offsets=new float[lines.Length+1];
            for(int i=0;i<lines.Length;i++)offsets[i+1]=offsets[i]+g.MeasureString(lines[i],verse,Math.Max(10,Width-Px(40)),format).Height+Px(9);
        }
        measuredWidth=Width;
    }
    void ScrollTo(int i){
        target=i>=0 && i<lines.Length?offsets[i]:0;
        if(reduce)scroll=target;else slide.Start();
    }
    protected override void OnPaint(PaintEventArgs e){
        base.OnPaint(e);var g=e.Graphics;
        g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        if(heading==null)return;
        string title=Edition.Text("歌词","Lyrics");
        g.DrawString(title,heading,Brushes.Black,Px(17),Px(8));
        float sx=Px(19)+g.MeasureString(title,heading).Width;
        TextRenderer.DrawText(g,song,small,new Rectangle((int)sx,Px(8),Math.Max(2,Width-(int)sx-Px(42)),Px(23)),Color.DimGray,TextFormatFlags.EndEllipsis|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine);
        g.DrawString("×",heading,Brushes.DimGray,Width-Px(29),Px(8));
        g.DrawRectangle(Pens.Gainsboro,0,0,Math.Max(0,Width-1),Math.Max(0,Height-1));
        g.SetClip(new Rectangle(1,Px(40),Math.Max(1,Width-2),Math.Max(1,Height-Px(41))));
        if(lines.Length==0){g.DrawString(Edition.Text("当前歌曲暂无可用歌词","No lyrics available for this song"),small,Brushes.Gray,Px(18),Px(50));return;}
        MeasureLines();
        for(int i=0;i<lines.Length;i++){
            float y=Px(42)+offsets[i]-scroll;
            if(y>Height)break;
            if(y+offsets[i+1]-offsets[i]<Px(40))continue;
            g.DrawString(lines[i],verse,i==first?Brushes.Black:Brushes.DimGray,new RectangleF(Px(18),y,Math.Max(1,Width-Px(40)),offsets[i+1]-offsets[i]),format);
        }
    }
    protected override void OnMouseWheel(MouseEventArgs e){
        base.OnMouseWheel(e);manualScroll=true;manualUntil=DateTime.UtcNow.AddSeconds(3);
        target=Math.Max(0,Math.Min(offsets[offsets.Length-1]-Px(40),target-e.Delta*scale*.7f));slide.Start();
    }
    int HitLine(Point point){
        if(snapshot==null || snapshot.Buttons==null || point.Y<Px(40))return -1;
        MeasureLines();
        float offset=point.Y-Px(42)+scroll;
        int hit=Array.BinarySearch(offsets,offset);
        if(hit<0)hit=~hit-1;
        return hit>=0 && hit<lines.Length?hit:-1;
    }
    async Task Seek(int index,MusicClient.Lyrics sheet){
        int revision=++seekVersion;manualScroll=false;seekTarget=first=index;
        seekUntil=DateTime.UtcNow.AddMilliseconds(1500);ScrollTo(index);Invalidate();
        bool ok=false;
        try{ok=await Task.Run(()=>MusicClient.SeekLine(sheet,index));}
        catch(InvalidOperationException){}catch(System.Runtime.InteropServices.COMException){}
        if(IsDisposed || revision!=seekVersion)return;
        if(!ok){seekTarget=-1;first=-1;}
        await UpdateText();
    }
    protected override void OnMouseDown(MouseEventArgs e){
        base.OnMouseDown(e);if(e.Button!=MouseButtons.Left)return;
        if(e.Y>Px(36)){pressed=HitLine(e.Location);pressedSheet=snapshot;pressPoint=e.Location;return;}
        if(e.X>=Width-Px(36)){Close();return;}
        moving=true;reveal=1;dragPointer=Cursor.Position;dragOrigin=Desktop.Bounds(Handle).Location;Capture=true;
    }
    protected override void OnMouseMove(MouseEventArgs e){
        base.OnMouseMove(e);if(!moving){Cursor=HitLine(e.Location)>=0?Cursors.Hand:Cursors.Default;return;}
        var c=Cursor.Position;free=new Point(dragOrigin.X+c.X-dragPointer.X,dragOrigin.Y+c.Y-dragPointer.Y);
        var shelf=shelfBounds();var area=Screen.FromRectangle(shelf).WorkingArea;
        var rect=new Rectangle(free,Size);double best=Px(52);attached=false;
        foreach(var side in new[]{"top","bottom","left","right"}){
            var aim=At(side,shelf);bool horizontal=side=="top" || side=="bottom";
            int span=horizontal?shelf.Width-aim.Width:shelf.Height-aim.Height;
            double ratio=span<=0?.5:Math.Max(0,Math.Min(1,(horizontal?free.X-shelf.X:free.Y-shelf.Y)/(double)span));
            if(horizontal)aim.X=shelf.X+(int)Math.Round(span*ratio);
            else aim.Y=shelf.Y+(int)Math.Round(span*ratio);
            if(!area.Contains(aim))continue;
            double distance=Math.Sqrt(Math.Pow(aim.X-free.X,2)+Math.Pow(aim.Y-free.Y,2));
            if(distance<best){edge=side;alignment=ratio;best=distance;attached=true;}
        }
        if(attached){var aim=At(edge,shelf);double pull=.85*Math.Pow(1-best/Px(52),2);rect.Offset((int)((aim.X-rect.X)*pull),(int)((aim.Y-rect.Y)*pull));}
        Desktop.Place(Handle,rect);
    }
    protected override async void OnMouseUp(MouseEventArgs e){
        base.OnMouseUp(e);
        int line=pressed;var sheet=pressedSheet;pressed=-1;pressedSheet=null;
        if(e.Button==MouseButtons.Left && line>=0 && sheet!=null && Math.Abs(e.X-pressPoint.X)<Px(6) && Math.Abs(e.Y-pressPoint.Y)<Px(6)){
            await Seek(line,sheet);return;
        }
        moving=false;Capture=false;if(attached)SetBoundsFromShelf();
    }
    protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(!Capture && moving){moving=false;SetBoundsFromShelf();}}
    protected override void Dispose(bool disposing){if(disposing){if(heading!=null)heading.Dispose();if(verse!=null)verse.Dispose();if(small!=null)small.Dispose();if(format!=null)format.Dispose();}base.Dispose(disposing);}
}
