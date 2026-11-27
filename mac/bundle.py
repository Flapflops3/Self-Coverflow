import base64
import plistlib
from pathlib import Path
import sys

app = Path(sys.argv[1])
lang, name = sys.argv[2:]
res = app / "Contents/Resources"
ui = res / "ui"
info = {
    "CFBundleExecutable": "CoverShelf", "CFBundleName": name, "CFBundleDisplayName": name,
    "CFBundleIdentifier": "io.github.flapflops3.selfcoverflow." + lang,
    "CFBundlePackageType": "APPL", "CFBundleVersion": "5", "CFBundleShortVersionString": "1.0.0",
    "LSMinimumSystemVersion": "13.0", "LSUIElement": True, "ShelfLanguage": lang, "CFBundleIconFile": "Icon.icns",
    "CFBundleDevelopmentRegion": "en" if lang == "en" else "zh_CN",
    "NSHighResolutionCapable": True,
    "NSAppleEventsUsageDescription": "Self Coverflow reads your albums and controls playback in Music." if lang == "en" else "唱片架需要读取本机音乐资料库并控制播放。",
}
(app / "Contents/Info.plist").write_bytes(plistlib.dumps(info))
html = (ui / "index.html").read_text()
html = html.replace("https://art.local", "shelf://art").replace("https://shelf.local/disc-drive.wav", "'none'")
html = html.replace('<script defer src="strings.js">', '<script defer src="bridge.js"></script><script defer src="strings.js">')
(ui / "index.html").write_text(html)
js = ui / "shelf.js"
js.write_text(js.read_text().replace("https://art.local/", "shelf://art/"))
# WKWebView does not consistently allow fetch() on custom schemes across supported OS versions.
audio = ui / "drive-sound.js"
src = audio.read_text()
start = src.index("fetch('disc-drive.wav')")
end = src.index(".then(bytes=>context.decodeAudioData(bytes))",start)
data = base64.b64encode((ui / "disc-drive.wav").read_bytes()).decode("ascii")
audio.write_text(src[:start] + "Promise.resolve().then(()=>Uint8Array.from(atob('" + data +
                 "'),c=>c.charCodeAt(0)).buffer)" + src[end:])
(ui / "disc-drive.wav").unlink()

