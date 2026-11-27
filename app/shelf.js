'use strict';
const $ = id => document.getElementById(id);
const send = value => window.chrome.webview.postMessage(value);
const stage = $('stage'), covers = $('covers');
const nodes = new Map();
const repeatButton = $('repeat');
const sourcePicker=$('musicSource');
const playerOptions=Array.from(sourcePicker.options);
const playerName=id=>playerOptions.find(option=>option.value===id)?.text || '';
let source='apple';
const recentShelves=new Map();
sourcePicker.onchange=()=>{endPending();send('source:'+sourcePicker.value);};
const playerControl=$('playerControl'), playerButton=$('playerButton'), playerMenu=$('playerMenu');

function closePlayers(restoreFocus=false) {
  playerMenu.hidden=true;
  playerButton.setAttribute('aria-expanded','false');
  if(restoreFocus && !playerControl.hidden)playerButton.focus();
}
function updatePlayers(running,installed) {
  const options=playerOptions.filter(option=>installed.includes(option.value));
  sourcePicker.replaceChildren(...options);
  sourcePicker.value=source;
  sourcePicker.closest('label').hidden=options.length===0;
  const focused=document.activeElement.dataset?.player;
  playerControl.hidden=running.length<2;
  if(playerControl.hidden)closePlayers();
  playerMenu.replaceChildren();
  for(const option of sourcePicker.options){
    if(!running.includes(option.value))continue;
    const row=document.createElement('button');
    row.type='button';row.dataset.player=option.value;row.textContent=option.text;
    row.setAttribute('role','menuitemradio');
    row.setAttribute('aria-checked',String(source===option.value));
    row.tabIndex=-1;
    playerMenu.append(row);
  }
  if(focused && !playerMenu.hidden)(playerMenu.querySelector('[data-player="'+focused+'"]') || playerMenu.firstElementChild)?.focus();
}
function openPlayers() {
  hideSearch();$('options').hidden=true;browsing();
  playerMenu.hidden=false;playerButton.setAttribute('aria-expanded','true');
  (playerMenu.querySelector('[aria-checked="true"]') || playerMenu.firstElementChild)?.focus();
}
playerButton.onclick=()=>playerMenu.hidden?openPlayers():closePlayers(true);
playerMenu.onclick=e=>{
  const row=e.target.closest('[data-player]');
  if(!row)return;
  closePlayers(true);
  if(source!==row.dataset.player){sourcePicker.value=row.dataset.player;sourcePicker.onchange();}
};
playerControl.onkeydown=e=>{
  if(e.key==='Escape'){closePlayers(true);}
  else if(e.key==='ArrowDown' || e.key==='ArrowUp'){
    if(playerMenu.hidden)openPlayers();
    else{
      const rows=Array.from(playerMenu.children),i=rows.indexOf(document.activeElement);
      rows[wrap(i+(e.key==='ArrowDown'?1:-1),rows.length)]?.focus();
    }
  }else return;
  e.preventDefault();e.stopPropagation();
};
playerControl.addEventListener('focusout',e=>{if(!playerControl.contains(e.relatedTarget))closePlayers();});
document.addEventListener('pointerdown',e=>{if(!playerControl.contains(e.target))closePlayers();});
for (const [id,label] of [['prev',ui.previousTrack],['next',ui.nextTrack]]) {
  $(id).title = label;
  $(id).setAttribute('aria-label',label);
  $(id).disabled = true;
}
repeatButton.hidden = false;
repeatButton.disabled = true;
let transportBusy = false;
function updateAlbumTransport() {
  const mode = media.repeatMode || 'off';
  const queue=source!=='apple';
  const label = mode === 'one' ? ui.repeatOne : mode === 'album' ? (queue?ui.queueRepeat:ui.repeatAlbum) : (queue?ui.queueOff:ui.repeatOff);
  repeatButton.dataset.mode = mode;
  repeatButton.title = label;
  repeatButton.setAttribute('aria-label',label);
  repeatButton.disabled = transportBusy || !media.canRepeat || !!pendingPlay;
  $('prev').disabled = transportBusy || !(media.canPrevious ?? media.canStep) || !!pendingPlay;
  $('next').disabled = transportBusy || !(media.canNext ?? media.canStep) || !!pendingPlay;
}
function albumCommand(command) {
  transportBusy = true;
  updateAlbumTransport();
  send(command);
}
let all = [], visible = [], index = 0;
let mode = localStorage.getItem('scope') || 'favorites';
let media = {}, noticeTimer, suppressClick = false;
let playingCard = null, browseSequence = 0, browsingActive = false, gesture = false;
let browseFlush = 0;
const wrap = (n, count) => ((n % count) + count) % count;
const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');
let cursor=0, motion=null, frame=0;
let wheelRest=0, wheelActive=false, wheelSpeed=0, wheelTime=0;
let layoutDirty=true, previousCenter=null, coverSize=1, radius=4, posed=[];

