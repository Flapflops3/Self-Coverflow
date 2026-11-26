using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

internal sealed class AlbumTrack
{
    internal readonly string Title, Artist;
    internal readonly int Order;
    internal AlbumTrack(string title,string artist,int order){Title=title;Artist=artist;Order=order;}
}

internal sealed class Album
{
    public string id, title, artist, cover;
    public bool favorite;
    public int count;
    internal ulong storeAlbum, firstSong;
    internal string cloudId, firstTitle, firstArtist;
    internal int firstOrder = int.MaxValue;
    internal uint added;
    internal readonly HashSet<string> songs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    internal readonly List<AlbumTrack> tracks = new List<AlbumTrack>();
    internal int TrackIndex(string title,string artist) {
        var matches = tracks.Select((track,index)=>new {track,index})
            .Where(x=>String.Equals(x.track.Title,title,StringComparison.OrdinalIgnoreCase)).ToList();
        if(matches.Count==1)return matches[0].index;
        var exact=matches.Where(x=>String.Equals(x.track.Artist,artist,StringComparison.OrdinalIgnoreCase)).ToList();
        return exact.Count==1?exact[0].index:-1;
    }
    internal AlbumTrack RelativeTrack(string title,string artist,int delta){
        int index=TrackIndex(title,artist);
        return index<0?null:tracks[(index+tracks.Count+delta)%tracks.Count];
    }
    internal static string TrackKey(string title,string artist) { return (title??"").Trim()+"\n"+(artist??"").Trim(); }
}

