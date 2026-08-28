#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 4 ]]; then
  echo "usage: build-deb.sh VERSION ARCHITECTURE PUBLISH_DIRECTORY OUTPUT_DIRECTORY" >&2
  exit 2
fi

version="$1"
architecture="$2"
publish_directory="$(realpath "$3")"
output_directory="$(realpath -m "$4")"
script_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(realpath "$script_directory/../..")"
case "$architecture" in
  amd64|arm64) ;;
  *)
    echo "unsupported Debian architecture: $architecture" >&2
    exit 2
    ;;
esac
if [[ ! -x "$publish_directory/BandHub.FirmwareUpdate" ]]; then
  echo "self-contained Linux application host was not found in: $publish_directory" >&2
  exit 2
fi
package_root="$(mktemp -d)"
trap 'rm -rf "$package_root"' EXIT
chmod 0755 "$package_root"

install -d "$package_root/DEBIAN"
install -d "$package_root/usr/lib/bandhub-firmware-update"
install -d "$package_root/usr/bin"
install -d "$package_root/usr/share/applications"
install -d "$package_root/usr/share/pixmaps"
install -d "$package_root/lib/udev/rules.d"
cp -a "$publish_directory/." "$package_root/usr/lib/bandhub-firmware-update/"
install -m 0644 "$script_directory/bandhub-firmware-update.desktop" \
  "$package_root/usr/share/applications/"
install -m 0644 "$repository_root/icon.png" \
  "$package_root/usr/share/pixmaps/bandhub-firmware-update.png"
install -m 0644 "$script_directory/99-bandhub-dongle.rules" \
  "$package_root/lib/udev/rules.d/"
install -m 0755 "$script_directory/bandhub-firmware-update" \
  "$package_root/usr/bin/"
sed -e "s/@VERSION@/$version/g" \
    -e "s/@ARCHITECTURE@/$architecture/g" \
    "$script_directory/control" \
  > "$package_root/DEBIAN/control"

mkdir -p "$output_directory"
package_path="$output_directory/bandhub-firmware-update_${version}_${architecture}.deb"
dpkg-deb --build --root-owner-group "$package_root" \
  "$package_path"
dpkg-deb --info "$package_path" >/dev/null
dpkg-deb --contents "$package_path" >/dev/null
