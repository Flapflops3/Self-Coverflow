using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

internal static class InstalledPlayers
{
    internal static string FromExecutable(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        switch (Path.GetFileName(path).ToLowerInvariant()) {
            case "applemusic.exe": return "apple";
            case "spotify.exe": return "spotify";
            case "qqmusic.exe": return "qq";
            case "kugou.exe": case "kgmusic.exe": return "kugou";
            default: return null;
        }
    }

    static string FromPackage(string appId,string name)
    {
        int split=appId.IndexOf('!');
        if (split<0) return null;
        string family=appId.Substring(0,split);
        if (family.Equals(AppLaunch.Family,StringComparison.OrdinalIgnoreCase)) return "apple";
        if (family.Equals("SpotifyAB.SpotifyMusic_zpdnekdrzrea0",StringComparison.OrdinalIgnoreCase)) return "spotify";
        // Store editions of QQ Music and KuGou can have localized display names.
        switch ((name ?? "").Replace(" ","").ToLowerInvariant()) {
            case "qqmusic": case "qqÒôÀÖ": case "qqÒô˜·": return "qq";
            case "kugoumusic": case "¿á¹·ÒôÀÖ": case "¿á¹·Òô˜·": return "kugou";
            default: return null;
        }
    }

    internal static HashSet<string> Discover()
    {
        var found=new HashSet<string>();
        dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application",true));
        dynamic folder=null,items=null;
        try {
            folder=shell.NameSpace("shell:AppsFolder");
            items=folder.Items();
            int count=items.Count;
            for (int i=0;i<count;i++) {
                dynamic item=items.Item(i);
                if (item==null) continue;
                try {
                    string id=(string)item.Path ?? "";
                    string source=id.Contains("!") ? FromPackage(id,(string)item.Name) : FromExecutable(id);
                    if (source==null && !id.Contains("!"))
                        source=FromExecutable(item.ExtendedProperty("System.Link.TargetParsingPath") as string);
                    if (source!=null) found.Add(source);
                } catch(COMException) { /* A shortcut can disappear during enumeration. */ }
                finally { Marshal.ReleaseComObject(item); }
            }
        } finally {
            if(items!=null) Marshal.ReleaseComObject(items);
            if(folder!=null) Marshal.ReleaseComObject(folder);
            Marshal.ReleaseComObject(shell);
        }
        // Some desktop installers register an uninstall entry but no Start menu shortcut.
        foreach(var hive in new[]{RegistryHive.CurrentUser,RegistryHive.LocalMachine})
        foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32})
        using(var root=RegistryKey.OpenBaseKey(hive,view))
        using(var apps=root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")) {
            if(apps==null)continue;
            foreach(string name in apps.GetSubKeyNames()) {
                try {
                    using(var app=apps.OpenSubKey(name)) {
                        string icon=app==null?null:app.GetValue("DisplayIcon") as string;
                        if(string.IsNullOrEmpty(icon))continue;
                        int index=icon.LastIndexOf(',');
                        int resource;
                        if(index>=0 && int.TryParse(icon.Substring(index+1),out resource))icon=icon.Substring(0,index);
                        var source=FromExecutable(icon.Trim().Trim('"'));
                        if(source!=null)found.Add(source);
                    }
                } catch(UnauthorizedAccessException){} catch(System.Security.SecurityException){} catch(IOException){}
            }
        }
        return found;
    }
}
