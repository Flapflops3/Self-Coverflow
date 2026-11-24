'use strict';

// "CD drive spin", 607freesound (CC0), https://freesound.org/s/550459/
// The local edit contains the start and motor, followed by a separate pause sample.
const driveSound=(()=>{
  const loopStart=1.6, loopEnd=4.55;
  const effectGain=0.708; // -3 dB for spin and pause
  let context, bufferPromise, voice;

  function prepare(){
    if(!context)context=new AudioContext();
    if(!bufferPromise){
      bufferPromise=fetch('disc-drive.wav').then(response=>{
        if(!response.ok)throw new Error('Disc recording unavailable');
        return response.arrayBuffer();
      }).then(bytes=>context.decodeAudioData(bytes)).catch(error=>{
        bufferPromise=null;
        throw error;
      });
    }
    return Promise.all([context.resume(),bufferPromise]);
  }

  function stop(){
    const active=voice;
    voice=null;
    if(!active?.source)return;
    const now=context.currentTime;
    active.gain.gain.cancelAndHoldAtTime(now);
    active.gain.gain.linearRampToValueAtTime(0,now+.045);
    active.source.stop(now+.05);
  }

  async function play(kind){
    if(voice?.kind===kind)return;
    stop();
    const active=voice={kind};
    const requested=performance.now();
    try{
      const [,buffer]=await prepare();
      if(voice!==active)return;
      const source=active.source=context.createBufferSource();
      const gain=active.gain=context.createGain();
      source.buffer=buffer;
      source.connect(gain);gain.connect(context.destination);
      const spin=kind==='spin';
      const onset=context.currentTime+(spin?Math.max(0,.14-(performance.now()-requested)/1000):0);
      gain.gain.setValueAtTime(0,onset);
      gain.gain.linearRampToValueAtTime(effectGain,onset+.006);
      source.loop=spin;
      source.loopStart=loopStart;source.loopEnd=loopEnd;
      source.onended=()=>{
        source.disconnect();gain.disconnect();
        if(voice===active)voice=null;
      };
      source.start(onset,spin?0:loopEnd);
    }catch{if(voice===active)voice=null;}
  }

  window.addEventListener('pagehide',stop);
  return {
    start:()=>play('spin'), pause:()=>play('pause'), stop,
    unlock:()=>prepare().catch(()=>{}),
    stopPause:()=>{if(voice?.kind==='pause')stop();}
  };
})();

