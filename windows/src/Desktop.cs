using System;
using System.Drawing;
using System.Runtime.InteropServices;

internal static class Desktop
{
    delegate bool EnumProc(IntPtr window,IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc,IntPtr data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent,IntPtr after,string cls,string name);
    [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr child,IntPtr parent);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr child);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h,int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h,int index,int value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int height,uint flags);
    [DllImport("user32.dll")] internal static extern bool ScreenToClient(IntPtr h,ref Point p);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr handle,out NativeRect rect);
    struct NativeRect { public int left,top,right,bottom; }
    const uint NoSize=1, NoMove=2, NoZOrder=4, NoActivate=16, FrameChanged=32, Show=64, Hide=128, NoOwnerZOrder=512;
    internal static Rectangle Bounds(IntPtr handle)
    {
        NativeRect r;
        if(!GetWindowRect(handle,out r))throw new InvalidOperationException("GetWindowRect failed");
        return Rectangle.FromLTRB(r.left,r.top,r.right,r.bottom);
    }
    static bool Reparent(IntPtr handle,IntPtr destination)
    {
        var bounds=Bounds(handle);
        const int popup=unchecked((int)0x80000000), child=0x40000000, visibleStyle=0x10000000;
        int style=GetWindowLong(handle,-16);
        var previous=(style & child)!=0?GetParent(handle):IntPtr.Zero;
        if(previous==destination)return true;
        bool visible=IsWindowVisible(handle);
        uint flags=NoActivate|NoZOrder|NoOwnerZOrder;
        if(visible)SetWindowPos(handle,IntPtr.Zero,0,0,0,0,flags|NoMove|NoSize|Hide);
        int hiddenStyle=style & ~visibleStyle;
        if(destination!=IntPtr.Zero)SetWindowLong(handle,-16,(hiddenStyle & ~popup)|child);
        var oldParent=SetParent(handle,destination);
        if(destination==IntPtr.Zero && oldParent!=IntPtr.Zero)
            SetWindowLong(handle,-16,(hiddenStyle & ~child)|popup);
        var parent=(GetWindowLong(handle,-16)&child)!=0?GetParent(handle):IntPtr.Zero;
        bool changed=parent==destination;
        if(!changed)SetWindowLong(handle,-16,hiddenStyle);
        var point=bounds.Location;
        if(parent!=IntPtr.Zero)ScreenToClient(parent,ref point);
        SetWindowPos(handle,IntPtr.Zero,point.X,point.Y,bounds.Width,bounds.Height,flags|FrameChanged);
        if(visible)SetWindowPos(handle,IntPtr.Zero,0,0,0,0,flags|NoMove|NoSize|Show);
        return changed;
    }
    internal static bool IsTopmost(IntPtr window) { return (GetWindowLong(window,-20)&8)!=0; }
    internal static void SetPinned(IntPtr window,bool pinned)
    {
        SetWindowPos(window,new IntPtr(pinned?-1:-2),0,0,0,0,NoMove|NoSize|NoActivate|NoOwnerZOrder);
    }
    internal static void Raise(IntPtr window)
    {
        SetWindowPos(window,IsTopmost(window)?new IntPtr(-1):IntPtr.Zero,0,0,0,0,
            NoMove|NoSize|NoActivate|NoOwnerZOrder|Show);
    }
    internal static IntPtr Attach(IntPtr handle)
    {
        IntPtr shell=IntPtr.Zero;
        EnumWindows(delegate(IntPtr w,IntPtr unused) {
            if(FindWindowEx(w,IntPtr.Zero,"SHELLDLL_DefView",null)==IntPtr.Zero) return true;
            shell=w; return false;
        },IntPtr.Zero);
        if(shell==IntPtr.Zero) return IntPtr.Zero;
        return Reparent(handle,shell)?shell:IntPtr.Zero;
    }
    internal static bool Detach(IntPtr handle)
    {
        return Reparent(handle,IntPtr.Zero);
    }
    internal static void Move(IntPtr handle,IntPtr parent,Point screen)
    {
        if(parent!=IntPtr.Zero) ScreenToClient(parent,ref screen);
        SetWindowPos(handle,IntPtr.Zero,screen.X,screen.Y,0,0,0x15);
    }

    internal static void Place(IntPtr handle,IntPtr parent,Rectangle bounds)
    {
        var old=Bounds(handle);
        if(bounds==old)return;
        uint flags=NoActivate|NoZOrder|NoOwnerZOrder;
        if(bounds.Size==old.Size)flags|=NoSize;
        if(bounds.Location==old.Location)flags|=NoMove;
        var p=bounds.Location;
        if(parent!=IntPtr.Zero) ScreenToClient(parent,ref p);
        SetWindowPos(handle,IntPtr.Zero,p.X,p.Y,bounds.Width,bounds.Height,flags);
    }
    internal static int Attract(int raw,int start,int end,int size,int threshold,out int landing)
    {
        int nearest=start;
        foreach(int guide in new[]{start+(end-start-size)/2,end-size})
            if(Math.Abs(raw-guide)<Math.Abs(raw-nearest)) nearest=guide;
        int distance=Math.Abs(raw-nearest);
        landing=distance<=threshold?nearest:raw;
        double proximity=Math.Max(0,1-distance/(threshold*2.0));
        double weight=.82*proximity*proximity*(3-2*proximity);
        return raw+(int)Math.Round((nearest-raw)*weight);
    }
}
