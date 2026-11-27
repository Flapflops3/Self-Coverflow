import AppKit
import WebKit

final class ShelfWindow: NSWindow {
    override var canBecomeKey:Bool {true}
    override var canBecomeMain:Bool {true}
}

final class Shelf: NSObject, NSApplicationDelegate, NSWindowDelegate, WKNavigationDelegate, WKScriptMessageHandler {
    let defaults=UserDefaults.standard
    let en=Bundle.main.object(forInfoDictionaryKey:"ShelfLanguage") as? String=="en"
    let smoke=CommandLine.arguments.contains("--smoke-test")
    let resources=Resources()
    let work=DispatchQueue(label:"cover-shelf.music")
    var window:ShelfWindow!
    var web:WKWebView!
    var lyrics:LyricPanel!
    var ready=false,reading=false,polling=false,attachedLyrics=true
    var pinned=false,docked=false,mode="classic",revision=0
    var lastTrack="",lyricsTrack="",lastState=Data(),lastRead=Date.distantPast
    var source="apple",sourceRevision=0
    var albumByTrack=[String:String](), tracksByAlbum=[String:[String]]()
    var origin=NSPoint.zero,anchor=NSPoint.zero
    var idle:DispatchWorkItem?
    var timer:Timer?
    var lastRunningPlayers:[String]?
    var installedPlayers=[String](), nextInstallScan=Date.distantPast
    var registeredPlayers=[String]()
    func refreshRunningPlayers(){
        let ids=Set(NSWorkspace.shared.runningApplications.compactMap { $0.bundleIdentifier })
        var players=LocalPlayer.bundles.compactMap { ids.contains($0.value) ? $0.key : nil }.sorted()
        if ids.contains("com.apple.Music"){players.insert("apple",at:0)}
        if Date() >= nextInstallScan {
            nextInstallScan=Date().addingTimeInterval(30)
            let bundles=LocalPlayer.bundles.merging(["apple":"com.apple.Music"]) { old,_ in old }
            registeredPlayers=bundles.compactMap { key,id in
                guard let url=NSWorkspace.shared.urlForApplication(withBundleIdentifier:id),
                      FileManager.default.fileExists(atPath:url.path) else{return nil}
                return key
            }
        }
        let installed=["apple","spotify","qq","kugou"].filter { registeredPlayers.contains($0) || players.contains($0) }
        guard players != lastRunningPlayers || installed != installedPlayers else{return}
        installedPlayers=installed
        if !smoke && !installed.contains(source){ selectSource(players.first ?? installed.first ?? "") }
        lastRunningPlayers=players
        post(["type":"runningPlayers","players":players,"installed":installed])
    }
    func tr(_ cn:String,_ english:String)->String {en ? english : cn}
    func applicationDidFinishLaunching(_ note:Notification) {
        let config=WKWebViewConfiguration()
        config.setURLSchemeHandler(resources,forURLScheme:"shelf")
        config.userContentController.add(self,name:"shelf")
        config.mediaTypesRequiringUserActionForPlayback=[]
        web=WKWebView(frame:.zero,configuration:config)
        web.navigationDelegate=self
        window=ShelfWindow(contentRect:NSRect(x:0,y:0,width:472,height:306),styleMask:[.borderless],backing:.buffered,defer:false)
        window.isReleasedWhenClosed=false;window.title="Self Coverflow"
        window.isOpaque=false;window.backgroundColor = .clear;window.hasShadow=true
        window.contentView=web;web.wantsLayer=true;web.layer?.cornerRadius=17;web.layer?.masksToBounds=true
        window.delegate=self
        window.center()
        if !smoke {
            if let saved=defaults.string(forKey:"source"),LocalPlayer.bundles[saved] != nil {source=saved}
            _=window.setFrameUsingName("CoverShelf")
            if !NSScreen.screens.contains(where:{$0.visibleFrame.intersects(window.frame)}) {window.center()}
            pinned=defaults.bool(forKey:"pin");docked=defaults.bool(forKey:"desktop")
            mode=defaults.string(forKey:"mode") ?? "classic"
        }
        applyLevel();resize(mode,animated:false)
        lyrics=LyricPanel(title:tr("歌词","Lyrics"))
        lyrics.seek={ [weak self] sec,id in self?.run(["seek",String(sec),id]) }
        window.makeKeyAndOrderFront(nil)
        web.load(URLRequest(url:URL(string:"shelf://ui/index.html")!))
        if smoke {DispatchQueue.main.asyncAfter(deadline:.now()+30){exit(7)}}
    }
    func webView(_ webView:WKWebView,decidePolicyFor action:WKNavigationAction,decisionHandler:@escaping(WKNavigationActionPolicy)->Void) {
        decisionHandler(action.request.url?.absoluteString=="shelf://ui/index.html" ? .allow : .cancel)
    }
    func webView(_ webView:WKWebView,didFinish navigation:WKNavigation!) {
        ready=true;post(["type":"size","mode":mode]);announceLevel()
        post(["type":"source","source":source])
        refreshRunningPlayers()
        if smoke {smokeTest();return}
        func connect(){self.reload();self.poll()}
        if source != "apple" {poll()}
        else if let url=NSWorkspace.shared.urlForApplication(withBundleIdentifier:"com.apple.Music") {
            let options=NSWorkspace.OpenConfiguration();options.activates=false
            NSWorkspace.shared.openApplication(at:url,configuration:options){_,error in
                DispatchQueue.main.async {if let error {self.notice(error.localizedDescription)} else {connect()}}
            }
        } else {notice(tr("找不到音乐 App。","The Music app could not be found."))}
        timer=Timer.scheduledTimer(withTimeInterval:1.1,repeats:true){[weak self] _ in
            guard let self else{return};self.refreshRunningPlayers();self.poll()
            if Date().timeIntervalSince(self.lastRead)>300 {self.reload()}
        }
    }
    func post(_ obj:[String:Any]) {
        guard ready,let bytes=try? JSONSerialization.data(withJSONObject:obj) else{return}
        web.evaluateJavaScript("window.receiveShelfMessage("+String(decoding:bytes,as:UTF8.self)+")",completionHandler:nil)
    }
    func notice(_ text:String){post(["type":"notice","message":text])}
    func reload(){
        guard source=="apple",!reading else{return};reading=true;lastRead=Date()
        let generation=sourceRevision
        work.async {
            do {
                let data=try Music.script(["library"])
                guard let input=try JSONSerialization.jsonObject(with:data) as? [[String:Any]] else{throw URLError(.cannotParseResponse)}
                var map=[String:String](),tracksByAlbum=[String:[String]](),albums=[[String:Any]]()
                for var row in input {
                    guard let id=row["id"] as? String else{continue}
                    let tracks=row.removeValue(forKey:"trackIDs") as? [String] ?? []
                    tracksByAlbum[id]=tracks
                    for track in tracks {map[track]=id}
                    row.removeValue(forKey:"order");row["cover"]=id;albums.append(row)
                }
                DispatchQueue.main.async {
                    self.reading=false;guard self.sourceRevision==generation else{return}
                    self.albumByTrack=map;self.tracksByAlbum=tracksByAlbum;self.resources.allowed=Set(map.values)
                    self.post(["type":"library","albums":albums]);self.lastState=Data();self.poll()
                }
            } catch {DispatchQueue.main.async{
                self.reading=false;guard self.sourceRevision==generation else{return}
                self.post(["type":"libraryError","message":self.tr("请在「系统设置 → 隐私与安全性 → 自动化」允许控制音乐，再刷新资料库。","Allow Music control in System Settings → Privacy & Security → Automation, then refresh the library.")+" "+error.localizedDescription])
            }}
        }
    }
    func poll(){
        guard !polling,!smoke,!source.isEmpty else{return};polling=true
        let selected=source,generation=sourceRevision
        if selected != "apple" {
            work.async {
                let state=(try? LocalPlayer.state(selected)) ?? ["available":false]
                DispatchQueue.main.async {
                    self.polling=false;guard self.sourceRevision==generation else{return}
                    let serial=(try? JSONSerialization.data(withJSONObject:state,options:.sortedKeys)) ?? Data()
                    if self.lastState != serial {self.lastState=serial;self.post(["type":"media","state":state])}
                    else {self.post(["type":"playbackCheck","playing":state["playing"] ?? false])}
                }
            }
            return
        }
        let wantsLyrics=lyrics.panel.isVisible
        work.async {
            do {
                let bytes=try Music.script(["state"])
                guard var state=try JSONSerialization.jsonObject(with:bytes) as? [String:Any] else{throw URLError(.cannotParseResponse)}
                let id=state["trackID"] as? String ?? ""
                var position:Double?
                if wantsLyrics,!id.isEmpty, let raw=try? Music.script(["position"]) {position=Double(String(decoding:raw,as:UTF8.self).trimmingCharacters(in:.whitespacesAndNewlines))}
                DispatchQueue.main.async {
                    self.polling=false;guard self.sourceRevision==generation else{return}
                    self.lastTrack=id;state["albumId"]=self.albumByTrack[id]
                    let tracks=self.tracksByAlbum[self.albumByTrack[id] ?? ""] ?? []
                    state["canRepeat"] = !tracks.isEmpty;state["canStep"] = tracks.count>1
                    let serial=(try? JSONSerialization.data(withJSONObject:state,options:.sortedKeys)) ?? Data()
                    if serial != self.lastState {self.lastState=serial;self.post(["type":"media","state":state])}
                    else {self.post(["type":"playbackCheck","playing":state["playing"] ?? false,"id":self.albumByTrack[id] ?? ""])}
                    if wantsLyrics {self.loadLyrics(id);if let position {self.lyrics.position(position)}}
                }
            } catch {DispatchQueue.main.async{self.polling=false}}
        }
    }
    func loadLyrics(_ id:String) {
        guard id != lyricsTrack else{return};lyricsTrack=id
        let fallback=tr("音乐未提供这首歌的歌词。","Music has no readable lyrics for this track.")
        lyrics.load("",trackID:id,fallback:fallback)
        guard !id.isEmpty else{return}
        work.async {
            let text=(try? Music.script(["lyrics"])).map{String(decoding:$0,as:UTF8.self)} ?? ""
            DispatchQueue.main.async {if self.lyricsTrack==id {self.lyrics.load(text,trackID:id,fallback:fallback)}}
        }
    }
    func run(_ args:[String],serial:Int?=nil){
        work.async {
            do {_=try Music.script(args);DispatchQueue.main.async{self.poll()}}
            catch {DispatchQueue.main.async{if let serial {self.post(["type":"playFailed","serial":serial,"message":error.localizedDescription])}else{self.notice(error.localizedDescription)}}}
        }
    }
    func selectSource(_ value:String){
        guard source != value else{return}
        source=value;sourceRevision+=1
        if !source.isEmpty{defaults.set(source,forKey:"source")}
        lyrics.panel.orderOut(nil);lyricsTrack="";lastTrack="";lastState=Data();lastRead = .distantPast
        albumByTrack=[:];tracksByAlbum=[:];resources.allowed=[]
        post(["type":"source","source":source])
    }
    func userContentController(_ controller:WKUserContentController,didReceive message:WKScriptMessage) {
        guard message.frameInfo.isMainFrame,message.frameInfo.request.url?.host=="ui",let cmd=message.body as? String else{return}
        let f=cmd.components(separatedBy:":")
        if f[0]=="source" {
            guard f.count==2, f[1] != source, installedPlayers.contains(f[1]) else{return}
            selectSource(f[1]);reload();poll();return
        }
        if source != "apple" {
            let command=f[0]
            if ["lyrics","open"].contains(command){return}
            if ["resume","toggle","previousTrack","nextTrack","repeatOne","repeatAlbum","repeatOff"].contains(command){
                let selected=source,generation=sourceRevision
                work.async {
                    var reply:[String:Any]=["type":"albumCommandDone"]
                    do {try LocalPlayer.control(selected,command)}
                    catch {
                        reply["message"]=self.tr("播放器未接受操作。","The player did not accept the command.")
                        if command=="resume",f.count>1 {reply["type"]="playFailed";reply["serial"]=Int(f[1])}
                    }
                    DispatchQueue.main.async {
                        guard self.sourceRevision==generation else{return}
                        self.post(reply);self.poll()
                    }
                }
                return
            }
        }
        switch f[0] {
        case "browse":
            guard f.count>1,let n=Int(f[1]) else{return}
            idle?.cancel();let job=DispatchWorkItem{self.post(["type":"idle","sequence":n])}
            idle=job;DispatchQueue.main.asyncAfter(deadline:.now()+2.5,execute:job)
        case "size":if f.count>1 {resize(f[1],animated:!f.contains("instant"))}
        case "pin":pinned.toggle();defaults.set(pinned,forKey:"pin");applyLevel();announceLevel()
        case "dock":docked.toggle();pinned=false;defaults.set(false,forKey:"pin");defaults.set(docked,forKey:"desktop");applyLevel();announceLevel()
        case "dragStart":origin=window.frame.origin;anchor=NSEvent.mouseLocation
        case "drag":let p=NSEvent.mouseLocation;window.setFrameOrigin(NSPoint(x:origin.x+p.x-anchor.x,y:origin.y+p.y-anchor.y))
        case "dragEnd":snap(animated:!f.contains("instant"))
        case "reload":if !smoke {reload()}
        case "syncPlayback":poll()
        case "open":
            guard f.count==3,let serial=Int(f[1]),resources.allowed.contains(f[2]) else{return}
            if smoke {post(["type":"playbackCheck","playing":true,"id":f[2]]);return}
            run(albumByTrack[lastTrack]==f[2] ? ["resume"] : ["play",f[2]],serial:serial)
        case "resume":if !smoke {run(["resume"],serial:f.count>1 ? Int(f[1]) : nil)}
        case "previousTrack","nextTrack","repeatOne","repeatAlbum","repeatOff":
            guard !smoke,let album=albumByTrack[lastTrack],let tracks=tracksByAlbum[album],
                  let bytes=try? JSONEncoder().encode(tracks) else {post(["type":"albumCommandDone"]);return}
            let command=f[0]
            work.async {
                var reply:[String:Any]=["type":"albumCommandDone"]
                do {
                    let result=try Music.script([command,String(decoding:bytes,as:UTF8.self)])
                    if command.hasPrefix("repeat") {
                        reply["repeatMode"]=String(decoding:result,as:UTF8.self).trimmingCharacters(in:.whitespacesAndNewlines)
                    }
                }
                catch {reply["message"]=error.localizedDescription}
                DispatchQueue.main.async {self.poll();self.post(reply)}
            }
        case "toggle":if !smoke {run(["toggle"])}
        case "lyrics":
            attachLyrics();lyrics.panel.orderFront(nil);lyricsTrack="!";loadLyrics(lastTrack)
        case "close":NSApp.terminate(nil)
        default:break
        }
    }
    func announceLevel(){post(["type":"dock","active":docked && !pinned,"pinned":pinned])}
    func applyLevel(){
        window.level=pinned ? .floating : (docked ? NSWindow.Level(rawValue:Int(CGWindowLevelForKey(.desktopWindow))+1) : .normal)
        window.collectionBehavior=[.canJoinAllSpaces,.fullScreenAuxiliary]
        lyrics?.panel.level=window.level
    }
    func resize(_ request:String,animated:Bool) {
        guard ["classic","half","wide"].contains(request),let area=(window.screen ?? NSScreen.main)?.visibleFrame else{return}
        var r=window.frame;let mid=r.midX
        r.size=NSSize(width:request=="wide" ? area.width : (request=="half" ? area.width/2 : min(area.width,472)),height:306)
        r.origin.x=max(area.minX,min(mid-r.width/2,area.maxX-r.width))
        r.origin.y=max(area.minY,min(r.minY,area.maxY-r.height))
        mode=request;if !smoke {defaults.set(request,forKey:"mode")}
        move(r,animated:animated);post(["type":"size","mode":request])
    }
    func move(_ r:NSRect,animated:Bool){
        if animated && !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion {
            NSAnimationContext.runAnimationGroup{ctx in ctx.duration=0.25;window.animator().setFrame(r,display:true)}
        }else{window.setFrame(r,display:true)}
    }
    func snap(animated:Bool){
        guard let area=window.screen?.visibleFrame else{return}
        var r=window.frame
        func align(_ v:CGFloat,_ values:[CGFloat])->CGFloat {values.min(by:{abs($0-v)<abs($1-v)}).flatMap{abs($0-v)<18 ? $0:nil} ?? v}
        r.origin.x=align(r.minX,[area.minX,area.midX-r.width/2,area.maxX-r.width])
        r.origin.y=align(r.minY,[area.minY,area.midY-r.height/2,area.maxY-r.height])
        move(r,animated:animated)
    }
    var lyricOrigin:NSPoint?
    func attachLyrics(){
        guard let area=window.screen?.visibleFrame else{return}
        let r=window.frame,p=lyrics.panel.frame
        let point=NSPoint(x:max(area.minX,min(r.midX-p.width/2,area.maxX-p.width)),y:r.minY-p.height-4>=area.minY ? r.minY-p.height-4 : min(area.maxY-p.height,r.maxY+4))
        lyrics.panel.setFrameOrigin(point);lyrics.panel.level=window.level;lyricOrigin=point
    }
    func windowDidMove(_ note:Notification){followWindow()}
    func windowDidResize(_ note:Notification){followWindow()}
    func followWindow(){
        if !smoke {window.saveFrame(usingName:"CoverShelf")}
        if let lyrics,lyrics.panel.isVisible,let last=lyricOrigin,abs(lyrics.panel.frame.minX-last.x)+abs(lyrics.panel.frame.minY-last.y)<3 {attachLyrics()}
    }
    func windowWillClose(_ note:Notification){NSApp.terminate(nil)}
    func smokeTest(){
        do {_=try LocalPlayer.state("spotify",artwork:false)}
        catch {fputs("Bundled Now Playing reader failed: "+error.localizedDescription+"\n",stderr);exit(9)}
        for (source,bundle) in LocalPlayer.bundles {
            let row:[String:Any]=["bundleIdentifier":bundle,"title":"Fixture track","playing":true,
                                  "supportedCommands":[["id":1,"enabled":true],["id":4,"enabled":true]]]
            let state=LocalPlayer.state(source,metadata:row)
            guard state["title"] as? String=="Fixture track",state["canNext"] as? Bool==true,
                  state["canPrevious"] as? Bool==false,state["canPause"] as? Bool==true,
                  LocalPlayer.state(source=="qq" ? "kugou":"qq",metadata:row)["available"] as? Bool==false else {exit(8)}
        }
        resources.fixture=true;resources.allowed=["1","2","3"]
        let albums=(1...3).map{["id":String($0),"title":"Album "+String($0),"artist":"Self Coverflow","cover":String($0),"favorite":true] as [String:Any]}
        post(["type":"library","albums":albums])
        DispatchQueue.main.asyncAfter(deadline:.now()+1.5){
            self.web.evaluateJavaScript("(()=>{document.querySelector('[data-size=half]').click();return {n:all.length,lang:document.documentElement.lang,repeatVisible:!document.getElementById('repeat').hidden,cover:!!document.querySelector('.cover img')?.naturalWidth}})()"){value,error in
                guard error==nil,let r=value as? [String:Any],r["n"] as? Int==3,r["cover"] as? Bool==true,r["repeatVisible"] as? Bool==true,(r["lang"] as? String)?.hasPrefix(self.en ? "en":"zh")==true else {fputs("WK smoke failed: "+String(describing:value)+" "+String(describing:error)+"\n",stderr);exit(3)}
                DispatchQueue.main.asyncAfter(deadline:.now()+0.4){exit(self.mode=="half" ? 0:4)}
            }
        }
    }
}
let app=NSApplication.shared
let shelf=Shelf()
app.setActivationPolicy(.accessory)
app.delegate=shelf
app.run()

