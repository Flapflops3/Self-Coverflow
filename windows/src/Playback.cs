using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media.Control;
using Windows.Storage.Streams;

internal sealed class MediaState
{
    public bool available, playing, paused, canPlay, canPause;
    public string title="", album="", artist="";
    public string albumId, artwork, repeatMode, albumArtist;
    public bool canStep, canRepeat;
    public bool? canNext,canPrevious;
}

internal static class Playback
{
    static Task<T> AsTask<T>(Windows.Foundation.IAsyncOperation<T> operation)
    {
        var result = new TaskCompletionSource<T>();
        operation.Completed = delegate(Windows.Foundation.IAsyncOperation<T> op, Windows.Foundation.AsyncStatus status)
        {
            try {
                if (status == Windows.Foundation.AsyncStatus.Canceled) result.TrySetCanceled();
                else result.TrySetResult(op.GetResults());
            } catch (Exception ex) { result.TrySetException(ex); }
        };
        return result.Task;
    }
    static GlobalSystemMediaTransportControlsSessionManager manager;
    static GlobalSystemMediaTransportControlsSession observed;
    internal static Action Changed;
    static void MediaChanged(GlobalSystemMediaTransportControlsSession s,MediaPropertiesChangedEventArgs args){Notify();}
    static void InfoChanged(GlobalSystemMediaTransportControlsSession s,PlaybackInfoChangedEventArgs args){Notify();}
    static void Notify(){var handler=Changed;if(handler!=null)handler();}
    static string artKey, artData;
    internal static Album MatchAlbum(List<Album> albums,MediaState s)
    {
        if(String.IsNullOrWhiteSpace(s.title)) return null;
        Func<string,string,bool> same=(a,b)=>!String.IsNullOrWhiteSpace(a) && String.Equals(a.Trim(),(b??"").Trim(),StringComparison.OrdinalIgnoreCase);
        var candidates=albums.Where(a=>same(a.title,s.album)).ToList();
        string key=Album.TrackKey(s.title,s.artist);
        var found=candidates.Where(a=>same(a.artist,s.artist) || a.songs.Contains(key)).ToList();
        if(found.Count==1) return found[0];
        if(candidates.Count==1) return candidates[0];
        if(!String.IsNullOrWhiteSpace(s.album)) return null;
        found=albums.Where(a=>a.songs.Contains(key)).Take(2).ToList();
        return found.Count==1?found[0]:null;
    }
    static async Task<string> ReadArtwork(IRandomAccessStreamReference reference)
    {
        if(reference==null) return null;
        using(var stream=await AsTask<IRandomAccessStreamWithContentType>(reference.OpenReadAsync())) {
            if(stream.Size==0 || stream.Size>4*1024*1024) return null;
            using(var reader=new DataReader(stream.GetInputStreamAt(0))) {
                uint size=await AsTask<uint>(reader.LoadAsync((uint)stream.Size));
                var data=new byte[size];reader.ReadBytes(data);
                if(size<8) return null;
                string mime=data[0]==0xff && data[1]==0xd8?"image/jpeg":data[0]==0x89 && data[1]==0x50?"image/png":null;
                return mime==null?null:"data:"+mime+";base64,"+Convert.ToBase64String(data);
            }
        }
    }
    internal static bool MatchesSource(string id,string source)
    {
        string name=(id??"").ToLowerInvariant();
        switch(source){
            case "spotify":return name.Contains("spotify");
            case "qq":return name.Contains("qqmusic") || name.Contains("qq音乐") || name.Contains("qq音樂");
            case "kugou":return name.Contains("kugou") || name.Contains("酷狗") || name.EndsWith("kgmusic.exe");
            case "apple":return name.StartsWith("appleinc.applemusicwin_");
            default:return false;
        }
    }
    static async Task<GlobalSystemMediaTransportControlsSession> Session(string source="apple")
    {
        if (manager==null) manager=await AsTask<GlobalSystemMediaTransportControlsSessionManager>(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
        var sessions=manager.GetSessions().Where(s=>MatchesSource(s.SourceAppUserModelId,source)).ToList();
        var session=sessions.Count<=1?sessions.FirstOrDefault():sessions.FirstOrDefault(s=>s.GetPlaybackInfo().PlaybackStatus==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)??sessions[0];
        if(Changed!=null && !Object.ReferenceEquals(session,observed)){
            if(observed!=null){observed.MediaPropertiesChanged-=MediaChanged;observed.PlaybackInfoChanged-=InfoChanged;}
            observed=session;
            if(session!=null){session.MediaPropertiesChanged+=MediaChanged;session.PlaybackInfoChanged+=InfoChanged;}
        }
        return session;
    }
    internal static async Task<MediaState> LocalState(string source)
    {
        var session=await Session(source);
        if(session==null)return new MediaState();
        var p=await AsTask<GlobalSystemMediaTransportControlsSessionMediaProperties>(session.TryGetMediaPropertiesAsync());
        var info=session.GetPlaybackInfo();
        var controls=info.Controls;
        return new MediaState {
            available=true,playing=info.PlaybackStatus==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            paused=info.PlaybackStatus==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            title=p.Title,album=p.AlbumTitle,albumArtist=p.AlbumArtist,artist=p.Artist,artwork=await ReadArtwork(p.Thumbnail),
            canPlay=controls.IsPlayEnabled || controls.IsPlayPauseToggleEnabled,
            canPause=controls.IsPauseEnabled || controls.IsPlayPauseToggleEnabled,canNext=controls.IsNextEnabled,
            canPrevious=controls.IsPreviousEnabled,canRepeat=controls.IsRepeatEnabled && info.AutoRepeatMode.HasValue,
            repeatMode=info.AutoRepeatMode==Windows.Media.MediaPlaybackAutoRepeatMode.Track?"one":
                info.AutoRepeatMode==Windows.Media.MediaPlaybackAutoRepeatMode.List?"album":"off"
        };
    }
    internal static async Task<bool> ControlLocal(string source,string command)
    {
        var session=await Session(source);
        if(session==null)return false;
        var info=session.GetPlaybackInfo();
        var c=info.Controls;
        switch(command){
            case "nextTrack":return c.IsNextEnabled && await AsTask<bool>(session.TrySkipNextAsync());
            case "previousTrack":return c.IsPreviousEnabled && await AsTask<bool>(session.TrySkipPreviousAsync());
            case "pause":
                if(info.PlaybackStatus==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)return true;
                return c.IsPauseEnabled?await AsTask<bool>(session.TryPauseAsync()):
                    c.IsPlayPauseToggleEnabled && await AsTask<bool>(session.TryTogglePlayPauseAsync());
            case "resume":return info.PlaybackStatus==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ||
                (c.IsPlayEnabled?await AsTask<bool>(session.TryPlayAsync()):
                c.IsPlayPauseToggleEnabled && await AsTask<bool>(session.TryTogglePlayPauseAsync()));
            case "repeatAlbum":case "repeatOne":case "repeatOff":
                var mode=command=="repeatAlbum"?Windows.Media.MediaPlaybackAutoRepeatMode.List:
                    command=="repeatOne"?Windows.Media.MediaPlaybackAutoRepeatMode.Track:Windows.Media.MediaPlaybackAutoRepeatMode.None;
                return c.IsRepeatEnabled && await AsTask<bool>(session.TryChangeAutoRepeatModeAsync(mode));
            default:return false;
        }
    }
    internal static async Task<MediaState> State(List<Album> library=null,bool includeArtwork=true)
    {
        var s=await Session();
        if(s==null) return new MediaState();
        var p=await AsTask<GlobalSystemMediaTransportControlsSessionMediaProperties>(s.TryGetMediaPropertiesAsync());
        var i=s.GetPlaybackInfo();
        var state=new MediaState {available=true,playing=i.PlaybackStatus==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            paused=i.PlaybackStatus==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            canPlay=i.Controls.IsPlayEnabled,canPause=i.Controls.IsPauseEnabled,title=p.Title,album=p.AlbumTitle,artist=p.Artist};
        if(String.IsNullOrWhiteSpace(state.album) && !String.IsNullOrWhiteSpace(state.artist)) {
            // Apple Music for Windows often puts both fields in its artist string.
            int at=state.artist.IndexOf(" — ",StringComparison.Ordinal);
            if(at>0) {state.album=state.artist.Substring(at+3).Trim();state.artist=state.artist.Substring(0,at).Trim();}
        }
        if(library!=null && !String.IsNullOrWhiteSpace(state.title)) {
            var match=MatchAlbum(library,state);
            state.albumId=match==null?null:match.id;
            if(includeArtwork && match!=null && match.TrackIndex(state.title,state.artist)>=0) {
                state.canStep=match.tracks.Count>1;
                try {state.repeatMode=await Task.Run(()=>MusicClient.RepeatMode());}catch(Exception ex){Debug.WriteLine(ex);}
                state.canRepeat=state.repeatMode!=null;
            }
            if(includeArtwork && (match==null || String.IsNullOrEmpty(match.cover))) {
                var key=Album.TrackKey(state.title,state.artist)+"\n"+state.album;
                if(key!=artKey || artData==null) {
                    try { artData=await ReadArtwork(p.Thumbnail);artKey=key; } catch {artData=null;}
                }
                state.artwork=artData;
            }
        }
        return state;
    }
    internal static async Task<bool> Toggle()
    {
        var s=await Session();
        if(s==null) return false;
        return s.GetPlaybackInfo().PlaybackStatus==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
            ? await AsTask<bool>(s.TryPauseAsync()) : await AsTask<bool>(s.TryPlayAsync());
    }

    internal static async Task<bool> Resume()
    {
        var session=await Session();
        return session!=null && await AsTask<bool>(session.TryPlayAsync());
    }

    internal static async Task<double> TrackPosition()
    {
        double position=await Task.Run(()=>MusicClient.Scrubber());
        if(position>=0)return position;
        var session=await Session();
        return session==null?-1:session.GetTimelineProperties().Position.TotalSeconds;
    }

    internal static async Task<bool> PauseForSwitch(Func<bool> cancelled)
    {
        var session=await Session();
        if(cancelled())return false;
        var playing=GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        if(session==null || session.GetPlaybackInfo().PlaybackStatus!=playing)return true;
        if(!await AsTask<bool>(session.TryPauseAsync()))return session.GetPlaybackInfo().PlaybackStatus!=playing;
        // Apple Music can acknowledge the command before publishing the paused state.
        var wait=Stopwatch.StartNew();
        while(!cancelled() && session.GetPlaybackInfo().PlaybackStatus==playing){
            if(wait.ElapsedMilliseconds>=1500)return false;
            await Task.Delay(40);
        }
        return !cancelled();
    }
}
