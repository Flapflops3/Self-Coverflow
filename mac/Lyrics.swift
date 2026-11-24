import AppKit

final class LyricPanel: NSObject, NSTextViewDelegate {
    let panel=NSPanel(contentRect:NSRect(x:0,y:0,width:340,height:225),
                      styleMask:[.titled,.closable,.resizable,.utilityWindow],backing:.buffered,defer:false)
    let text=NSTextView()
    var seek: ((Double,String)->Void)?
    private var track=""
    private var times=[(Double,NSRange)]()
    private var highlighted:NSRange?
    init(title:String) {
        super.init()
        panel.title=title;panel.isReleasedWhenClosed=false;panel.hidesOnDeactivate=false
        let scroll=NSScrollView(frame:panel.contentView!.bounds)
        scroll.autoresizingMask=[.width,.height];scroll.hasVerticalScroller=true
        text.isEditable=false;text.isSelectable=true;text.delegate=self
        text.isVerticallyResizable=true;text.autoresizingMask=[.width];text.textContainer?.widthTracksTextView=true
        text.textContainerInset=NSSize(width:14,height:12)
        scroll.documentView=text;panel.contentView=scroll
    }
    func load(_ lyrics:String, trackID:String, fallback:String) {
        track=trackID;times=[];highlighted=nil
        let out=NSMutableAttributedString(string:"")
        let re=try! NSRegularExpression(pattern:#"\[(\d+):(\d{1,2}(?:\.\d+)?)\]"#)
        var rows=[(Double?,String)]()
        for line in lyrics.components(separatedBy:.newlines) {
            let ns=line as NSString
            let matches=re.matches(in:line,range:NSRange(location:0,length:ns.length))
            if matches.isEmpty { rows.append((nil,line)); continue }
            let label=re.stringByReplacingMatches(in:line,range:NSRange(location:0,length:ns.length),withTemplate:"").trimmingCharacters(in:.whitespaces)
            for match in matches {rows.append(((Double(ns.substring(with:match.range(at:1))) ?? 0)*60+(Double(ns.substring(with:match.range(at:2))) ?? 0),label))}
        }
        if lyrics.trimmingCharacters(in:.whitespacesAndNewlines).isEmpty { rows=[(nil,fallback)] }
        if rows.allSatisfy({$0.0 != nil || $0.1.isEmpty}) {rows.sort {($0.0 ?? -1)<($1.0 ?? -1)}}
        for (time,label) in rows {
            let start=out.length
            out.append(NSAttributedString(string:label+"\n",attributes:[.font:NSFont.systemFont(ofSize:17,weight:.medium),.foregroundColor:NSColor.labelColor]))
            if let time,!label.isEmpty {let r=NSRange(location:start,length:(label as NSString).length);out.addAttribute(.link,value:"seek:"+String(time),range:r);times.append((time,r))}
        }
        text.textStorage?.setAttributedString(out)
    }
    func position(_ pos:Double) {
        guard let current=times.last(where:{$0.0<=pos})?.1,current != highlighted else{return}
        if let highlighted {text.textStorage?.removeAttribute(.backgroundColor,range:highlighted)}
        text.textStorage?.addAttribute(.backgroundColor,value:NSColor.selectedTextBackgroundColor,range:current)
        text.scrollRangeToVisible(current);highlighted=current
    }
    func textView(_ textView:NSTextView,clickedOnLink link:Any,at charIndex:Int)->Bool {
        if let s=link as? String,s.hasPrefix("seek:"),let t=Double(s.dropFirst(5)){seek?(t,track)}
        return true
    }
}

