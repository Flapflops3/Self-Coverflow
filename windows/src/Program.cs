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
    string lastRunningPlayers;
    DateTime nextPlayerScan,nextInstallScan;
    HashSet<string> registeredPlayers=new HashSet<string>();
    string[] installedPlayers=new string[0];
    bool scanningPlayers;
    async Task RefreshRunningPlayers()
    {
        if(scanningPlayers || DateTime.UtcNow<nextPlayerScan)return;
        scanningPlayers=true;
        try {
        nextPlayerScan=DateTime.UtcNow.AddSeconds(4);
            var players=await Task.Run(()=>{
                var running=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var process in Process.GetProcesses()){
                    using(process){
                        try{running.Add(process.ProcessName);}
                        catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
                    }
                }
                var result=new List<string>();
                if(running.Contains("AppleMusic"))result.Add("apple");
                if(running.Contains("Spotify"))result.Add("spotify");
                if(running.Contains("QQMusic"))result.Add("qq");
                if(running.Contains("KuGou") || running.Contains("KGMusic"))result.Add("kugou");
                return result;
            });
            if(DateTime.UtcNow>=nextInstallScan){
                nextInstallScan=DateTime.UtcNow.AddSeconds(30);
                try{registeredPlayers=await Task.Run(()=>InstalledPlayers.Discover());}
                catch(Exception ex){Debug.WriteLine(ex);nextInstallScan=DateTime.UtcNow.AddSeconds(5);}
            }
            ApplyPlayerInventory(registeredPlayers,players);
        } finally {scanningPlayers=false;}
    }

    void ApplyPlayerInventory(HashSet<string> registered,List<string> players)
    {
        installedPlayers=new[]{"apple","spotify","qq","kugou"}.Where(p=>registered.Contains(p) || players.Contains(p)).ToArray();
        string key=string.Join(",",players)+"|"+string.Join(",",installedPlayers);
        if(key==lastRunningPlayers)return;
        lastRunningPlayers=key;
        if(!verify && !installedPlayers.Contains(source))
            SelectSource(players.FirstOrDefault() ?? installedPlayers.FirstOrDefault() ?? "");
        Post(new {type="runningPlayers",players,installed=installedPlayers});
    }
    string source;
    int sourceRevision;
    internal static bool ValidSource(string value){return value=="apple" || value=="spotify" || value=="qq" || value=="kugou";}
    internal static string SavedSource(){
        var file=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),Edition.Profile,"source.txt");
        try{if(File.Exists(file)){var value=File.ReadAllText(file).Trim();if(ValidSource(value))return value;}}
        catch(IOException){}catch(UnauthorizedAccessException){}
        return "apple";
    }
    List<Album> albums=new List<Album>();
    bool reading,polling,ready,dragging;
    bool pinned;
    LyricsPane lyrics;
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
    string albumQueueId;
    Point dragCursor,dragOrigin;
    long dbStamp=-1,artStamp=-1;
    string lastMedia="";
    protected override bool ShowWithoutActivation { get { return true; } }
    public Shelf(bool verifyUI)
    {
        verify=verifyUI;
        source=verify?"apple":SavedSource();
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
            File.WriteAllLines(Path.Combine(profile,"window.txt"),new[]{pos.X.ToString(),pos.Y.ToString(),"window",pinned?"pinned":"normal",sizeMode,anchorX.ToString(),monitor.DeviceName,monitor.WorkingArea.X.ToString(),monitor.WorkingArea.Y.ToString()});
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
        if(message.Msg==0x47 && lyrics!=null && !lyrics.IsDisposed)lyrics.SetBoundsFromShelf();
        if(verificationScreen!=null && (message.Msg==0x47 || message.Msg==0x18) && Desktop.IsWindowVisible(message.HWnd)){
            visiblePositions++;
            if(!verificationScreen.Bounds.Contains(Desktop.Bounds(message.HWnd)))positionsOutsideMonitor++;
        }
    }
    void AnimateWindow(Rectangle target,int duration,bool release)
    {
        windowTimer.Stop();motionStart=ScreenBounds();motionEnd=target;motionDuration=duration;settling=release;
        if(duration==0 || motionStart==target){Desktop.Place(Handle,target);SavePosition();return;}
        windowClock.Restart();windowTimer.Start();
    }
    void TickWindow()
    {
        double t=Math.Min(1,windowClock.Elapsed.TotalMilliseconds/motionDuration);
        double mix=settling?1-Math.Pow(1-t,3):t*t*(3-2*t);
        Func<int,int,int> blend=(a,b)=>a+(int)Math.Round((b-a)*mix);
        var next=new Rectangle(blend(motionStart.X,motionEnd.X),blend(motionStart.Y,motionEnd.Y),blend(motionStart.Width,motionEnd.Width),motionEnd.Height);
        Desktop.Place(Handle,next);
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
        Desktop.SetPinned(Handle,pinned);
        if(lyrics!=null)lyrics.SetMode(pinned,this);
        Post(new {type="pin",pinned});
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
                Post(new {type="source",source});
                await RefreshRunningPlayers(); await Reload(true); await PollMedia();
                if(verify) { await VerifyUI(); return; }
                timer.Tick+=async delegate { if(!dragging) { await RefreshRunningPlayers(); await Reload(false); await PollMedia(); } }; timer.Start();
            } catch(Exception ex){ Fail(ex); }
        };
        core.Navigate("https://shelf.local/index.html");
    }
    void Post(object data) { if(ready && !IsDisposed && !Disposing) view.CoreWebView2.PostWebMessageAsJson(json.Serialize(data)); }
    void Notice(string message) { Post(new {type="notice",message}); }
    void MoveWithSnap(Point raw,Rectangle area)
    {
        int gx,gy,w=ModeWidth(area);
        int x=Desktop.Attract(raw.X,area.Left,area.Right,w,snapDistance,out gx);
        int y=Desktop.Attract(raw.Y,area.Top,area.Bottom,Height,snapDistance,out gy);
        if(sizeMode=="wide")x=gx=area.Left;
        landing=new Point(gx,Math.Max(area.Top,Math.Min(area.Bottom-Height,gy)));
        if(sizeMode!="wide")anchorX=gx+w/2;
        Desktop.Place(Handle,new Rectangle(x,y,w,Height));
    }
    async Task Reload(bool force)
    {
        if(reading || source!="apple") return;
        reading=true;
        int revision=sourceRevision;
        try {
            long db=File.GetLastWriteTimeUtc(Library.Database).Ticks, art=Directory.GetLastWriteTimeUtc(Library.Covers).Ticks;
            if(!force && db==dbStamp && art==artStamp) return;
            var next=await Task.Run(()=>Library.Read());
            if(revision!=sourceRevision)return;
            albums=next; dbStamp=db; artStamp=art;
            Post(new {type="library",albums});
        } catch(Exception ex) {
            if(revision==sourceRevision && (force || albums.Count>0)) Post(new {type="libraryError",message=File.Exists(Library.Database)?ex.Message:Edition.Text("请先在本机 Apple Music 中打开并同步资料库。","Open Apple Music on this PC and sync your library.")});
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
        if(polling || source.Length==0) return; polling=true;
        int revision=sourceRevision;
        try {
            MediaState state;
            try {state=source=="apple"?await Playback.State(albums):await Playback.LocalState(source);} catch {state=new MediaState();}
            if(revision!=sourceRevision)return;
            if(albumQueueId!=state.albumId)albumQueueId=null;
            songTitle=state.title; if(lyrics!=null)lyrics.SetSong(songTitle);
            var current=json.Serialize(state);
            if(current!=lastMedia) {lastMedia=current; Post(new {type="media",state});}
            else if(confirm)Post(new {type="playbackCheck",playing=state.playing,id=state.albumId});
        } finally {polling=false;}
    }
    async Task<bool> WaitForTrack(Album album,int index,Func<bool> cancelled)
    {
        for(int i=0;i<35 && !cancelled();i++){
            await Task.Delay(100);
            var state=await Playback.State(albums,false);
            if(state.albumId==album.id && state.playing && album.TrackIndex(state.title,state.artist)==index)return true;
        }
        return false;
    }
    async Task<bool> StepAlbum(Album album,bool unique,MediaState state,int target,Func<bool> cancelled)
    {
        int current=album.TrackIndex(state.title,state.artist);
        if(await Task.Run(()=>MusicClient.AlbumMenuAvailable() && !cancelled() &&
            MusicClient.PlayTrack(album,unique,album.tracks[target],cancelled))){
            return await WaitForTrack(album,target,cancelled);
        }
        if(cancelled())return false;
        // Transport buttons operate on the playback queue. Establish an album queue
        // before using them when playback came from outside the shelf or must wrap.
        if(albumQueueId!=album.id || Math.Abs(current-target)!=1){
            if(!await Task.Run(()=>MusicClient.Play(album,unique,cancelled)) ||
                !await WaitForTrack(album,0,cancelled))return false;
            current=0;
        }
        if(current!=target && state.paused && !cancelled() && !await Playback.Resume())return false;
        while(current!=target && !cancelled()){
            bool forward=target>current;
            double before=forward?-1:await Playback.TrackPosition();
            if(!await Task.Run(()=>MusicClient.StepPlayer(forward,cancelled)))return false;
            if(!forward && before>=2){
                // Music interprets Back as rewind once the song is under way.
                // Press again only after observing that rewind on the same track.
                for(int i=0;i<10 && !cancelled();i++){
                    await Task.Delay(90);
                    var seen=await Playback.State(albums,false);
                    if(seen.albumId!=album.id || album.TrackIndex(seen.title,seen.artist)!=current)break;
                    double position=await Playback.TrackPosition();
                    if(position>=0 && position<=1){
                        await Task.Delay(180);
                        seen=await Playback.State(albums,false);
                        if(seen.albumId!=album.id || album.TrackIndex(seen.title,seen.artist)!=current)break;
                        if(!await Task.Run(()=>MusicClient.StepPlayer(false,cancelled)))return false;
                        break;
                    }
                }
            }
            current+=forward?1:-1;
            if(!await WaitForTrack(album,current,cancelled))return false;
        }
        return !cancelled();
    }
    async Task<bool> AlbumTransport(string cmd,int token)
    {
        Func<bool> cancelled=()=>Volatile.Read(ref request)!=token || IsDisposed;
        var state=await Playback.State(albums,false);
        var album=albums.FirstOrDefault(a=>a.id==state.albumId);
        if(album==null || cancelled())return false;
        bool unique=albums.Count(a=>String.Equals(a.title,album.title,StringComparison.OrdinalIgnoreCase))==1;
        if(cmd=="repeatOne" || cmd=="repeatOff")
            return await Task.Run(()=>MusicClient.SetRepeat(cmd=="repeatOne"?"one":"off",cancelled));
        bool repeat=cmd=="repeatAlbum";
        var target=album.RelativeTrack(state.title,state.artist,repeat?0:cmd=="nextTrack"?1:-1);
        if(target==null)return false;
        if(!repeat){
            bool ok=await StepAlbum(album,unique,state,album.tracks.IndexOf(target),cancelled);
            if(!cancelled())albumQueueId=ok?album.id:null;
            return ok;
        }
        // Repeat All loops the native queue. Establish the album queue at the
        // current track using the same background path as the navigation buttons.
        if(albumQueueId!=album.id){
            double position=await Playback.TrackPosition();
            if(cancelled() || !await StepAlbum(album,unique,state,album.tracks.IndexOf(target),cancelled) || cancelled())return false;
            if(state.paused && !await Playback.PauseForSwitch(cancelled))return false;
            if(position>=0 && !cancelled())await Task.Run(()=>MusicClient.Scrubber(position));
            if(cancelled())return false;
            albumQueueId=album.id;
        }
        return await Task.Run(()=>MusicClient.SetRepeat("album",cancelled));
    }

    void SelectSource(string value)
    {
        if(source==value)return;
        request++;sourceRevision++;source=value;
        if(!verify && value.Length>0)File.WriteAllText(Path.Combine(profile,"source.txt"),value);
        if(lyrics!=null)lyrics.Close();
        albums=new List<Album>();albumQueueId=null;dbStamp=artStamp=-1;lastMedia="";
        Post(new {type="source",source});
    }

    async Task Command(string cmd)
    {
        if(cmd.StartsWith("source:",StringComparison.Ordinal)){
            var value=cmd.Substring(7);
            if(!installedPlayers.Contains(value) || value==source)return;
            SelectSource(value);
            await Reload(true);await PollMedia();
            return;
        }
        if(source!="apple"){
            if(cmd.StartsWith("lyrics") || cmd.StartsWith("open:"))return;
            bool resume=cmd.StartsWith("resume:",StringComparison.Ordinal);
            if(cmd=="previousTrack" || cmd=="nextTrack" || cmd=="toggle" || resume || cmd=="repeatOne" || cmd=="repeatAlbum" || cmd=="repeatOff"){
                int revision=sourceRevision;
                bool ok=false;
                try{ok=await Playback.ControlLocal(source,resume?"resume":cmd=="toggle"?"pause":cmd);}
                catch(Exception ex){Debug.WriteLine(ex);}
                if(revision==sourceRevision){
                    await PollMedia(true);
                    if(revision==sourceRevision){
                        string message=ok?null:Edition.Text("播放器未接受操作。","The player did not accept the command.");
                        Post(new {type="albumCommandDone",message});
                        int localSerial;
                        if(resume && !ok && int.TryParse(cmd.Substring(7),out localSerial))Post(new {type="playFailed",serial=localSerial,message});
                    }
                }
                return;
            }
        }
        if(cmd=="previousTrack" || cmd=="nextTrack" || cmd=="repeatOne" || cmd=="repeatAlbum" || cmd=="repeatOff"){
            if(verify){Post(new {type="albumCommandDone"});return;}
            int albumToken=++request;
            string message=null;
            await transportCommands.WaitAsync();
            try{
                if(albumToken==request && !await AlbumTransport(cmd,albumToken))
                    message=Edition.Text("Apple Music 未确认本次专辑播放操作。","Apple Music could not confirm the album playback change.");
            }catch(Exception ex){Debug.WriteLine(ex);message=Edition.Text("Apple Music 播放控制暂不可用。","Apple Music playback controls are unavailable.");}
            finally{transportCommands.Release();}
            if(!IsDisposed){
                // An older poll may still carry the previous repeat mode.
                while(polling && !IsDisposed)await Task.Delay(30);
                if(!IsDisposed){await PollMedia(true);Post(new {type="albumCommandDone",message});}
            }
            return;
        }
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
                lyrics.SetSong(songTitle);lyrics.Start(pinned,this);
            }else{lyrics.ShowRequested();}
            return;
        }
        if(cmd=="close"){Close();return;}
        if(cmd.StartsWith("size:",StringComparison.Ordinal)){
            var args=cmd.Split(':');ChangeWidth(args[1],args.Length>2 && args[2]=="instant");return;
        }
        if(cmd=="reload"){await Reload(true);return;}
        if(cmd=="pin"){pinned=!pinned;SetDisplayMode();SavePosition();return;}
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
                        if(played && token==request)albumQueueId=a.id;
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
        bool onTop=Desktop.IsTopmost(Handle);
        await core.ExecuteScriptAsync("document.getElementById('pin').click()");
        await Task.Delay(180);
        bool unpinned=!Desktop.IsTopmost(Handle);
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
        Desktop.Move(Handle,origin);
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
        var checks=new {stableSurface,cancelledPull,upCenter,upSide,search,isolatedPull,pullCentered,waiting,confirmed,doubleClick,paneOpened,sharedPin,sharedUnpin,gallery=json.DeserializeObject(result),monitor=verificationScreen.DeviceName,visiblePositions,positionsOutsideMonitor,held,rolling,returned,interrupted,followed=json.DeserializeObject(followed),onTop,unpinned,centered,atEdge,released,softTop,top,widening,wide,half,classic,transport};
        File.WriteAllText(Path.Combine(profile,"ui-check.json"),json.Serialize(checks));
        using(var fs=File.Create(Path.Combine(profile,"preview.png"))) await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,fs);
        if(!isolatedPull || !pullCentered || !waiting || !confirmed || !doubleClick || !paneOpened || !sharedPin || !sharedUnpin || positionsOutsideMonitor!=0 || visiblePositions==0 || !result.Contains("\"passed\":true") || !unpinned || !held || !rolling || !returned || !interrupted || !onTop || !centered || !atEdge || !released || !softTop || !top || !(widening||reduced) || !wide || !half || !classic || !transport) Environment.ExitCode=2;
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
            if(!verify && Shelf.SavedSource()=="apple" && !args.Contains("--from-music")) try { AppLaunch.OpenMusic(); }
            catch(Exception ex) { Debug.WriteLine(ex); }
            Application.Run(new Shelf(verify));
        }
    }
}

