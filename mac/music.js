/* JavaScript for Automation, executed by macOS's osascript. */
function run(args) {
    const music = Application('com.apple.Music');
    const command = args[0];
    if (command === 'library') {
        const tracks = music.libraryPlaylists[0].tracks;
        const ids = tracks.databaseID(), albums = tracks.album(), artists = tracks.artist();
        const albumArtists = tracks.albumArtist(), numbers = tracks.trackNumber(), discs = tracks.discNumber();
        const compilations = tracks.compilation();
        let favorites = [];
        for (const property of ['albumFavorited', 'albumLoved']) {
            try { favorites = tracks[property](); break; } catch (_) {}
        }
        const grouped = new Map();
        for (let i=0; i<ids.length; i++) {
            if (!albums[i] || !Number.isInteger(ids[i])) continue;
            const artist = albumArtists[i] || (compilations[i] ? '' : artists[i]) || '';
            const key = JSON.stringify([albums[i],artist]);
            const order = (discs[i] || 1)*10000 + (numbers[i] || 9999);
            const id = String(ids[i]);
            let a = grouped.get(key);
            if (!a) {
                a = {id, title:albums[i], artist, favorite:false, order, trackIDs:[]};
                grouped.set(key,a);
            }
            if (order < a.order) { a.order=order; a.id=id; }
            a.favorite = a.favorite || favorites[i] === true;
            a.trackIDs.push({id,order});
        }
        for (const album of grouped.values()) {
            album.trackIDs.sort((a,b)=>a.order-b.order || Number(a.id)-Number(b.id));
            album.trackIDs=album.trackIDs.map(t=>t.id);
            album.id=album.trackIDs[0];
        }
        return JSON.stringify(Array.from(grouped.values()).sort((a,b)=>a.title.localeCompare(b.title)));
    }
    if (command === 'state') {
        if (!music.running()) return JSON.stringify({available:false});
        const state = music.playerState();
        if (state === 'stopped') return JSON.stringify({available:true, playing:false});
        const t = music.currentTrack;
        return JSON.stringify({available:true, playing:state==='playing', paused:state==='paused',
            repeatMode:repeatMode(music), title:t.name(), album:t.album(), artist:t.artist(), trackID:String(t.databaseID())});
    }
    if (['previousTrack','nextTrack','repeatAlbum','repeatOne','repeatOff'].includes(command)) {
        const ids=JSON.parse(args[1]), current=String(music.currentTrack.databaseID());
        const index=ids.indexOf(current);
        if (index<0) throw Error('The playing album changed.');
        const library=music.libraryPlaylists[0].tracks;
        if (command==='repeatOne' || command==='repeatOff') {
            music.songRepeat=command==='repeatOne' ? 'one' : 'off';
        } else if (command==='repeatAlbum') {
            const name='Self Coverflow · '+music.currentTrack.album()+' ['+ids[0]+']';
            const matches=music.userPlaylists.whose({name:{_equals:name}})();
            let queue=matches.find(p=>JSON.stringify(p.tracks.databaseID().map(String))===JSON.stringify(ids));
            if (!queue) {
                queue=music.UserPlaylist({name});
                music.userPlaylists.push(queue);
                try {
                    for (const id of ids) music.duplicate(findTrack(library,id),{to:queue});
                } catch (error) { music.delete(queue); throw error; }
            }
            if (currentPlaylistID(music)!==queue.persistentID()) {
                const paused=music.playerState()==='paused', position=music.playerPosition();
                music.play(findTrack(queue.tracks,current));
                if (paused) music.pause();
                music.playerPosition=position;
            }
            music.songRepeat='all';
        } else {
            const target=ids[(index+ids.length+(command==='nextTrack'?1:-1))%ids.length];
            const tracks=isShelfQueue(music) ? music.currentPlaylist.tracks : library;
            music.play(findTrack(tracks,target));
        }
        if (command.startsWith('repeat')) return repeatMode(music);
    } else if (command === 'play') {
        const matches=music.libraryPlaylists[0].tracks.whose({databaseID:{_equals:Number(args[1])}})();
        const t=matches[0];
        if (!t) throw Error('The selected track is no longer in the library.');
        const title=t.name(); // resolve the target before pausing
        if (!title) throw Error('The selected track has no title.');
        if (repeatMode(music)==='album') music.songRepeat='off';
        if (music.playerState()==='playing') music.pause();
        music.play(t);
    } else if (command==='resume') music.play();
    else if (command==='toggle') music.playpause();
    else if (command==='lyrics') {
        if (!music.running() || music.playerState()==='stopped') return '';
        return music.currentTrack.lyrics() || '';
    } else if (command==='seek') {
        if (String(music.currentTrack.databaseID())===args[2]) music.playerPosition=Number(args[1]);
    } else if (command==='position') return String(music.playerPosition());
    return '';
}

function findTrack(tracks,id) {
    const track=tracks.whose({databaseID:{_equals:Number(id)}})()[0];
    if (!track) throw Error('Music could not find this track.');
    return track;
}
function currentPlaylistID(music) {
    try { return music.currentPlaylist.persistentID(); } catch (_) { return ''; }
}
function isShelfQueue(music) {
    try { return music.currentPlaylist.name().startsWith('Self Coverflow · '); } catch (_) { return false; }
}
function repeatMode(music) {
    const mode=music.songRepeat();
    return mode==='one' ? 'one' : mode==='all' && isShelfQueue(music) ? 'album' : 'off';
}

