#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
out="$(mktemp -d)"
trap 'rm -rf "$out"' EXIT
mkdir -p dist
adapter_rev=2ef957c736256cee7905c9f3f1c7e279087bf195
curl --fail --location --retry 3 "https://codeload.github.com/ungive/mediaremote-adapter/tar.gz/$adapter_rev" -o "$out/adapter.tgz"
echo "238ae0bd24272dee92b10a2befbe511c8688e9ee6fbb456df43aed4a897e4c9b  $out/adapter.tgz" | shasum -a 256 -c -
tar -xzf "$out/adapter.tgz" -C "$out"
adapter="$out/mediaremote-adapter-$adapter_rev"
cmake -S "$adapter" -B "$out/adapter-build" -DCMAKE_BUILD_TYPE=Release -DCMAKE_OSX_DEPLOYMENT_TARGET=13.0
cmake --build "$out/adapter-build" --target MediaRemoteAdapter --parallel
framework="$out/adapter-build/MediaRemoteAdapter.framework"
lipo "$framework/MediaRemoteAdapter" -verify_arch arm64 x86_64
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
  mkdir -p "$app/Contents/Frameworks"
  cp -R "$framework" "$app/Contents/Frameworks/"
  cp "$adapter/bin/mediaremote-adapter.pl" "$resources/"
  cp "$adapter/LICENSE" "$resources/MediaRemoteAdapter-LICENSE"
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

