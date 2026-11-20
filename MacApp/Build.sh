#!/bin/bash
set -euo pipefail
rid="${1:-}"
case "$rid" in osx-arm64|osx-x64) ;; *) echo 'Usage: bash MacApp/Build.sh osx-arm64|osx-x64' >&2; exit 2;; esac
project="$(cd "$(dirname "$0")/.." && pwd)"
output="$project/dist/mac/$rid"
app="$output/CalendarTodo.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
dotnet publish "$project/MacApp/CalendarTodo.Mac.csproj" -c Release -r "$rid" \
  --self-contained true -p:DebugType=None -p:DebugSymbols=false -o "$app/Contents/MacOS"
cp "$project/MacApp/Info.plist" "$app/Contents/Info.plist"
cp "$project/MacApp/README.md" "$output/README-macOS.md"
cp "$project/THIRD-PARTY-NOTICES.md" "$app/Contents/Resources/THIRD-PARTY-NOTICES.md"
cp -R "$project/ThirdParty" "$app/Contents/Resources/"
python3 - "$project/App/Assets/CalendarTodo.ico" "$app/Contents/Resources/CalendarTodo.png" <<'PY'
import pathlib, struct, sys
data = pathlib.Path(sys.argv[1]).read_bytes()
images = []
for index in range(struct.unpack_from('<H', data, 4)[0]):
    size, offset = struct.unpack_from('<II', data, 6 + index * 16 + 8)
    image = data[offset:offset + size]
    if image.startswith(b'\x89PNG\r\n\x1a\n'):
        images.append(image)
if not images:
    raise RuntimeError('App icon contains no PNG representation')
pathlib.Path(sys.argv[2]).write_bytes(max(images, key=len))
PY
iconset="$output/CalendarTodo.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$app/Contents/Resources/CalendarTodo.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z "$double" "$double" "$app/Contents/Resources/CalendarTodo.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/CalendarTodo.icns"
chmod +x "$app/Contents/MacOS/CalendarTodo.Mac"
codesign --force --deep --sign - --entitlements "$project/MacApp/entitlements.plist" "$app"
codesign --verify --deep --strict "$app"
ditto -c -k --sequesterRsrc --keepParent "$app" "$output/CalendarTodo-macOS-$rid-1.5.0-preview.1.zip"
cd "$output"
shasum -a 256 "CalendarTodo-macOS-$rid-1.5.0-preview.1.zip" > SHA256SUMS.txt
