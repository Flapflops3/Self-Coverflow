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
            a.trackIDs.push(id);
        }
        return JSON.stringify(Array.from(grouped.values()).sort((a,b)=>a.title.localeCompare(b.title)));
    }
    if (command === 'state') {
        if (!music.running()) return JSON.stringify({available:false});
        const state = music.playerState();
        if (state === 'stopped') return JSON.stringify({available:true, playing:false});
        const t = music.currentTrack;
        return JSON.stringify({available:true, playing:state==='playing', paused:state==='paused',
            title:t.name(), album:t.album(), artist:t.artist(), trackID:String(t.databaseID())});
    }
    if (command === 'play') {
        const t=music.libraryPlaylists[0].tracks.byId(Number(args[1]));
        if (!t.exists()) throw Error('The selected track is no longer in the library.');
        const title=t.name(); // resolve the target before pausing
        if (!title) throw Error('The selected track has no title.');
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

