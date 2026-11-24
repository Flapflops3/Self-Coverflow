'use strict';
const shelfEvents = new EventTarget();
window.chrome = {webview:{
    postMessage(value){window.webkit.messageHandlers.shelf.postMessage(value);},
    addEventListener(type,listener){shelfEvents.addEventListener(type,listener);}
}};
window.receiveShelfMessage = data=>shelfEvents.dispatchEvent(new MessageEvent('message',{data}));

