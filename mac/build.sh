#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
out="$(mktemp -d)"
trap 'rm -rf "$out"' EXIT
mkdir -p dist
for cpu in arm64 x86_64; do
  xcrun swiftc -swift-version 5 -O -target "$cpu-apple-macos13.0" mac/*.swift -o "$out/shelf-$cpu"
done
lipo -create "$out/shelf-arm64" "$out/shelf-x86_64" -output "$out/CoverShelf"
lipo -verify_arch arm64 x86_64 "$out/CoverShelf"
osacompile -o "$out/artwork.scpt" mac/artwork.applescript
osacompile -l JavaScript -o "$out/music.scpt" mac/music.js
for lang in en zh; do
  if [[ "$lang" = en ]]; then name="Cover Shelf"; else name="唱片架"; fi
  app="$out/$name.app"
  resources="$app/Contents/Resources"
  mkdir -p "$app/Contents/MacOS" "$resources/ui"
  cp "$out/CoverShelf" "$app/Contents/MacOS/CoverShelf"
  cp mac/music.js "$out/artwork.scpt" "$resources/"
  cp app/shelf.{css,js} app/drive-sound.js app/disc-drive.wav mac/bridge.js "$resources/ui/"
  if [[ "$lang" = en ]]; then cp locales/en/{index.html,strings.js} "$resources/ui/"; else cp app/{index.html,strings.js} "$resources/ui/"; fi
  cp mac/fixture.jpg "$resources/"
  python3 mac/bundle.py "$app" "$lang" "$name"
  codesign --force --sign - "$app"
  codesign --verify --deep --strict "$app"
  "$app/Contents/MacOS/CoverShelf" --smoke-test
  ditto -c -k --keepParent "$app" "dist/Cover-Shelf-macOS-universal-$lang.zip"
done

