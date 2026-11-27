using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class AppLaunch
{
    internal const string Family = "AppleInc.AppleMusicWin_nzyj5cx40ttqa";
    internal const string WidgetMutex = @"Local\CoverShelfWidget"+Edition.Suffix;
    const string AppId = Family + "!App";

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
    }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)]
    static extern int GetPackageFamilyName(IntPtr process, ref uint length, StringBuilder family);
    [DllImport("ole32.dll")]
    static extern int CoAllowSetForegroundWindow([MarshalAs(UnmanagedType.IUnknown)] object proxy, IntPtr reserved);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hWnd, int command);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);

    internal static void OpenMusic()
    {
        using(var self=Process.GetCurrentProcess())
            if(MusicWindowProcess(self.SessionId)!=0)return;
        var activation = (IApplicationActivationManager)Activator.CreateInstance(
            Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"), true));
        try {
            uint pid;
            CoAllowSetForegroundWindow(activation, IntPtr.Zero);
            Marshal.ThrowExceptionForHR(activation.ActivateApplication(AppId, null, 0, out pid));
            if (pid != 0) using (var app = Process.GetProcessById((int)pid)) {
                IntPtr window = app.MainWindowHandle;
                if (window != IntPtr.Zero) {
                    if (IsIconic(window)) ShowWindowAsync(window, 9);
                    SetForegroundWindow(window);
                }
            }
        } finally { Marshal.ReleaseComObject(activation); }
    }

    static int MusicWindowProcess(int session)
    {
        int result = 0;
        foreach (var p in Process.GetProcessesByName("AppleMusic"))
        using (p) {
            try {
                if (p.SessionId != session || p.MainWindowHandle == IntPtr.Zero) continue;
                var family = new StringBuilder(128);
                uint length = (uint)family.Capacity;
                if (GetPackageFamilyName(p.Handle, ref length, family) == 0 && family.ToString() == Family)
                    result = p.Id;
            } catch (InvalidOperationException) { /* The window closed during detection. */ }
              catch (Win32Exception) { /* The process is exiting or belongs to another user. */ }
        }
        return result;
    }

    internal static void WatchMusic()
    {
        bool first;
        using (var watcher = new Mutex(true, @"Local\CoverShelfMusicWatcher", out first)) {
            if (!first) return;
            int session;
            using (var self = Process.GetCurrentProcess()) session = self.SessionId;
            int previous = 0;
            while (true) {
                string source=Shelf.SavedSource();
                int current=0;
                if(source=="apple")current=MusicWindowProcess(session);
                else {
                    string name=source=="qq"?"QQMusic":source=="kugou"?"KuGou":"Spotify";
                    foreach(var app in Process.GetProcessesByName(name))using(app){
                        try{if(app.SessionId==session && app.MainWindowHandle!=IntPtr.Zero){current=app.Id;break;}}
                        catch(InvalidOperationException){}
                    }
                }
                if (current != 0 && current != previous) {
                    Mutex widget;
                    if (Mutex.TryOpenExisting(@"Local\CoverShelfWidget", out widget) ||
                        Mutex.TryOpenExisting(@"Local\CoverShelfWidget-EN", out widget)) widget.Dispose();
                    else {
                        try {
                            using (Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--from-music")
                                     {UseShellExecute=false})) {}
                        } catch (Win32Exception) { Thread.Sleep(2000); continue; }
                    }
                }
                previous = current;
                Thread.Sleep(1500);
            }
        }
    }
}
