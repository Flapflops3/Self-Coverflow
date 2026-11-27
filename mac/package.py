"""Create localised Apple Installer pages and non-relocatable component metadata."""
import html
from pathlib import Path
import plistlib
import subprocess
import sys
import xml.etree.ElementTree as ET

folder, lang, name = Path(sys.argv[1]), sys.argv[2], sys.argv[3]
en = lang == "en"
title = "Self Coverflow"
guide = folder / "Guide"
guide.mkdir()
def page(file, heading, steps):
    body = "".join("<h3>"+html.escape(label)+"</h3><p>"+html.escape(copy)+"</p>" for label,copy in steps)
    (guide / file).write_text('<!doctype html><meta charset="utf-8"><style>body{font:14px -apple-system,BlinkMacSystemFont,sans-serif;color:#26262a;margin:24px;line-height:1.5}h1{font-size:27px;font-weight:600;letter-spacing:-.6px;margin:0 0 18px}h3{font-size:15px;margin:16px 0 3px}p{margin:0;color:#535359}</style><h1>'+html.escape(heading)+'</h1>'+body)

page("welcome.html", "Self Coverflow", [
    ("Your albums, on your desktop.", "This installer places the English edition in Applications. It runs on Apple silicon and Intel Macs with macOS 13 or later.") if en else
    ("把唱片放在桌面上", "向导将把简体中文版安装到「应用程序」。支持 macOS 13 或更新版本的 Apple 芯片和 Intel Mac。"),
    ("Before you begin", "Open Music, sign in and confirm that Library → Albums contains your albums.") if en else
    ("安装前", "打开「音乐」并登录，在「资料库 → 专辑」确认可以看到自己的唱片。"),
    ("Continue to install", "Your Music library stays on this Mac. The first launch asks permission to control Music.") if en else
    ("继续安装", "唱片架在本机读取资料库。首次启动时，系统会询问是否允许控制「音乐」。")
])
page("finish.html", "Ready to play" if en else "准备好播放", [
    ("1. Open Self Coverflow", "Open Applications and launch Self Coverflow.") if en else ("1. 打开应用", "前往「应用程序」，打开「Self Coverflow 中文版」。"),
    ("2. Allow Music control", "Choose Allow when macOS asks. If you previously declined, enable Music for Self Coverflow in System Settings → Privacy & Security → Automation. Use Refresh library in the shelf menu.") if en else
    ("2. 允许控制音乐", "出现系统询问时选「允许」。如果此前拒绝，可在「系统设置 → 隐私与安全性 → 自动化」里允许 Self Coverflow 控制「音乐」，然后在唱片架菜单中刷新资料库。"),
    ("3. Check a record", "Choose All Albums, double-click a cover and check that the title and playback status update.") if en else
    ("3. 试播一张专辑", "切到「全部专辑」，双击一张封面，确认曲名和播放状态跟着变化。")
])

identifier = "io.github.flapflops3.selfcoverflow." + lang
dist = ET.Element("installer-gui-script", {"minSpecVersion":"2"})
ET.SubElement(dist,"title").text = title
ET.SubElement(dist,"welcome",{"file":"welcome.html","mime-type":"text/html"})
ET.SubElement(dist,"conclusion",{"file":"finish.html","mime-type":"text/html"})
ET.SubElement(dist,"options",{"customize":"never","hostArchitectures":"arm64,x86_64"})
ET.SubElement(dist,"domains",{"enable_localSystem":"true","enable_anywhere":"false","enable_currentUserHome":"false"})
ET.SubElement(ET.SubElement(dist,"allowed-os-versions"),"os-version",{"min":"13.0"})
ET.SubElement(ET.SubElement(dist,"choices-outline"),"line",{"choice":"app"})
choice=ET.SubElement(dist,"choice",{"id":"app","visible":"false"})
ET.SubElement(choice,"pkg-ref",{"id":identifier})
ET.SubElement(dist,"pkg-ref",{"id":identifier,"version":"1.0.0.5"}).text="app.pkg"
ET.ElementTree(dist).write(folder/"distribution.xml",encoding="utf-8",xml_declaration=True)
payload=folder/"payload"
metadata=folder/"components.plist"
subprocess.run(["pkgbuild","--analyze","--root",str(payload),str(metadata)],check=True)
components=plistlib.loads(metadata.read_bytes())
for component in components: component["BundleIsRelocatable"]=False
metadata.write_bytes(plistlib.dumps(components))
subprocess.run(["pkgbuild","--root",str(payload),"--component-plist",str(metadata),"--identifier",identifier,"--version","1.0.0.5","--install-location","/Applications",str(folder/"app.pkg")],check=True)

