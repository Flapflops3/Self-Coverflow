using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using NativeUI;
using Element=NativeUI.IUIAutomationElement;
using Property=NativeUI.UIA_PropertyIds;
using PatternId=NativeUI.UIA_PatternIds;

internal static class MusicClient
{
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent,IntPtr previous,string cls,string title);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr handle,out int pid);
    [ThreadStatic] static IUIAutomation2 client;
    static IUIAutomation2 Client {
        get{
            if(client==null){client=(IUIAutomation2)new CUIAutomation8();client.AutoSetFocus=0;}
            return client;
        }
    }
    const TreeScope Descendants=TreeScope.TreeScope_Descendants;
    static Element MainWindow()
    {
        IntPtr window=IntPtr.Zero;
        while((window=FindWindowEx(IntPtr.Zero,window,"WinUIDesktopWin32WindowClass","Apple Music"))!=IntPtr.Zero){
            int pid;GetWindowThreadProcessId(window,out pid);
            using(var process=Process.GetProcessById(pid))
                if(process.ProcessName=="AppleMusic")return Client.ElementFromHandle(window);
        }
        return null;
    }
    static Element Find(Element root,int property,object value)
    {
        return root==null?null:root.FindFirst(Descendants,Client.CreatePropertyCondition(property,value));
    }
    static Element Id(Element root,string id){return Find(root,Property.UIA_AutomationIdPropertyId,id);}
    static T Pattern<T>(Element element,int key) where T:class
    {
        return element==null?null:element.GetCurrentPattern(key) as T;
    }
    static bool Invoke(Element element)
    {
        var action=Pattern<IUIAutomationInvokePattern>(element,PatternId.UIA_InvokePatternId);
        if(action==null || element.CurrentIsEnabled==0)return false;
        action.Invoke();return true;
    }
    static bool Matches(string label,Album album,bool uniqueTitle)
    {
        if(String.IsNullOrEmpty(label))return false;
        if(string.Equals(label,album.title+" "+album.artist,StringComparison.OrdinalIgnoreCase))return true;
        return uniqueTitle && label.StartsWith(album.title+" ",StringComparison.OrdinalIgnoreCase);
    }
    static readonly Dictionary<string,double> albumPages=new Dictionary<string,double>(StringComparer.OrdinalIgnoreCase);
    static Element AlbumButton(Element root,Album album,bool uniqueTitle){
        var header=Id(root,"ContainerDetailHeaderLockupRegularContent") ?? Id(root,"SingleItemSectionContent");
        if(header==null || Find(header,Property.UIA_NamePropertyId,album.title)==null)return null;
        if(!uniqueTitle && Find(header,Property.UIA_NamePropertyId,album.artist)==null)return null;
        return Id(header,"PlayButtonElement") ?? Id(root,"PlayButtonElement");
    }
    internal static bool Play(Album album,bool uniqueTitle,Func<bool> cancelled)
    {
        var root=MainWindow();if(root==null || cancelled())return false;
        var current=AlbumButton(root,album,uniqueTitle);
        if(current!=null && !cancelled())return Invoke(current);
        var nav=Id(root,"Sidebar_Library_Albums");
        if(nav==null){
            var header=Pattern<IUIAutomationExpandCollapsePattern>(Id(root,"Sidebar_Header_Library"),PatternId.UIA_ExpandCollapsePatternId);
            if(header!=null && header.CurrentExpandCollapseState==ExpandCollapseState.ExpandCollapseState_Collapsed)header.Expand();
            nav=Id(root,"Sidebar_Library_Albums");
        }
        var select=Pattern<IUIAutomationSelectionItemPattern>(nav,PatternId.UIA_SelectionItemPatternId);
        if(select==null || cancelled())return false;
        // Apple Music keeps Albums selected while showing a library album's detail page.
        // Selecting it again leaves that page open. Back restores the album grid.
        if(select.CurrentIsSelected!=0 && Id(root,"ContainerDetailHeaderLockupRegularContent")!=null){
            if(!Invoke(Id(root,"NavigationViewBackButton")))return false;
        }else select.Select();
        Element grid=null;
        var libraryGrid=Client.CreateAndCondition(Client.CreatePropertyCondition(Property.UIA_ClassNamePropertyId,"GridView"),
            Client.CreatePropertyCondition(Property.UIA_AutomationIdPropertyId,""));
        for(int i=0;i<24 && !cancelled() && grid==null;i++){
            grid=root.FindFirst(Descendants,libraryGrid);
            if(grid==null)Thread.Sleep(75);
        }
        if(grid==null || cancelled())return false;
        var scroll=Pattern<IUIAutomationScrollPattern>(grid,PatternId.UIA_ScrollPatternId);
        double page=scroll==null?100:Math.Max(.5,Math.Min(100,scroll.CurrentVerticalViewSize));
        var cards=Client.CreatePropertyCondition(Property.UIA_ClassNamePropertyId,"GridViewItem");
        var cache=Client.CreateCacheRequest();cache.AddProperty(Property.UIA_NamePropertyId);
        double remembered;
        bool tryRemembered=albumPages.TryGetValue(album.title+" "+album.artist,out remembered);
        for(double pageAt=-1;!cancelled();){
            if(pageAt>=0 && scroll!=null && scroll.CurrentVerticallyScrollable!=0){
                scroll.SetScrollPercent(-1,pageAt);Thread.Sleep(65);
            }
            var items=grid.FindAllBuildCache(TreeScope.TreeScope_Children,cards,cache);
            double actual=scroll==null?0:Math.Max(0,scroll.CurrentVerticalScrollPercent);
            for(int n=0;n<items.Length;n++){
                var item=items.GetElement(n);
                string label=item.CachedName;
                if(!string.IsNullOrEmpty(label))albumPages[label]=actual;
                if(!Matches(label,album,uniqueTitle))continue;
                var title=Id(item,"TitleTextBlock");
                if(title==null || !string.Equals(title.CurrentName,album.title,StringComparison.OrdinalIgnoreCase))continue;
                if(cancelled() || !Invoke(item))return false;
                for(int i=0;i<28 && !cancelled();i++){
                    var play=AlbumButton(root,album,uniqueTitle);
                    if(play!=null && !cancelled())return Invoke(play);
                    Thread.Sleep(90);
                }
                return false;
            }
            if(scroll==null || scroll.CurrentVerticallyScrollable==0)break;
            if(pageAt<0 && tryRemembered){pageAt=remembered;continue;}
            if(tryRemembered){tryRemembered=false;pageAt=0;continue;}
            if(pageAt>=100)break;
            pageAt=pageAt<0?0:Math.Min(100,pageAt+page);
        }
        return false;
    }

    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);

    internal sealed class Lyrics {
        internal string[] Lines=new string[0];
        internal int First=-1;
        internal int[] Buttons,PanelId;
        internal Lyrics At(int index){var next=(Lyrics)MemberwiseClone();next.First=index;return next;}
    }

    // A WinUI scroll viewer reports its destination before its visual transition ends.
    // Map that destination to the observed row tops while the native view is at rest.
    internal sealed class LyricTracker
    {
        string[] lines;
        double[] tops;
        int width,height;
        double previous=-1,anchor;
        readonly Stopwatch sinceChange=Stopwatch.StartNew();
        readonly Stopwatch sinceRead=new Stopwatch();
        IUIAutomationScrollPattern viewport;
        Lyrics snapshot;
        double nativeView,nativeDpi;

        internal void Remember(Lyrics data,IUIAutomationScrollPattern scroll,double view,double dpi){
            snapshot=data;viewport=scroll;nativeView=view;nativeDpi=dpi;sinceRead.Restart();
        }
        internal Lyrics FastFrame(){
            if(snapshot==null || sinceRead.ElapsedMilliseconds>=900)return null;
            if(viewport==null || tops==null || snapshot.Lines.Length==0)
                return sinceRead.ElapsedMilliseconds<350?snapshot:null;
            double percent;
            try{percent=viewport.CurrentVerticalScrollPercent;}catch(COMException){viewport=null;return null;}
            if(percent<0 || nativeView<=0)return null;
            double dest=height*(100/nativeView-1)*percent/100+anchor;
            int match=-1;double best=Math.Max(6,nativeDpi/10);
            for(int i=0;i<tops.Length;i++){
                double error=Math.Abs(tops[i]-dest);
                if(error<=best){match=i;best=error;}
            }
            if(match<0)return null;
            if(Math.Abs(previous-percent)>.001){previous=percent;sinceChange.Restart();}
            return snapshot.First==match?snapshot:snapshot.At(match);
        }

        internal int Locate(string[] text,double[] visibleTops,int w,int h,double dpi,double percent,double view)
        {
            if(text.Length==0)return -1;
            if(lines==null || !text.SequenceEqual(lines) || width!=w || height!=h){
                lines=text;tops=Enumerable.Repeat(double.NaN,text.Length).ToArray();
                previous=-1;width=w;height=h;anchor=60*dpi/96.0;
            }
            double offset=view>0 && view<=100 && percent>=0 ? h*(100/view-1)*percent/100 : 0;
            bool initial=previous<0;
            if(initial || Math.Abs(percent-previous)>.001){previous=percent;sinceChange.Restart();}
            if(percent<=.001 && visibleTops[0]>0)anchor=visibleTops[0];
            bool steady=initial || sinceChange.ElapsedMilliseconds>=650;
            int visual=-1,projected=-1;double visualError=double.MaxValue,best=double.MaxValue;
            for(int i=0;i<text.Length;i++){
                double y=visibleTops[i];
                if(!double.IsNaN(y)){
                    if(steady)tops[i]=y+offset;
                    if(Math.Abs(y-anchor)<visualError){visualError=Math.Abs(y-anchor);visual=i;}
                }
                double error=Math.Abs(tops[i]-(offset+anchor));
                if(error<best){best=error;projected=i;}
            }
            return best<=Math.Max(6,dpi/10)?projected:visual;
        }
    }
    static IUIAutomationCondition LyricButtons(){
        return Client.CreatePropertyCondition(Property.UIA_ControlTypePropertyId,UIA_ControlTypeIds.UIA_ButtonControlTypeId);
    }
    internal static Lyrics ReadLyrics(LyricTracker tracker,bool open,ref bool openedHere)
    {
        var frame=open?null:tracker.FastFrame();if(frame!=null)return frame;
        var result=new Lyrics();var root=MainWindow();
        if(root==null)return result;
        if(open){
            var toggle=Pattern<IUIAutomationTogglePattern>(Id(root,"LyricsToggleButton"),PatternId.UIA_TogglePatternId);
            if(toggle!=null && toggle.CurrentToggleState==ToggleState.ToggleState_Off){toggle.Toggle();openedHere=true;}
        }
        var panel=Id(root,"TimeBasedLyrics");
        if(panel==null){
            var plain=Id(root,"StaticLyrics") ?? Id(root,"StaticLyricsTextBlock");
            if(plain!=null)result.Lines=(plain.CurrentName??"").Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries);
            tracker.Remember(result,null,100,96);
            return result;
        }
        var rect=panel.CurrentBoundingRectangle;
        var scroll=Pattern<IUIAutomationScrollPattern>(panel,PatternId.UIA_ScrollPatternId);
        var cache=Client.CreateCacheRequest();cache.AddProperty(Property.UIA_NamePropertyId);cache.AddProperty(Property.UIA_BoundingRectanglePropertyId);
        var buttons=panel.FindAllBuildCache(Descendants,LyricButtons(),cache);
        var lines=new List<string>();var indices=new List<int>();var tops=new List<double>();
        for(int n=0;n<buttons.Length;n++){
            var b=buttons.GetElement(n);var name=(b.CachedName??"").Trim();if(name.Length==0)continue;
            lines.Add(name);indices.Add(n);
            var box=b.CachedBoundingRectangle;
            tops.Add(box.bottom-box.top>3 && box.top>rect.top && box.top<rect.bottom?box.top-rect.top:double.NaN);
        }
        result.Lines=lines.ToArray();result.Buttons=indices.ToArray();result.PanelId=panel.GetRuntimeId();
        double dpi=GetDpiForWindow((IntPtr)root.CurrentNativeWindowHandle),view=scroll==null?100:scroll.CurrentVerticalViewSize;
        result.First=tracker.Locate(result.Lines,tops.ToArray(),rect.right-rect.left,rect.bottom-rect.top,dpi,
            scroll==null?-1:scroll.CurrentVerticalScrollPercent,view);
        tracker.Remember(result,scroll,view,dpi);
        return result;
    }
    internal static bool SeekLine(Lyrics snapshot,int index)
    {
        if(snapshot==null || snapshot.Buttons==null || index<0 || index>=snapshot.Lines.Length)return false;
        var panel=Id(MainWindow(),"TimeBasedLyrics");
        if(panel==null || !panel.GetRuntimeId().SequenceEqual(snapshot.PanelId))return false;
        var cache=Client.CreateCacheRequest();cache.AddProperty(Property.UIA_NamePropertyId);
        var rows=panel.FindAllBuildCache(Descendants,LyricButtons(),cache);
        // Use occurrence as well as text so repeated choruses keep distinct seek targets.
        if(snapshot.Buttons[snapshot.Buttons.Length-1]>=rows.Length)return false;
        for(int i=0;i<snapshot.Lines.Length;i++)
            if((rows.GetElement(snapshot.Buttons[i]).CachedName??"").Trim()!=snapshot.Lines[i])return false;
        return Invoke(rows.GetElement(snapshot.Buttons[index]));
    }
    internal static void CloseLyrics()
    {
        var toggle=Pattern<IUIAutomationTogglePattern>(Id(MainWindow(),"LyricsToggleButton"),PatternId.UIA_TogglePatternId);
        if(toggle!=null && toggle.CurrentToggleState==ToggleState.ToggleState_On)toggle.Toggle();
    }
}