function stopRoll() {
  cancelAnimationFrame(frame);frame=0;motion=null;
}
function finishWheel() {
  clearTimeout(wheelRest);wheelActive=false;wheelSpeed=0;
}
// A critically damped spring keeps its velocity when more input arrives.
function glideTo(target, rate, snap) {
  if(reducedMotion.matches){
    stopRoll();cursor=target;index=wrap(Math.round(cursor),visible.length);
    if(snap)localStorage.setItem('selected-'+mode,visible[index].id);
    draw();return;
  }
  if(!motion || motion.following){
    stopRoll();
    motion={following:false,to:target,velocity:0,last:performance.now(),rate,snap};
    frame=requestAnimationFrame(paintFrame);
  }
  Object.assign(motion,{to:target,rate,snap});
  index=wrap(Math.round(target),visible.length);
}
function paintFrame(time) {
  if(!motion)return;
  const m=motion;
  const ms=Math.max(0,Math.min(48,time-m.last)); m.last=time;
  if(m.following){
    m.elapsed+=ms;
    const t=Math.min(1,m.elapsed/m.duration);
    cursor=m.from+(m.to-m.from)*t*t*(3-2*t);
    index=wrap(Math.round(cursor),visible.length);
    if(t===1){cursor=index;stopRoll();}
  }else{
    const dt=ms/1000, displacement=cursor-m.to;
    const decay=Math.exp(-m.rate*dt), slope=m.velocity+m.rate*displacement;
    cursor=m.to+(displacement+slope*dt)*decay;
    m.velocity=(m.velocity-m.rate*slope*dt)*decay;
    if(Math.abs(cursor-m.to)<.001 && Math.abs(m.velocity)<.02){
      cursor=m.to;
      if(m.snap){cursor=index;localStorage.setItem('selected-'+mode,visible[index].id);}
      stopRoll();
    }
  }
  draw();
  if(motion)frame=requestAnimationFrame(paintFrame);
}
function rollTo(target,following) {
  const length=visible.length;
  if(!length)return;
  let delta=wrap(target-cursor+length/2,length)-length/2;
  if(!following){glideTo(cursor+delta,19,true);return;}
  if(motion && motion.following===following && Math.abs(motion.to-cursor-delta)<.0001)return;
  stopRoll();
  if(Math.abs(delta)<.001 || reducedMotion.matches){
    cursor=index=target;draw();return;
  }
  const distance=Math.abs(delta);
  const duration=400+distance*75;
  motion={from:cursor,to:cursor+delta,following,elapsed:0,last:performance.now(),duration};
  frame=requestAnimationFrame(paintFrame);
}

function currentAlbum() {
  if (!media.available || !media.title) return null;
  if(source!=='apple'){
    const title=media.album || media.title,artist=media.albumArtist || media.artist || '';
    return {id:JSON.stringify([source,title,artist]),title,artist,temporary:true};
  }
  return all.find(a => a.id === media.albumId) || {
    id:'now/' + JSON.stringify([media.album || media.title,media.album ? '' : media.artist]),
    title:media.album || media.title, artist:media.artist, temporary:true
  };
}
let pendingPlay=null, playSerial=0, pollPlay=0;
let driveSoundEnabled=localStorage.getItem('driveSoundEnabled')!=='false';
let searchSelection=null;
function follow() {
  if (searchSelection || $('lookup').classList.contains('open') || pendingPlay || browsingActive || gesture || !$('options').hidden || !playerMenu.hidden || !playingCard) return;
  const target=visible.findIndex(a=>a.id===playingCard.id);
  if(target>=0)rollTo(target,true);
}
function browsing() {
  if(motion?.following)stopRoll();
  browsingActive=true;
  ++browseSequence;
  if(!browseFlush)send('browse:'+browseSequence);
  clearTimeout(browseFlush);
  const sequence=browseSequence;
  browseFlush=setTimeout(()=>{
    browseFlush=0;
    send('browse:'+sequence);
  },45);
}

