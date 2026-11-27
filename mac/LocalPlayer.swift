import Foundation

enum LocalPlayer {
    static let bundles = ["spotify":"com.spotify.client", "qq":"com.tencent.QQMusicMac", "kugou":"com.kugou.mac.Music"]

    private static func call(_ args:[String]) throws -> Data {
        let task=Process(), output=Pipe()
        task.executableURL=URL(fileURLWithPath:"/usr/bin/perl")
        task.arguments=[Music.resources.appendingPathComponent("mediaremote-adapter.pl").path,
                        Bundle.main.privateFrameworksURL!.appendingPathComponent("MediaRemoteAdapter.framework").path]+args
        task.standardOutput=output
        task.standardError=FileHandle.nullDevice
        try task.run()
        let timeout=DispatchWorkItem { if task.isRunning {task.terminate()} }
        DispatchQueue.global().asyncAfter(deadline:.now()+5,execute:timeout)
        defer {timeout.cancel()}
        let data=output.fileHandleForReading.readDataToEndOfFile()
        task.waitUntilExit()
        guard task.terminationStatus==0 else {throw CocoaError(.executableRuntimeMismatch)}
        return data
    }

    static func state(_ source:String, artwork:Bool=true) throws -> [String:Any] {
        let data=try call(artwork ? ["get"] : ["get","--no-artwork"])
        guard !data.isEmpty,
              let row=try JSONSerialization.jsonObject(with:data,options:.fragmentsAllowed) as? [String:Any]
        else {return ["available":false]}
        return state(source,metadata:row)
    }

    static func state(_ source:String, metadata row:[String:Any]) -> [String:Any] {
        guard let bundle=bundles[source],
              [row["bundleIdentifier"] as? String,row["parentApplicationBundleIdentifier"] as? String].contains(bundle)
        else {return ["available":false]}
        let playing=row["playing"] as? Bool ?? false
        let commands=row["supportedCommands"] as? [[String:Any]]
        func supports(_ id:Int)->Bool {
            commands?.contains { $0["id"] as? Int==id && $0["enabled"] as? Bool==true } ?? false
        }
        var state:[String:Any]=["available":true,"playing":playing,"paused":!playing,
            "title":row["title"] as? String ?? "","album":row["album"] as? String ?? "",
            "artist":row["artist"] as? String ?? "","canPlay":supports(0) || supports(2),
            "canPause":supports(1) || supports(2),"canNext":supports(4),"canPrevious":supports(5),
            "playDirect":supports(0),"pauseDirect":supports(1)]
        if let repeatMode=row["repeatMode"] as? Int, (1...3).contains(repeatMode),
           commands?.contains(where: { ($0["name"] as? String ?? "").lowercased().contains("repeat") && $0["enabled"] as? Bool==true })==true {
            state["canRepeat"]=true
            state["repeatMode"]=repeatMode==2 ? "one":repeatMode==3 ? "album":"off"
        }
        if let mime=row["artworkMimeType"] as? String,["image/jpeg","image/png","image/webp"].contains(mime),
           let art=row["artworkData"] as? String,art.utf8.count<=6_000_000 {state["artwork"]="data:"+mime+";base64,"+art}
        return state
    }

    static func control(_ source:String,_ command:String) throws {
        let current=try state(source,artwork:false)
        if let mode=["repeatOff":"1","repeatOne":"2","repeatAlbum":"3"][command] {
            guard current["canRepeat"] as? Bool==true else {throw CocoaError(.featureUnsupported)}
            _=try call(["repeat",mode]);return
        }
        let key:String, id:Int
        switch command {
        case "resume":
            if current["playing"] as? Bool==true{return}
            key="canPlay";id=current["playDirect"] as? Bool==true ? 0:2
        case "toggle": key="canPause";id=current["pauseDirect"] as? Bool==true ? 1:2
            if current["paused"] as? Bool==true{return}
        case "previousTrack": key="canPrevious";id=5
        case "nextTrack": key="canNext";id=4
        default:throw CocoaError(.featureUnsupported)
        }
        guard current[key] as? Bool==true else {throw CocoaError(.featureUnsupported)}
        _=try call(["send",String(id)])
    }
}
