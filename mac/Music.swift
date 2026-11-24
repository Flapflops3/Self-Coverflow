import AppKit
import WebKit

enum Music {
    static let resources = Bundle.main.resourceURL!
    static func script(_ args: [String], artwork: Bool = false) throws -> Data {
        let task = Process(), pipe = Pipe()
        task.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
        task.arguments = artwork
            ? [resources.appendingPathComponent("artwork.scpt").path] + args
            : ["-l", "JavaScript", resources.appendingPathComponent("music.js").path] + args
        task.standardOutput = pipe
        task.standardError = pipe
        try task.run()
        let timeout = DispatchWorkItem { if task.isRunning { task.terminate() } }
        DispatchQueue.global().asyncAfter(deadline: .now() + (args.first == "library" ? 100 : 18), execute: timeout)
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        task.waitUntilExit()
        timeout.cancel()
        guard task.terminationStatus == 0 else {
            throw NSError(domain:"Music", code:Int(task.terminationStatus), userInfo:[
                NSLocalizedDescriptionKey:String(decoding:data, as:UTF8.self).trimmingCharacters(in:.whitespacesAndNewlines)])
        }
        return data
    }
}

// Serve bundled UI and local album art without a network server.
final class Resources: NSObject, WKURLSchemeHandler {
    private let queue = OperationQueue()
    private var active = [ObjectIdentifier: Operation]()
    private let cache: URL
    var allowed = Set<String>()
    var fixture = false
    override init() {
        cache = FileManager.default.urls(for:.cachesDirectory,in:.userDomainMask)[0]
            .appendingPathComponent(Bundle.main.bundleIdentifier!).appendingPathComponent("covers")
        super.init()
        queue.maxConcurrentOperationCount = 2
        try? FileManager.default.createDirectory(at:cache,withIntermediateDirectories:true)
    }
    func webView(_ webView: WKWebView, start task: WKURLSchemeTask) {
        guard let url=task.request.url else { return }
        let name=url.lastPathComponent
        func deliver(_ data:Data,_ mime:String) {
            task.didReceive(URLResponse(url:url,mimeType:mime,expectedContentLength:data.count,textEncodingName:nil))
            task.didReceive(data); task.didFinish()
        }
        if url.host=="ui", ["index.html","bridge.js","shelf.js","shelf.css","strings.js","drive-sound.js","disc-drive.wav"].contains(name),
           let data=try? Data(contentsOf:Music.resources.appendingPathComponent("ui").appendingPathComponent(name)) {
            let types=["html":"text/html","js":"application/javascript","css":"text/css","wav":"audio/wav"]
            deliver(data,types[url.pathExtension] ?? "application/octet-stream");return
        }
        guard url.host=="art", allowed.contains(name), UInt64(name) != nil else {
            task.didFailWithError(URLError(.fileDoesNotExist)); return
        }
        if fixture {
            if let data=try? Data(contentsOf:Music.resources.appendingPathComponent("fixture.jpg")) { deliver(data,"image/jpeg") }
            else { task.didFailWithError(URLError(.fileDoesNotExist)) }
            return
        }
        let path=cache.appendingPathComponent(name+".jpg"),key=ObjectIdentifier(task)
        let op=BlockOperation()
        op.addExecutionBlock { [weak self, weak op] in
            guard let self, op?.isCancelled==false else {return}
            var bytes=try? Data(contentsOf:path)
            if bytes==nil {
                let temp=self.cache.appendingPathComponent(UUID().uuidString)
                defer { try? FileManager.default.removeItem(at:temp) }
                if (try? Music.script([name,temp.path],artwork:true)) != nil,
                   let image=NSImage(contentsOf:temp) {
                    let size=NSSize(width:320,height:320)
                    let thumb=NSImage(size:size)
                    thumb.lockFocus()
                    image.draw(in:NSRect(origin:.zero,size:size))
                    thumb.unlockFocus()
                    if let tiff=thumb.tiffRepresentation, let rep=NSBitmapImageRep(data:tiff) {
                        bytes=rep.representation(using:.jpeg,properties:[.compressionFactor:0.88])
                        try? bytes?.write(to:path,options:.atomic)
                    }
                }
            }
            let result=bytes
            DispatchQueue.main.async {
                guard self.active.removeValue(forKey:key) != nil else {return}
                if let result {deliver(result,"image/jpeg")} else {task.didFailWithError(URLError(.fileDoesNotExist))}
            }
        }
        active[key]=op;queue.addOperation(op)
    }
    func webView(_ webView: WKWebView, stop task: WKURLSchemeTask) {
        active.removeValue(forKey:ObjectIdentifier(task))?.cancel()
    }
}

