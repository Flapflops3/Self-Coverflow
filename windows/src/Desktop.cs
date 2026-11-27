using System;
using System.Drawing;
using System.Runtime.InteropServices;

internal static class Desktop
{
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h,int index);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int height,uint flags);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr handle,out NativeRect rect);
    struct NativeRect { public int left,top,right,bottom; }
    const uint NoSize=1, NoMove=2, NoZOrder=4, NoActivate=16, Show=64, NoOwnerZOrder=512;
    internal static Rectangle Bounds(IntPtr handle)
    {
        NativeRect r;
        if(!GetWindowRect(handle,out r))throw new InvalidOperationException("GetWindowRect failed");
        return Rectangle.FromLTRB(r.left,r.top,r.right,r.bottom);
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
    internal static void Move(IntPtr handle,Point screen)
    {
        SetWindowPos(handle,IntPtr.Zero,screen.X,screen.Y,0,0,0x15);
    }

    internal static void Place(IntPtr handle,Rectangle bounds)
    {
        var old=Bounds(handle);
        if(bounds==old)return;
        uint flags=NoActivate|NoZOrder|NoOwnerZOrder;
        if(bounds.Size==old.Size)flags|=NoSize;
        if(bounds.Location==old.Location)flags|=NoMove;
        var p=bounds.Location;
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
