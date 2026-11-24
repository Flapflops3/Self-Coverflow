using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal sealed class Shelf : Form
{
    readonly WebView2 view=new WebView2();
    readonly JavaScriptSerializer json=new JavaScriptSerializer { MaxJsonLength=16*1024*1024 };
    readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer {Interval=2000};
    readonly System.Windows.Forms.Timer browseTimer=new System.Windows.Forms.Timer {Interval=2500};
    long browseSequence;
    readonly string baseDir=AppDomain.CurrentDomain.BaseDirectory;
    readonly string profile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),Edition.Profile);
    readonly bool verify;
    List<Album> albums=new List<Album>();
    IntPtr desktopParent=IntPtr.Zero;
    bool preferDock=true,reading,polling,ready,dragging;
    bool pinned;
    LyricsPane lyrics;
    bool changingMode;
    string songTitle="";
    readonly int snapDistance;
    readonly double scale;
    string sizeMode="classic";
    int anchorX;
    Point landing;
    readonly System.Windows.Forms.Timer windowTimer=new System.Windows.Forms.Timer {Interval=15};
    readonly Stopwatch windowClock=new Stopwatch();
    Rectangle motionStart,motionEnd;
    int motionDuration;
    bool settling;
    Screen verificationScreen;
    int visiblePositions,positionsOutsideMonitor;
    int request;
    readonly SemaphoreSlim transportCommands=new SemaphoreSlim(1,1);
    int verifiedToggles,verifiedOpens;
    Point dragCursor,dragOrigin;
    long dbStamp=-1,artStamp=-1;
    string lastMedia="";
    protected override bool ShowWithoutActivation { get { return preferDock; } }
    public Shelf(bool verifyUI)
    {
        verify=verifyUI;
        Text="Self Coverflow · Apple Music"; FormBorderStyle=FormBorderStyle.None;
        AutoScaleMode=AutoScaleMode.None;
        using(var g=Graphics.FromHwnd(IntPtr.Zero)) scale=g.DpiX/96.0;
        snapDistance=(int)Math.Round(14*scale);
        ClientSize=new Size((int)Math.Round(472*scale),(int)Math.Round(306*scale));
        BackColor=Color.White; StartPosition=FormStartPosition.Manual;
        if(verify)verificationScreen=Screen.AllScreens.FirstOrDefault(s=>!s.Primary) ?? Screen.PrimaryScreen;
        var screen=(verificationScreen ?? Screen.FromPoint(Cursor.Position)).WorkingArea;
        Location=new Point(screen.Right-Width-32,screen.Top+44);
        anchorX=Left+Width/2;
        Directory.CreateDirectory(profile);
        string config=Path.Combine(profile,"window.txt");
        if(!verify && File.Exists(config)) try {
            var parts=File.ReadAllLines(config); var pt=new Point(int.Parse(parts[0]),int.Parse(parts[1]));
            var savedScreen=parts.Length>=9 ? Screen.AllScreens.FirstOrDefault(s=>s.DeviceName==parts[6]) : null;
            int oldX,oldY;
            if(savedScreen!=null && int.TryParse(parts[7],out oldX) && int.TryParse(parts[8],out oldY)){
                var origin=savedScreen.WorkingArea.Location;
                pt.Offset(origin.X-oldX,origin.Y-oldY);
            }
            if(savedScreen!=null || Screen.AllScreens.Any(s=>s.WorkingArea.Contains(new Point(pt.X+Width/2,pt.Y+35)))) Location=pt;
            preferDock=parts[2]=="desktop";
            pinned=parts.Length>3 && parts[3]=="pinned";
            sizeMode=parts.Length>4 && (parts[4]=="half" || parts[4]=="wide") ? parts[4] : "classic";
            anchorX=Left+Width/2;
            int savedAnchor;
            if(parts.Length>5 && int.TryParse(parts[5],out savedAnchor))anchorX=savedAnchor+pt.X-int.Parse(parts[0]);
        } catch(IOException) {} catch(FormatException) {} catch(IndexOutOfRangeException) {}
        var area=Screen.FromRectangle(Bounds).WorkingArea;
        anchorX=Math.Max(area.Left,Math.Min(anchorX,area.Right));
        Width=ModeWidth(area);
        Location=new Point(Math.Max(area.Left,Math.Min(Left,area.Right-Width)),Math.Max(area.Top,Math.Min(Top,area.Bottom-Height)));
        RoundCorners();SizeChanged+=delegate {RoundCorners();};
        windowTimer.Tick+=delegate {TickWindow();};
        Icon=new Icon(Path.Combine(baseDir,"app","record.ico"));
        Controls.Add(view);LayoutSurface();
        ClientSizeChanged+=delegate { LayoutSurface(); };
        browseTimer.Tick+=delegate {browseTimer.Stop();Post(new {type="idle",sequence=browseSequence});};
        Shown+=async delegate { try { await Initialize(); } catch(Exception ex) { Fail(ex); } };
        FormClosing+=delegate {
            Playback.Changed=null;timer.Stop();browseTimer.Stop();windowTimer.Stop(); request++;
            if(lyrics!=null)lyrics.Close();
            SavePosition();
        };
    }
    void SavePosition()
    {
        if(verify || WindowState==FormWindowState.Minimized) return;
        try {
            var bounds=ScreenBounds();var pos=bounds.Location;var monitor=Screen.FromRectangle(bounds);
            File.WriteAllLines(Path.Combine(profile,"window.txt"),new[]{pos.X.ToString(),pos.Y.ToString(),preferDock?"desktop":"window",pinned?"pinned":"normal",sizeMode,anchorX.ToString(),monitor.DeviceName,monitor.WorkingArea.X.ToString(),monitor.WorkingArea.Y.ToString()});
        } catch(IOException) {}
    }
    void RoundCorners()
    {
        using(var path=new System.Drawing.Drawing2D.GraphicsPath()) {
            int d=Math.Min(Math.Min(Width,Height),(int)(32*scale));
            if(d<=0)return;
            path.AddArc(0,0,d,d,180,90);path.AddArc(Width-d,0,d,d,270,90);
            path.AddArc(Width-d,Height-d,d,d,0,90);path.AddArc(0,Height-d,d,d,90,90);
            path.CloseFigure();Region=new Region(path);
        }
    }
    int ModeWidth(Rectangle area)
    {
        if(sizeMode=="wide")return area.Width;
        if(sizeMode=="half")return area.Width/2;
        return Math.Min(area.Width,(int)Math.Round(472*scale));
    }
    // Keep a monitor-wide rendered surface behind the visible window. Growing a
    // WebView controller on every tick exposes its blank backing store on Windows.
    void LayoutSurface()
    {
        int width=Math.Max(ClientSize.Width,Screen.FromRectangle(IsHandleCreated?ScreenBounds():Bounds).WorkingArea.Width);
        view.SetBounds((ClientSize.Width-width)/2,0,width,ClientSize.Height);
        Post(new {type="viewport",fraction=(double)ClientSize.Width/width});
    }
    Rectangle ScreenBounds() {return Desktop.Bounds(Handle);}
    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if(message.Msg==0x47 && !changingMode && lyrics!=null && !lyrics.IsDisposed)lyrics.SetBoundsFromShelf();
        if(verificationScreen!=null && (message.Msg==0x47 || message.Msg==0x18) && Desktop.IsWindowVisible(message.HWnd)){
            visiblePositions++;
            if(!verificationScreen.Bounds.Contains(Desktop.Bounds(message.HWnd)))positionsOutsideMonitor++;
        }
    }
    void AnimateWindow(Rectangle target,int duration,bool release)
    {
        windowTimer.Stop();motionStart=ScreenBounds();motionEnd=target;motionDuration=duration;settling=release;
        if(duration==0 || motionStart==target){Desktop.Place(Handle,desktopParent,target);SavePosition();return;}
        windowClock.Restart();windowTimer.Start();
    }
    void TickWindow()
    {
        double t=Math.Min(1,windowClock.Elapsed.TotalMilliseconds/motionDuration);
        double mix=settling?1-Math.Pow(1-t,3):t*t*(3-2*t);
        Func<int,int,int> blend=(a,b)=>a+(int)Math.Round((b-a)*mix);
        var next=new Rectangle(blend(motionStart.X,motionEnd.X),blend(motionStart.Y,motionEnd.Y),blend(motionStart.Width,motionEnd.Width),motionEnd.Height);
        Desktop.Place(Handle,desktopParent,next);
        if(t>=1){windowTimer.Stop();SavePosition();}
    }
    void ChangeWidth(string mode,bool instant)
    {
        if(mode!="classic" && mode!="half" && mode!="wide") return;
        var current=ScreenBounds();var area=Screen.FromRectangle(current).WorkingArea;
        sizeMode=mode;
        int w=ModeWidth(area),x=Math.Max(area.Left,Math.Min(anchorX-w/2,area.Right-w));
        AnimateWindow(new Rectangle(x,current.Y,w,current.Height),instant?0:300,false);
        Post(new {type="size",mode=sizeMode});
    }
    void SetDisplayMode()
    {
        changingMode=true;
        try{
            if(desktopParent!=IntPtr.Zero && (pinned || !preferDock)) {
                if(Desktop.Detach(Handle))desktopParent=IntPtr.Zero;
            }
            if(desktopParent==IntPtr.Zero)Desktop.SetPinned(Handle,pinned);
            if(preferDock && !pinned && desktopParent==IntPtr.Zero) DockToDesktop();
        }finally{changingMode=false;}
        if(lyrics!=null)lyrics.SetMode(pinned,desktopParent!=IntPtr.Zero);
        Post(new {type="dock",active=desktopParent!=IntPtr.Zero,pinned});
    }
    void Fail(Exception ex)
    {
        File.WriteAllText(Path.Combine(profile,"error.txt"),ex.ToString());
        if(!verify) MessageBox.Show(ex.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Error);
        Environment.ExitCode=1; Close();
    }
    async Task Initialize()
    {
        var env=await CoreWebView2Environment.CreateAsync(null,Path.Combine(profile,verify?"VerifyWebView":"WebView"),new CoreWebView2EnvironmentOptions { Language=Edition.Locale });
        await view.EnsureCoreWebView2Async(env);
        var core=view.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled=false;
        core.Settings.AreDevToolsEnabled=false;
        core.Settings.IsZoomControlEnabled=false;
        core.Settings.AreBrowserAcceleratorKeysEnabled=false;
        core.SetVirtualHostNameToFolderMapping("shelf.local",Path.Combine(baseDir,"app"),CoreWebView2HostResourceAccessKind.DenyCors);
        if(Directory.Exists(Library.Covers)) core.SetVirtualHostNameToFolderMapping("art.local",Library.Covers,CoreWebView2HostResourceAccessKind.DenyCors);
        core.NavigationStarting+=(s,e)=> { if(e.Uri!="https://shelf.local/index.html") e.Cancel=true; };
        core.NewWindowRequested+=(s,e)=>e.Handled=true;
        core.WebMessageReceived+=async (s,e)=>{ if(e.Source!="https://shelf.local/index.html") return;
            try { await Command(e.TryGetWebMessageAsString()); } catch(Exception ex) { Notice(ex.Message); } };
        core.NavigationCompleted+=async (s,e)=>{
            try {
                if(!e.IsSuccess) throw new IOException(Edition.Text("无法载入唱片架界面：","Could not load Self Coverflow. ")+e.WebErrorStatus);
                ready=true;
                LayoutSurface();
                SetDisplayMode();
                Post(new {type="size",mode=sizeMode});
                if(!verify)Playback.Changed=QueueMediaUpdate;
                await Reload(true); await PollMedia();
                if(verify) { await VerifyUI(); return; }
                timer.Tick+=async delegate { if(!dragging) { await Reload(false); await PollMedia(); } }; timer.Start();
            } catch(Exception ex){ Fail(ex); }
        };
        core.Navigate("https://shelf.local/index.html");
    }
    void Post(object data) { if(ready && !IsDisposed && !Disposing) view.CoreWebView2.PostWebMessageAsJson(json.Serialize(data)); }
    void Notice(string message) { Post(new {type="notice",message}); }
    void DockToDesktop() {
        desktopParent=Desktop.Attach(Handle);
    }
    void MoveWithSnap(Point raw,Rectangle area)
    {
        int gx,gy,w=ModeWidth(area);
        int x=Desktop.Attract(raw.X,area.Left,area.Right,w,snapDistance,out gx);
        int y=Desktop.Attract(raw.Y,area.Top,area.Bottom,Height,snapDistance,out gy);
        if(sizeMode=="wide")x=gx=area.Left;
        landing=new Point(gx,Math.Max(area.Top,Math.Min(area.Bottom-Height,gy)));
        if(sizeMode!="wide")anchorX=gx+w/2;
        Desktop.Place(Handle,desktopParent,new Rectangle(x,y,w,Height));
    }
    async Task Reload(bool force)
    {
        if(reading) return;
        reading=true;
        try {
            long db=File.GetLastWriteTimeUtc(Library.Database).Ticks, art=Directory.GetLastWriteTimeUtc(Library.Covers).Ticks;
            if(!force && db==dbStamp && art==artStamp) return;
            var next=await Task.Run(()=>Library.Read());
            albums=next; dbStamp=db; artStamp=art;
            Post(new {type="library",albums});
        } catch(Exception ex) {
            if(force || albums.Count>0) Post(new {type="libraryError",message=File.Exists(Library.Database)?ex.Message:Edition.Text("请先在本机 Apple Music 中打开并同步资料库。","Open Apple Music on this PC and sync your library.")});
        } finally {reading=false;}
    }
    int mediaQueued;
    void QueueMediaUpdate(){
        if(Interlocked.Exchange(ref mediaQueued,1)!=0)return;
        try{BeginInvoke((Action)(async delegate{
            Interlocked.Exchange(ref mediaQueued,0);
            if(!IsDisposed && !Disposing)await PollMedia();
        }));}catch(InvalidOperationException){}
    }
    async Task PollMedia(bool confirm=false)
    {
        if(polling) return; polling=true;
        try {
            MediaState state;
            try {state=await Playback.State(albums);} catch {state=new MediaState();}
            songTitle=state.title; if(lyrics!=null)lyrics.SetSong(songTitle);
            var current=json.Serialize(state);
            if(current!=lastMedia) {lastMedia=current; Post(new {type="media",state});}
            else if(confirm)Post(new {type="playbackCheck",playing=state.playing,id=state.albumId});
        } finally {polling=false;}
    }
    async Task Command(string cmd)
    {
        if(cmd=="syncPlayback"){await PollMedia(true);return;}
        if(verify && (cmd=="toggle" || cmd.StartsWith("resume:",StringComparison.Ordinal))){verifiedToggles++;return;}
        if(verify && cmd.StartsWith("open:",StringComparison.Ordinal)){verifiedOpens++;return;}
        if(cmd.StartsWith("browse:",StringComparison.Ordinal)) {
            long sequence;
            if(long.TryParse(cmd.Substring(7),out sequence)) {
                browseSequence=sequence; browseTimer.Stop(); browseTimer.Start();
            }
            return;
        }
        if(cmd=="lyrics" || cmd=="lyrics:instant"){
            if(lyrics==null){
                lyrics=new LyricsPane(ScreenBounds,scale,cmd=="lyrics:instant");
                lyrics.FormClosed+=delegate{lyrics=null;};
                lyrics.SetSong(songTitle);lyrics.Start(pinned,desktopParent!=IntPtr.Zero);
            }else{lyrics.SetBoundsFromShelf();}
            return;
        }
        if(cmd=="close"){Close();return;}
        if(cmd.StartsWith("size:",StringComparison.Ordinal)){
            var args=cmd.Split(':');ChangeWidth(args[1],args.Length>2 && args[2]=="instant");return;
        }
        if(cmd=="reload"){await Reload(true);return;}
        if(cmd=="pin"){pinned=!pinned;SetDisplayMode();SavePosition();return;}
        if(cmd=="dock"){preferDock=desktopParent==IntPtr.Zero;pinned=false;SetDisplayMode();SavePosition();return;}
        if(cmd=="dragStart"){
            if(windowTimer.Enabled && !settling) return;
            windowTimer.Stop();dragging=true;dragCursor=Cursor.Position;dragOrigin=landing=ScreenBounds().Location;return;
        }
        if(cmd.StartsWith("dragEnd",StringComparison.Ordinal)){if(dragging){dragging=false;AnimateWindow(new Rectangle(landing,Size),cmd=="dragEnd:instant"?0:167,true);}return;}
        if(cmd=="drag" && dragging){
            var c=Cursor.Position;var area=Screen.FromPoint(c).WorkingArea;
            MoveWithSnap(new Point(dragOrigin.X+c.X-dragCursor.X,dragOrigin.Y+c.Y-dragCursor.Y),area);return;
        }
        bool toggle=cmd=="toggle";
        var fields=cmd.Split(new[]{':'},3);
        int serial=0;
        if(!toggle && (fields.Length<2 || (fields[0]!="open" && fields[0]!="resume") || !int.TryParse(fields[1],out serial)))return;
        int token=++request;
        bool played=false;
        string failure=Edition.Text("无法在 Apple Music 中开始播放。","Apple Music could not start playback.");
        await transportCommands.WaitAsync();
        try{
            if(token!=request)return;
            if(toggle)played=await Playback.Toggle();
            else if(fields[0]=="resume")played=await Playback.Resume();
            else if(fields.Length==3){
                var a=albums.FirstOrDefault(item=>item.id==fields[2]);
                if(a!=null){
                    var state=await Playback.State(albums,false);
                    if(token!=request)return;
                    if(state.albumId==a.id)played=state.playing || await Playback.Resume();
                    else if(!await Playback.PauseForSwitch(()=>token!=request)){
                        failure=Edition.Text("Apple Music 尚未暂停当前曲目，请重试。","Apple Music has not paused the current track. Try again.");
                    }else if(token==request){
                        bool unique=albums.Count(item=>String.Equals(item.title,a.title,StringComparison.OrdinalIgnoreCase))==1;
                        played=await Task.Run(()=>MusicClient.Play(a,unique,()=>Volatile.Read(ref request)!=token));
                    }
                }
            }
        }catch(Exception ex){Debug.WriteLine(ex);}
        finally{transportCommands.Release();}
        if(token!=request || IsDisposed)return;
        if(!played){
            if(toggle)Notice(Edition.Text("Apple Music 当前没有可控制的播放会话。","Play a song in Apple Music to connect playback controls."));
            else Post(new {type="playFailed",serial,message=failure});
        }
        await PollMedia(true);
    }
    async Task<bool> VerifySearch()
    {
        var core=view.CoreWebView2;
        int opens=verifiedOpens,toggles=verifiedToggles;
        string ready=await core.ExecuteScriptAsync(@"(()=>{
            if(mode!=='favorites')toggleFilter();
            $('searchToggle').click();
            query.value='__not_a_real_album_92f23__';query.dispatchEvent(new Event('input'));
            if($('searchEmpty').hidden || document.activeElement!==query)return false;
            const album=all.find(a=>!a.favorite && a.id!==playingCard?.id && searchable(a.title)) || all.find(a=>searchable(a.title));
            if(!album)return false;
            query.value=album.title;query.dispatchEvent(new Event('input'));
            window.testSearchId=results[0]?.album.id;
            return !!testSearchId && !$('searchPanel').hidden && results.every(r=>r.album.title);
        })()");
        if(ready!="true")return false;
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent","{\"type\":\"keyDown\",\"key\":\"Enter\",\"code\":\"Enter\",\"windowsVirtualKeyCode\":13}");
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent","{\"type\":\"keyUp\",\"key\":\"Enter\",\"code\":\"Enter\",\"windowsVirtualKeyCode\":13}");
        // Wait past the ordinary idle return and exercise a real metadata refresh.
        await Task.Delay(3700);
        await core.ExecuteScriptAsync("updateList(true,true)");
        bool landed=await core.ExecuteScriptAsync("searchSelection===testSearchId && !motion?.following && !lookup.classList.contains('open') && visible[wrap(Math.round(cursor),visible.length)].id===testSearchId && !document.getElementById('scrub')")=="true";
        return landed && verifiedOpens==opens && verifiedToggles==toggles && Height==(int)Math.Round(306*scale);
    }
    async Task<bool> VerifyDrag(int[] point,int distance,bool cancel=false)
    {
        var core=view.CoreWebView2;
        bool isolated=false;
        foreach(var action in new[]{"mousePressed","mouseMoved","mouseReleased"}){
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",json.Serialize(new {
                type=action,x=point[0],y=point[1]+(action=="mousePressed"?0:distance),
                button="left",buttons=action=="mouseReleased"?0:1,clickCount=1
            }));
            if(action=="mouseMoved"){
                isolated=await core.ExecuteScriptAsync("document.querySelectorAll('.cover.pulling').length===1 && pulledCard?.dataset.id===testPullId && !covers.style.transform")=="true";
                if(cancel)await core.ExecuteScriptAsync("stage.dispatchEvent(new PointerEvent('pointercancel',{pointerId}))");
            }
        }
        return isolated;
    }
    async Task VerifyUI()
    {
        await Task.Delay(800);
        var core=view.CoreWebView2;
        string result=await core.ExecuteScriptAsync("window.shelfCheck()");
        await core.ExecuteScriptAsync("if(!playingCard)playingCard=visible[0];stopRoll();cursor=index=visible.findIndex(a=>a.id===playingCard.id);draw();step(4)");
        await Task.Delay(1450);
        await core.ExecuteScriptAsync("step(1)");
        await Task.Delay(1450);
        bool held=await core.ExecuteScriptAsync("visible[index].id!==playingCard.id")=="true";
        await Task.Delay(1300);
        bool rolling=await core.ExecuteScriptAsync("reducedMotion.matches || (!!motion?.following && Math.abs(cursor-Math.round(cursor))>.0001)")=="true";
        await Task.Delay(1400);
        bool returned=await core.ExecuteScriptAsync("!motion && visible[index].id===playingCard.id")=="true";
        string followed=await core.ExecuteScriptAsync("playingCard?.title || null");
        await core.ExecuteScriptAsync("step(6)");
        await Task.Delay(500);
        await core.ExecuteScriptAsync("browsingActive=false;follow()");
        await Task.Delay(180);
        bool interrupted=await core.ExecuteScriptAsync("browsing();!motion?.following")=="true";
        await core.ExecuteScriptAsync("browsingActive=false;follow()");
        await core.ExecuteScriptAsync("const avail=media.available;media.available=false;$('transport').click();media.available=true;$('transport').click();media.available=avail");
        await core.ExecuteScriptAsync("document.getElementById('pin').click()");
        await Task.Delay(180);
        bool onTop=desktopParent==IntPtr.Zero && Desktop.IsTopmost(Handle);
        await core.ExecuteScriptAsync("document.getElementById('pin').click()");
        await Task.Delay(180);
        var origin=ScreenBounds().Location;var area=Screen.FromPoint(origin).WorkingArea;
        int fixedHeight=Height,classicWidth=Width,surfaceWidth=view.Width;
        await core.ExecuteScriptAsync("document.querySelector('[data-size=wide]').click()");
        await Task.Delay(110);
        bool widening=Width>classicWidth && Width<area.Width && Height==fixedHeight;
        bool stableSurface=view.Width==surfaceWidth && surfaceWidth>=area.Width && Math.Abs(2*view.Left+view.Width-ClientSize.Width)<=1;
        await Task.Delay(450);
        bool wide=Width==area.Width && ScreenBounds().Location.X==area.X && Height==fixedHeight;
        await core.ExecuteScriptAsync("document.querySelector('[data-size=half]').click()");
        await Task.Delay(490);
        bool half=Width==area.Width/2 && Height==fixedHeight;
        await core.ExecuteScriptAsync("document.querySelector('[data-size=classic]').click()");
        await Task.Delay(490);
        bool classic=Width==classicWidth && Height==fixedHeight;
        var center=new Point(area.Left+(area.Width-Width)/2,area.Top+(area.Height-Height)/2);
        MoveWithSnap(new Point(center.X+3,center.Y-3),area);
        AnimateWindow(new Rectangle(landing,Size),0,true);
        bool centered=ScreenBounds().Location==center;
        MoveWithSnap(new Point(center.X,area.Bottom-Height+5),area);
        AnimateWindow(new Rectangle(landing,Size),0,true);
        bool atEdge=ScreenBounds().Location==new Point(center.X,area.Bottom-Height);
        MoveWithSnap(new Point(center.X,area.Bottom-Height-snapDistance*2),area);
        bool released=ScreenBounds().Location.Y==area.Bottom-Height-snapDistance*2;
        MoveWithSnap(new Point(center.X,area.Top+snapDistance),area);
        int pulledY=ScreenBounds().Location.Y;
        bool softTop=pulledY>area.Top && pulledY<area.Top+snapDistance && landing.Y==area.Top;
        AnimateWindow(new Rectangle(landing,Size),167,true);
        await Task.Delay(240);
        bool top=ScreenBounds().Location.Y==area.Top;
        Desktop.Move(Handle,desktopParent,origin);
        bool transport=verifiedToggles==2 && verifiedOpens==0;
        bool reduced=await core.ExecuteScriptAsync("reducedMotion.matches")=="true";
        string clicks=await core.ExecuteScriptAsync("(()=>{const c=posed.find(p=>all.some(a=>a.id===p.card.dataset.id))?.card;if(!c)return false;c.click();c.dispatchEvent(new MouseEvent('dblclick',{bubbles:true}));return true})()");
        await Task.Delay(120);
        bool doubleClick=clicks=="true" && verifiedOpens==1;
        var point=json.Deserialize<int[]>(await core.ExecuteScriptAsync(@"(()=>{
            endPending();finishWheel();stopRoll();cursor=index=Math.floor(visible.length/2);draw();
            const r=stage.getBoundingClientRect(),y=Math.round(r.y+r.height*.48);
            for(let x=Math.round(r.x+r.width/2+5);x<r.right-3;x+=3){
                const card=document.elementFromPoint(x,y)?.closest('.cover');
                if(card && card.dataset.id!==visible[index].id && card.dataset.id!==playingCard?.id){
                    window.testPullId=card.dataset.id;return [x,y];
                }
            }
            return [];
        })()"));
        bool cancelledPull=await VerifyDrag(point,-58,true) && verifiedOpens==1;
        bool isolatedPull=await VerifyDrag(point,58);
        await Task.Delay(750);
        bool pullCentered=await core.ExecuteScriptAsync("visible[wrap(Math.round(cursor),visible.length)].id===testPullId && pendingPlay?.id===testPullId && $('status').classList.contains('pending')")=="true" && verifiedOpens==2;
        Post(new {type="playbackCheck",playing=true,id="unrelated-test-album"});
        await Task.Delay(60);
        bool waiting=await core.ExecuteScriptAsync("!!pendingPlay")=="true";
        string playId=json.Deserialize<string>(await core.ExecuteScriptAsync("testPullId"));
        Post(new {type="playbackCheck",playing=true,id=playId});
        await Task.Delay(60);
        bool confirmed=await core.ExecuteScriptAsync("!pendingPlay && !$('status').classList.contains('pending')")=="true";
        var centerPoint=json.Deserialize<int[]>(await core.ExecuteScriptAsync("(()=>{const r=nodes.get(testPullId).getBoundingClientRect();return [Math.round(r.x+r.width/2),Math.round(r.y+r.height/2)]})()"));
        bool upCenter=await VerifyDrag(centerPoint,-58);
        await Task.Delay(120);
        upCenter=upCenter && verifiedOpens==3 && await core.ExecuteScriptAsync("pendingPlay?.id===testPullId")=="true";
        await core.ExecuteScriptAsync("endPending();window.testPullId=document.elementFromPoint("+point[0]+","+point[1]+").closest('.cover').dataset.id");
        bool upSide=await VerifyDrag(point,-58);
        await Task.Delay(750);
        upSide=upSide && verifiedOpens==4 && await core.ExecuteScriptAsync("pendingPlay?.id===testPullId && visible[wrap(Math.round(cursor),visible.length)].id===testPullId")=="true";
        await core.ExecuteScriptAsync("endPending()");
        bool paneOpened=lyrics!=null && lyrics.Visible;
        await Command("pin");
        bool sharedPin=paneOpened && lyrics.IsPinned && Desktop.IsTopmost(Handle);
        await Command("pin");
        bool sharedUnpin=paneOpened && !lyrics.IsPinned && lyrics.Coupled;
        if(lyrics!=null)lyrics.Close();
        bool search=await VerifySearch();
        var checks=new {stableSurface,cancelledPull,upCenter,upSide,search,isolatedPull,pullCentered,waiting,confirmed,doubleClick,paneOpened,sharedPin,sharedUnpin,gallery=json.DeserializeObject(result),monitor=verificationScreen.DeviceName,visiblePositions,positionsOutsideMonitor,held,rolling,returned,interrupted,followed=json.DeserializeObject(followed),onTop,restored=desktopParent!=IntPtr.Zero,centered,atEdge,released,softTop,top,widening,wide,half,classic,transport};
        File.WriteAllText(Path.Combine(profile,"ui-check.json"),json.Serialize(checks));
        using(var fs=File.Create(Path.Combine(profile,"preview.png"))) await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,fs);
        if(!isolatedPull || !pullCentered || !waiting || !confirmed || !doubleClick || !paneOpened || !sharedPin || !sharedUnpin || positionsOutsideMonitor!=0 || visiblePositions==0 || !result.Contains("\"passed\":true") || desktopParent==IntPtr.Zero || !held || !rolling || !returned || !interrupted || !onTop || !centered || !atEdge || !released || !softTop || !top || !(widening||reduced) || !wide || !half || !classic || !transport) Environment.ExitCode=2;
        if(!search || !stableSurface || !cancelledPull || !upCenter || !upSide)Environment.ExitCode=2;
        Close();
    }
}

internal static class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [STAThread] static void Main(string[] args)
    {
        if(args.Contains("--watch-music")) { AppLaunch.WatchMusic(); return; }
        bool verify=args.Contains("--verify-ui");
        SetProcessDPIAware(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        bool first;
        using(var m=new Mutex(true,verify?"Local\\CoverShelfUiCheck"+Edition.Suffix:AppLaunch.WidgetMutex,out first)){
            if(!first)return;
            if(!verify && !args.Contains("--from-music")) try { AppLaunch.OpenMusic(); }
            catch(Exception ex) { MessageBox.Show(Edition.Text("本机 Apple Music 未能启动。\n","Could not start Apple Music.\n")+ex.Message,"Self Coverflow",MessageBoxButtons.OK,MessageBoxIcon.Warning); }
            Application.Run(new Shelf(verify));
        }
    }
}