function status() {
  updateAlbumTransport();
  $('status').classList.toggle('pending',!!pendingPlay);
  $('status').textContent = pendingPlay ? ui.starting+(pendingPlay.title?' · '+pendingPlay.title:'') : media.title ? (media.playing ? ui.playing : ui.paused) + ' · ' + media.title : '';
  $('transport').classList.toggle('playing',Boolean(media.playing));
  const local=source!=='apple';
  $('transport').disabled=local && !(media.playing?media.canPause:media.canPlay);
  $('transport').setAttribute('aria-label',local?(media.playing?ui.localPause:ui.localResume):(media.playing?ui.pause:ui.play));
  $('transport').title = local?(media.playing?ui.localPause:ui.localResume):(media.playing?ui.pause:ui.resume);
}
function endPending(error) {
  if(!pendingPlay)return;
  driveSound.stop();
  clearInterval(pollPlay);pendingPlay=null;status();
  if(error)notice(error);
}
function confirmPlayback(){
  if(pendingPlay?.id===searchSelection)searchSelection=null;
  endPending();
}
function startPlay(album) {
  clearInterval(pollPlay);clearTimeout(noticeTimer);
  const serial=++playSerial;
  pendingPlay={serial,id:album?.id,title:album?.title || media.title};
  status();
  pollPlay=setInterval(()=>send('syncPlayback'),180);
  send(album && !album.temporary?'open:'+serial+':'+album.id:'resume:'+serial);
  if(driveSoundEnabled)driveSound.start();
}
function notice(text) {
  clearTimeout(noticeTimer);
  $('status').textContent = text;
  noticeTimer = setTimeout(status, 6500);
}
function updateList(preserve,initialFocus=false) {
  layoutDirty=true;
  const old=visible;
  const previous = preserve && visible[index] ? visible[index].id : localStorage.getItem('selected-' + mode);
  visible = source==='apple'?all.filter(a => mode === 'all' || a.favorite):all.slice();
  playingCard=currentAlbum();
  if(playingCard && !visible.some(a=>a.id===playingCard.id)) visible.unshift(playingCard);
  if(searchSelection && !visible.some(a=>a.id===searchSelection))searchSelection=null;
  index = Math.max(0, visible.findIndex(a => a.id === (searchSelection || previous)));
  if(!preserve || old.length!==visible.length || old.some((a,i)=>a.id!==visible[i].id)) {
    finishWheel();stopRoll();cursor=index;
  }
  if(initialFocus && !searchSelection && !pendingPlay && !gesture && !browsingActive && !lookup.classList.contains('open') && playingCard)cursor=index=visible.findIndex(a=>a.id===playingCard.id);
  draw();follow();
}
function toggleFilter() {
  searchSelection=null;
  mode = mode === 'all' ? 'favorites' : 'all';
  localStorage.setItem('scope', mode); $('options').hidden = true; browsing(); updateList(false);
}
function select(i) {
  if (!visible.length) return;
  finishWheel();browsing();
  const target=wrap(i,visible.length);
  if(visible[target].id!==searchSelection)searchSelection=null;
  rollTo(target,false);
}
const lookup=$('lookup'), query=$('albumQuery'), matches=$('albumMatches');
let searchIndex=[], results=[], activeResult=0;
let departure=null, arrival=null;
function searchable(text){
  return (text||'').normalize('NFKD').toLowerCase().replace(/[\u0300-\u036f]/g,'').replace(/[^\p{L}\p{N}]+/gu,' ').trim();
}
function highlightResult(i){
  activeResult=i;
  Array.from(matches.children).forEach((row,n)=>row.setAttribute('aria-selected',String(n===i)));
  if(matches.children[i])query.setAttribute('aria-activedescendant',matches.children[i].id);
  else query.removeAttribute('aria-activedescendant');
}
function renderResults(){
  const term=searchable(query.value), tokens=term.split(' ');
  results=[];
  if(term){
    for(const item of searchIndex){
      if(!tokens.every(word=>item.title.includes(word)||item.artist.includes(word)))continue;
      const rank=item.title===term?0:item.title.startsWith(term)?1:item.title.includes(term)?2:item.artist.startsWith(term)?3:4;
      results.push({album:item.album,rank});
    }
    results.sort((a,b)=>a.rank-b.rank);
    results=results.slice(0,4);
  }
  matches.replaceChildren();
  results.forEach(({album},i)=>{
    const row=document.createElement('div');
    row.setAttribute('role','option');row.id='album-match-'+i;row.dataset.index=String(i);
    const title=document.createElement('strong');title.textContent=album.title;
    const artist=document.createElement('small');artist.textContent=album.artist;
    row.append(title,artist);matches.append(row);
  });
  highlightResult(0);
  $('searchPanel').hidden=!term;
  $('searchEmpty').hidden=!term || results.length>0;
  query.setAttribute('aria-expanded',String(!!term && results.length>0));
}
function showSearch(){
  browsing();$('options').hidden=true;
  lookup.classList.add('open');$('searchToggle').setAttribute('aria-expanded','true');
  query.disabled=false;query.focus({preventScroll:true});
}
function hideSearch(){
  if(!lookup.classList.contains('open'))return;
  lookup.classList.remove('open');$('searchToggle').setAttribute('aria-expanded','false');
  query.disabled=true;query.value='';renderResults();browsing();
}
function chooseResult(i){
  const album=results[i]?.album;
  if(!album)return;
  hideSearch();stage.focus({preventScroll:true});
  finishWheel();stopRoll();
  if(!visible.some(a=>a.id===album.id)){
    // Carry the centered album across the filter change before starting the roll.
    index=visible.length?wrap(Math.round(cursor),visible.length):0;
    mode='all';localStorage.setItem('scope',mode);updateList(true);
  }
  const dest=visible.findIndex(a=>a.id===album.id);
  if(dest<0)return;
  const delta=wrap(dest-cursor+visible.length/2,visible.length)-visible.length/2;
  if(arrival)arrival.cancel();
  if(departure)departure.remove();
  if(Math.abs(delta)>8 && !reducedMotion.matches){
    // Bridge distant shelves without flashing every intervening cover.
    const old=covers.cloneNode(true);
    old.removeAttribute('id');old.className='outgoing-covers';old.inert=true;old.setAttribute('aria-hidden','true');
    stage.append(old);departure=old;
    const sign=Math.sign(delta);
    old.animate([{opacity:1,transform:'translateX(0)'},{opacity:0,transform:'translateX('+(-sign*coverSize*.38)+'px)'}],
      {duration:225,easing:'ease-out',fill:'forwards'}).onfinish=()=>{old.remove();if(departure===old)departure=null;};
    cursor=dest-sign*1.45;draw();
    arrival=covers.animate([{opacity:0},{opacity:1}],{duration:225,easing:'ease-out'});
  }
  select(dest);
  searchSelection=album.id;
}
$('searchToggle').onclick=()=>lookup.classList.contains('open')?hideSearch():showSearch();
query.addEventListener('input',renderResults);
$('searchForm').addEventListener('submit',e=>{e.preventDefault();});
query.addEventListener('keydown',e=>{
  if(e.isComposing || e.keyCode===229)return;
  if(e.key==='Escape'){e.preventDefault();hideSearch();$('searchToggle').focus();return;}
  if(e.key==='Enter'){e.preventDefault();chooseResult(activeResult);return;}
  if(results.length && (e.key==='ArrowDown' || e.key==='ArrowUp')){
    e.preventDefault();highlightResult(wrap(activeResult+(e.key==='ArrowDown'?1:-1),results.length));
  }
});
matches.addEventListener('pointerdown',e=>e.preventDefault());
matches.addEventListener('pointermove',e=>{
  const row=e.target.closest('[role=option]');
  if(row && e.pointerType==='mouse' && Number(row.dataset.index)!==activeResult)highlightResult(Number(row.dataset.index));
});
matches.addEventListener('click',e=>{const row=e.target.closest('[role=option]');if(row)chooseResult(Number(row.dataset.index));});
lookup.addEventListener('focusout',()=>queueMicrotask(()=>{if(!lookup.contains(document.activeElement))hideSearch();}));
document.addEventListener('pointerdown',e=>{if(!lookup.contains(e.target))hideSearch();});
function openSelected(selected=visible[index]) {
  if(!selected)return;
  if(source!=='apple'){
    if(selected.id!==currentAlbum()?.id){notice(ui.localReplay);return;}
    if(media.playing || !media.canPlay)return;
  }
  browsing();
  startPlay(selected);
}
function draw() {
  const length=visible.length, center=Math.round(cursor), displayed=length?wrap(center,length):0, a=visible[displayed];
  if(!layoutDirty && center===previousCenter){paintCovers();return;}
  layoutDirty=false;previousCenter=center;
  $('filter').firstChild.textContent = source!=='apple' ? playerName(source) : mode === 'favorites' ? ui.favorites : ui.all;
  $('switch').textContent = mode === 'favorites' ? ui.showAll : ui.showFavorites;
  $('counter').textContent = length ? String(displayed + 1).padStart(2, '0') + ' / ' + length : '0 / 0';
  $('album').textContent = a ? a.title : !source?ui.noPlayers:source!=='apple'?ui.waitingPlayer:(mode === 'favorites' ? ui.noFavorites : ui.empty);
  $('artist').textContent = a ? a.artist || ui.unknown : !source?'':source!=='apple'?(media.hint || ui.localHint):(mode === 'favorites' ? ui.browseAll : ui.add);
  $('album').title = $('album').textContent; $('artist').title = $('artist').textContent;
  const keep = new Set();posed=[];
  const left=Math.min(radius,Math.floor((length-1)/2)),right=Math.min(radius,length-1-left);
  for (let offset = -left; length && offset <= right; offset++) {
    const position = wrap(center+offset,length), item = visible[position];
    keep.add(item.id);
    let card = nodes.get(item.id);
    if (!card) {
      card = document.createElement('button'); card.className = 'cover'; card.tabIndex = -1; card.dataset.id=item.id;
      card.setAttribute('aria-label',item.title + ' · ' + (item.artist || ''));
      const fallback = document.createElement('span'); fallback.textContent = '♪'; card.append(fallback);
      card.onclick = () => {
        if (suppressClick) return;
        const selected = visible.findIndex(x => x.id === item.id);
        if (selected !== index) select(selected);
      };
      card.ondblclick = e => {
        if(suppressClick)return;
        e.preventDefault();openSelected(item);
      };
      nodes.set(item.id,card); covers.append(card);
    }
    const artwork=item.cover ? 'https://art.local/'+encodeURIComponent(item.cover) : item.localArtwork || (item.id===playingCard?.id && media.artwork) || '';
    if (artwork !== (card.dataset.cover || '')) {
      const old = card.querySelector('img'); if (old) old.remove();
      card.dataset.cover = artwork;
      if (artwork) {
        const img = new Image(); img.alt = ''; img.draggable = false;img.decoding='async';
        img.style.position = 'relative'; img.onerror = () => img.remove();
        img.src = artwork; card.append(img);
      }
    }
    posed.push({card,position:center+offset});
  }
  for (const [id,node] of nodes) if (!keep.has(id)) { node.remove(); nodes.delete(id); }
  paintCovers();
}
function paintCovers(){
  for(const {card,position} of posed){
    const delta=position-cursor, distance=Math.abs(delta), side=Math.sign(delta);
    const turn=Math.min(1,distance), outer=Math.max(0,distance-1);
    const x=side*coverSize*(.77*turn+.34*outer);
    const scale=Math.max(.68,1-.13*turn-.04*outer);
    const tilt=-side*55*turn*(2-turn);
    card.style.transform='translate(-50%,-50%) translateX('+x+'px) scale('+scale+') rotateY('+tilt+'deg)';
    card.style.zIndex=String(1000-Math.round(distance*2));
  }
}
function step(amount) { if (visible.length>1) select(index + amount); }
$('prev').onclick = () => albumCommand('previousTrack');
$('next').onclick = () => albumCommand('nextTrack');
const nextRepeatCommand = {off:'repeatAlbum', album:'repeatOne', one:'repeatOff'};
repeatButton.onclick = () => albumCommand(nextRepeatCommand[media.repeatMode || 'off']);
$('transport').onclick = () => {
  if(media.playing){
    if(driveSoundEnabled)driveSound.unlock();
    endPending();send('toggle');
  }else startPlay(null);
};
$('filter').onclick = $('switch').onclick = toggleFilter;
$('menu').onclick = () => { $('options').hidden = !$('options').hidden; browsing(); };
const soundSwitch=$('driveSoundSwitch');
soundSwitch.setAttribute('aria-checked',String(driveSoundEnabled));
soundSwitch.onclick=()=>{
  driveSoundEnabled=!driveSoundEnabled;
  soundSwitch.setAttribute('aria-checked',String(driveSoundEnabled));
  localStorage.setItem('driveSoundEnabled',String(driveSoundEnabled));
  if(!driveSoundEnabled)driveSound.stop();
  else if(pendingPlay)driveSound.start();
};
$('pin').onclick = () => send('pin');
for(const button of document.querySelectorAll('[data-size]'))button.onclick=()=>{
  $('options').hidden=true;
  send('size:'+button.dataset.size+(reducedMotion.matches?':instant':''));
  browsing();
};
$('lyrics').onclick=()=>{$('options').hidden=true;send(reducedMotion.matches?'lyrics:instant':'lyrics');};
$('close').onclick = () => send('close');
$('refresh').onclick = () => { $('options').hidden = true; browsing(); send('reload'); };
document.addEventListener('pointerdown',e => {if (!$('options').hidden && !e.target.closest('#options,#menu')) { $('options').hidden=true; browsing(); }});
function scroll(e) {
  e.preventDefault();
  if(visible.length<2)return;
  searchSelection=null;
  browsing();
  const delta=Math.abs(e.deltaX)>Math.abs(e.deltaY) ? e.deltaX : e.deltaY;
  const amount=Math.max(-3,Math.min(3,delta*(e.deltaMode===1?32:e.deltaMode===2?100:1)/100));
  const now=performance.now(), gap=now-wheelTime;
  const dest=wheelActive && motion ? motion.to : cursor;
  wheelSpeed=wheelActive && gap<100 ? .55*wheelSpeed+.45*amount/Math.max(8,gap) : 0;
  wheelTime=now;wheelActive=true;
  glideTo(dest+amount,26,false);
  clearTimeout(wheelRest);
  wheelRest=setTimeout(()=>{
    const tail=Math.max(-.65,Math.min(.65,wheelSpeed*32));
    const target=Math.round((motion?.to??cursor)+tail);
    finishWheel();glideTo(target,18,true);
  },85);
}
stage.addEventListener('wheel',scroll,{passive:false});
let startY=0, direction=null, pulledCard=null;
let startX=null, dragOrigin=0, dragTarget=0, dragSpeed=0, dragTime=0, pointerId=null;
stage.addEventListener('pointerdown',e=>{
  if(e.button!==0 || pointerId!==null)return;
  finishWheel();gesture=true;browsing();stopRoll();index=visible.length?wrap(Math.round(cursor),visible.length):0;
  pulledCard=e.target.closest('.cover');
  pointerId=e.pointerId;startX=e.clientX;startY=e.clientY;direction=null;dragOrigin=dragTarget=cursor;dragSpeed=0;dragTime=performance.now();suppressClick=false;
});
stage.addEventListener('pointermove',e=>{
  if(e.pointerId!==pointerId || startX===null || !visible.length)return;
  const dx=startX-e.clientX;
  const dy=e.clientY-startY;
  if(!direction){
    if(Math.max(Math.abs(dx),Math.abs(dy))<5)return;
    direction=Math.abs(dy)>Math.abs(dx)*1.15?'lyrics':'browse';
  }
  if(!suppressClick){stage.setPointerCapture(e.pointerId);suppressClick=true;}
  if(direction==='lyrics'){
    if(source!=='apple')return;
    if(!pulledCard)return;
    pulledCard.classList.add('pulling');
    pulledCard.style.setProperty('--pull',reducedMotion.matches?'0px':Math.sign(dy)*Math.min(50,Math.abs(dy)*.52)+'px');
    return;
  }
  if(visible.length<2)return;
  searchSelection=null;
  const target=dragOrigin+dx/Math.max(60,coverSize*.52);
  const now=performance.now();
  dragSpeed=.4*dragSpeed+.6*(target-dragTarget)/Math.max(6,now-dragTime);
  dragTime=now;dragTarget=target;glideTo(target,52,false);
});
function endDrag(e){
  if(e.pointerId!==pointerId)return;
  pointerId=null;gesture=false;browsing();startX=null;
  const pulled=direction==='lyrics';
  if(pulledCard){
    pulledCard.classList.remove('pulling');pulledCard.style.removeProperty('--pull');
    const releasedPull=e.clientY-startY;
    if(source==='apple' && pulled && e.type==='pointerup' && Math.abs(releasedPull)>=42){
      const dest=visible.findIndex(a=>a.id===pulledCard.dataset.id);
      if(dest>=0){
        const wasSide=dest!==wrap(Math.round(dragOrigin),visible.length);
        select(dest);
        if(releasedPull<0 || wasSide)openSelected(visible[dest]);
        send(reducedMotion.matches?'lyrics:instant':'lyrics');
      }
    }
    pulledCard=null;
  }
  if(!pulled && visible.length && (suppressClick || Math.abs(cursor-Math.round(cursor))>.001)){
    const coast=suppressClick && e.type==='pointerup' && performance.now()-dragTime<100 ? Math.max(-1.25,Math.min(1.25,dragSpeed*65)) : 0;
    glideTo(Math.round(dragTarget+coast),19,true);
  }
  setTimeout(()=>suppressClick=false,0);
}
window.addEventListener('pointerup',endDrag);stage.addEventListener('pointercancel',endDrag);
stage.addEventListener('lostpointercapture',e=>{if(e.target===stage && pointerId===e.pointerId)endDrag(e);});
window.addEventListener('blur',()=>{if(pointerId!==null)endDrag({pointerId,type:'pointercancel'});});
$('grip').addEventListener('pointerdown',e=>{
  if(e.button!==0 || e.target.closest('button,#lookup'))return;
  $('grip').setPointerCapture(e.pointerId);send('dragStart');
});
$('grip').onpointermove=e=>{if($('grip').hasPointerCapture(e.pointerId))send('drag');};
$('grip').onlostpointercapture=()=>send(reducedMotion.matches?'dragEnd:instant':'dragEnd');
document.addEventListener('keydown',e=>{
  if(e.target.closest('#lookup,#playerControl,select') || e.isComposing)return;
  if(e.key==='ArrowLeft' || e.key==='ArrowRight'){
    if (!e.repeat) $(e.key==='ArrowLeft'?'prev':'next').click();
  }
  else if(e.key==='Enter' && !e.target.closest('button'))openSelected();
  else if(e.key===' ' && !e.target.closest('button'))$('transport').click();
  else if(e.key==='Escape'){
    if($('options').hidden && searchSelection){searchSelection=null;browsingActive=false;follow();}
    else{$('options').hidden=true;browsing();}
  }else return;e.preventDefault();
});
window.chrome.webview.addEventListener('message',e=>{
  const x=e.data;
  if(x.type==='runningPlayers'){updatePlayers(x.players,x.installed);return;}
  if(x.type==='source'){
    source=x.source;sourcePicker.value=source;
    playerButton.title=playerMenu.getAttribute('aria-label')+' · '+playerName(source);
    playerButton.setAttribute('aria-label',playerButton.title);
    for(const row of playerMenu.children)row.setAttribute('aria-checked',String(row.dataset.player===source));
    document.body.classList.toggle('local-source',source!=='apple');
    $('filter').disabled=source!=='apple';
    endPending();hideSearch();searchSelection=null;all=recentShelves.get(source)||[];media={};transportBusy=false;
    searchIndex=all.map(album=>({album,title:searchable(album.title),artist:searchable(album.artist)}));
    updateList(false);status();
  }else if(x.type==='library'){
    const first=!all.length;all=x.albums;
    searchIndex=all.map(album=>({album,title:searchable(album.title),artist:searchable(album.artist)}));
    if(lookup.classList.contains('open'))renderResults();
    if(first && !localStorage.getItem('scope') && !all.some(a=>a.favorite))mode='all';
    updateList(!first);
  }else if(x.type==='libraryError'){
    if(!all.length){$('album').textContent=ui.libraryError;$('artist').textContent=x.message;}
    notice(x.message);
  }else if(x.type==='media'){const first=!media.title,previous=media;media=x.state;
    if(source!=='apple'){
      const item=currentAlbum();
      if(item){
        const existing=all.find(a=>a.id===item.id);
        if(!existing){all=[item,...all].slice(0,16);recentShelves.set(source,all);}
        if(media.artwork)(existing || item).localArtwork=media.artwork;
        if(!existing){searchIndex=all.map(album=>({album,title:searchable(album.title),artist:searchable(album.artist)}));if(lookup.classList.contains('open'))renderResults();}
      }
    }
    if(media.playing || !media.available)driveSound.stopPause();
    if(driveSoundEnabled && !pendingPlay && previous.playing && media.paused && media.title===previous.title)driveSound.pause();
    if(pendingPlay && previous.available && !media.available)endPending(ui.playTimeout);
    if(pendingPlay && media.playing && (!pendingPlay.id || pendingPlay.id===currentAlbum()?.id))confirmPlayback();
    status();updateList(true,first);}
  else if(x.type==='playbackCheck' && x.playing && pendingPlay && (!pendingPlay.id || pendingPlay.id===(x.id || currentAlbum()?.id)))confirmPlayback();
  else if(x.type==='albumCommandDone'){
    if(x.repeatMode)media.repeatMode=x.repeatMode;
    transportBusy=false;updateAlbumTransport();if(x.message)notice(x.message);
  }
  else if(x.type==='playFailed' && x.serial===pendingPlay?.serial)endPending(x.message);
  else if(x.type==='viewport'){
    document.documentElement.style.setProperty('--shelf-width',(x.fraction*100)+'vw');
  }else if(x.type==='size'){
    document.body.dataset.mode=x.mode;
    for(const button of document.querySelectorAll('[data-size]'))button.setAttribute('aria-pressed',String(button.dataset.size===x.mode));
  }
  else if(x.type==='idle' && x.sequence===browseSequence){browsingActive=false;follow();}
  else if(x.type==='notice')notice(x.message);
  else if(x.type==='pin'){
    $('pin').setAttribute('aria-pressed',String(!!x.pinned));$('pin').title=x.pinned?ui.unpin:ui.pin;
  }
});
new ResizeObserver(([entry])=>{
  const {width,height}=entry.contentRect;
  coverSize=Math.max(1,Math.floor(Math.min(height*.86,width*.40)));
  radius=Math.max(4,Math.ceil((width/2-coverSize*.45)/(coverSize*.34))+2);
  stage.style.setProperty('--size',coverSize+'px');layoutDirty=true;draw();
}).observe(stage);
window.shelfCheck=()=>{
  const before=index, scope=mode;
  const images=[...covers.querySelectorAll('img')].filter(i=>i.complete&&i.naturalWidth).length;
  stage.dispatchEvent(new WheelEvent('wheel',{deltaY:90,cancelable:true})); const advanced=index!==before;
  step(-1); const returned=index===before;
  toggleFilter();const switched=scope!==mode;toggleFilter();
  return {passed:advanced&&returned&&switched&&images>0,total:all.length,favorites:all.filter(a=>a.favorite).length,
    cached:all.filter(a=>a.cover).length,visibleImages:images,selected:visible[index]?.title};
};