internal static class Library
{
    internal static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        "Apple Music", "Apple Music Library.musiclibrary");
    internal static readonly string Database = Path.Combine(Folder, "Library.musicdb");
    internal static readonly string Covers = Path.Combine(Folder, "artwork");
    static uint U32(byte[] b, int p) { return BitConverter.ToUInt32(b, p); }
    static ulong U64(byte[] b, int p) { return BitConverter.ToUInt64(b, p); }
    static string Text(byte[] b, int p, int size)
    {
        if (size < 36) return "";
        uint len = U32(b, p + 24);
        if (len > size - 36) return "";
        var enc = U32(b, p + 20) == 2 ? Encoding.UTF8 : Encoding.Unicode;
        return enc.GetString(b, p + 36, (int)len).TrimEnd('\0').Trim();
    }

    static byte[] ReadPayload()
    {
        byte[] b;
        using (var fs = new FileStream(Database, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var br = new BinaryReader(fs)) b = br.ReadBytes(checked((int)fs.Length));
        if (b.Length < 88 || Encoding.ASCII.GetString(b, 0, 4) != "hfma" || U32(b, 8) != b.Length)
            throw new InvalidDataException(Edition.Text("资料库正在更新，或当前版本的格式尚未支持。","The library is updating or uses an unsupported format."));
        int start = checked((int)U32(b, 4)), available = b.Length - start;
        if (start < 88 || available < 8) throw new InvalidDataException(Edition.Text("资料库文件不完整。","The library file is incomplete."));
        int limit = checked((int)U32(b, 84));
        int n = (limit > 0 ? Math.Min(available, limit) : available) / 16 * 16;
        using (var aes = Aes.Create())
        {
            aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None;
            aes.Key = FormatConstants.MetadataKey;
            using (var decoder = aes.CreateDecryptor())
            {
                var prefix = decoder.TransformFinalBlock(b, start, n);
                Buffer.BlockCopy(prefix, 0, b, start, n);
            }
        }
        using (var stream = new MemoryStream(b, start + 2, available - 6))
        using (var deflate = new DeflateStream(stream, CompressionMode.Decompress))
        using (var output = new MemoryStream()) { deflate.CopyTo(output); return output.ToArray(); }
    }

    internal static List<Album> Read()
    {
        var b = ReadPayload();
        var art = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(Covers))
            foreach (string path in Directory.EnumerateFiles(Covers).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string file = Path.GetFileName(path); int split = file.IndexOf("_sk", StringComparison.OrdinalIgnoreCase);
                if (split == 36 && !art.ContainsKey(file.Substring(0, split))) art[file.Substring(0,split)] = file;
            }
        var albums = new Dictionary<ulong,Album>();
        Album album = null, trackAlbum = null;
        int order = 0; bool hasAlbumTable = false, hasTrackTable = false;
        ulong songId = 0;
        string songTitle = null, songArtist = null;
        for (int p = 0; p + 8 <= b.Length;)
        {
            string sig = Encoding.ASCII.GetString(b, p, 4);
            if (sig!="boma" && trackAlbum!=null && !String.IsNullOrWhiteSpace(songTitle))
            {
                trackAlbum.songs.Add(Album.TrackKey(songTitle,songArtist));
                trackAlbum.tracks.Add(new AlbumTrack(songTitle,songArtist,order));
            }
            if (sig != "boma" && trackAlbum != null && songId > 0 && order < trackAlbum.firstOrder)
            {
                trackAlbum.firstSong=songId; trackAlbum.firstOrder=order;
                trackAlbum.firstTitle=songTitle; trackAlbum.firstArtist=songArtist;
            }
            if (U32(b,p) == 0 || (sig == "hsma" && hasAlbumTable && hasTrackTable)) break;
            bool atom = sig == "boma";
            if (atom && b.Length - p < 16) throw new InvalidDataException(Edition.Text("资料库记录不完整。","A library record is incomplete."));
            int size = checked((int)U32(b, p + (atom ? 8 : 4)));
            if (size < (atom ? 16 : 8) || size > b.Length - p) throw new InvalidDataException(Edition.Text("资料库记录长度异常。","A library record has an invalid length."));
            if (!atom)
            {
                album = null; trackAlbum = null; songId=0; songTitle=null; songArtist=null;
                if (sig == "lama") hasAlbumTable = true;
                if (sig == "ltma") hasTrackTable = true;
                if (sig == "iama" && size >= 44 && b[p+26] == 0)
                {
                    ulong id = U64(b,p+16);
                    album = new Album { id=id.ToString("X16"), favorite=BitConverter.ToUInt16(b,p+42)==2,
                        storeAlbum=size>=112 ? U64(b,p+104) : 0 };
                    albums[id] = album;
                }
                if (sig == "itma" && size >= 180 && albums.TryGetValue(U64(b,p+172),out trackAlbum))
                {
                    trackAlbum.count++;
                    order = Math.Max(1,(int)BitConverter.ToUInt16(b,p+84))*10000 + BitConverter.ToUInt16(b,p+160);
                    if (trackAlbum.cover == null && size >= 272)
                    {
                        string hex = BitConverter.ToString(b,p+256,16).Replace("-","");
                        string file;
                        if (art.TryGetValue(Guid.ParseExact(hex,"N").ToString(),out file)) trackAlbum.cover=file;
                    }
                }
            }
            else
            {
                uint sub = U32(b,p+12);
                if (album != null) {
                    if (sub==300) album.title=Text(b,p,size);
                    if (sub==301 || (sub==302 && String.IsNullOrEmpty(album.artist))) album.artist=Text(b,p,size);
                    if (sub==307) album.cloudId=Text(b,p,size);
                }
                if (trackAlbum != null && sub==1 && size>=332)
                {
                    trackAlbum.added=Math.Max(trackAlbum.added,U32(b,p+112));
                    ulong song=U64(b,p+324);
                    if (song>0 && song<long.MaxValue) songId=song;
                }
                if (trackAlbum!=null && sub==2) songTitle=Text(b,p,size);
                if (trackAlbum!=null && sub==4) songArtist=Text(b,p,size);
            }
            p += size;
        }
        if (!hasAlbumTable && albums.Count==0) throw new InvalidDataException(Edition.Text("未识别到专辑表。","The album table could not be found."));
        foreach(var entry in albums.Values)entry.tracks.Sort((a,c)=>a.Order.CompareTo(c.Order));
        return albums.Values.Where(a=>!String.IsNullOrWhiteSpace(a.title)).OrderByDescending(a=>a.added)
            .ThenBy(a=>a.title,StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}

