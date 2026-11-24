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
lipo "$out/CoverShelf" -verify_arch arm64 x86_64
osacompile -o "$out/artwork.scpt" mac/artwork.applescript
osacompile -l JavaScript -o "$out/music.scpt" mac/music.js
for lang in en zh; do
  if [[ "$lang" = en ]]; then name="Self Coverflow"; else name="Self Coverflow 中文版"; fi
  stage="$out/$lang"
  app="$stage/payload/$name.app"
  resources="$app/Contents/Resources"
  mkdir -p "$app/Contents/MacOS" "$resources/ui"
  cp "$out/CoverShelf" "$app/Contents/MacOS/CoverShelf"
  cp mac/music.js "$out/artwork.scpt" "$resources/"
  cp app/shelf.{css,js} app/drive-sound.js app/disc-drive.wav mac/bridge.js "$resources/ui/"
  if [[ "$lang" = en ]]; then cp locales/en/{index.html,strings.js} "$resources/ui/"; else cp app/{index.html,strings.js} "$resources/ui/"; fi
  cp mac/fixture.jpg mac/Icon.icns "$resources/"
  python3 mac/bundle.py "$app" "$lang" "$name"
  codesign --force --sign - "$app"
  codesign --verify --deep --strict "$app"
  "$app/Contents/MacOS/CoverShelf" --smoke-test
  python3 mac/package.py "$stage" "$lang" "$name"
  pkg="dist/Self-Coverflow-macOS-universal-$lang.pkg"
  productbuild --distribution "$stage/distribution.xml" --package-path "$stage" --resources "$stage/Guide" "$pkg"
  pkgutil --expand "$pkg" "$stage/expanded"
  if [[ "${CI:-}" = true ]]; then
    sudo installer -pkg "$pkg" -target /
    codesign --verify --deep --strict "/Applications/$name.app"
    "/Applications/$name.app/Contents/MacOS/CoverShelf" --smoke-test
  fi
done

