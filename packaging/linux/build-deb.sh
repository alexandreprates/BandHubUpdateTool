#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "usage: build-deb.sh VERSION PUBLISH_DIRECTORY OUTPUT_DIRECTORY" >&2
  exit 2
fi

version="$1"
publish_directory="$(realpath "$2")"
output_directory="$(realpath -m "$3")"
package_root="$(mktemp -d)"
trap 'rm -rf "$package_root"' EXIT
chmod 0755 "$package_root"

install -d "$package_root/DEBIAN"
install -d "$package_root/usr/lib/bandhub-firmware-update"
install -d "$package_root/usr/bin"
install -d "$package_root/usr/share/applications"
install -d "$package_root/lib/udev/rules.d"
cp -a "$publish_directory/." "$package_root/usr/lib/bandhub-firmware-update/"
install -m 0644 FirmwareUpdate/packaging/linux/bandhub-firmware-update.desktop \
  "$package_root/usr/share/applications/"
install -m 0644 FirmwareUpdate/packaging/linux/99-bandhub-dongle.rules \
  "$package_root/lib/udev/rules.d/"
install -m 0755 FirmwareUpdate/packaging/linux/bandhub-firmware-update \
  "$package_root/usr/bin/"
sed "s/@VERSION@/$version/g" FirmwareUpdate/packaging/linux/control \
  > "$package_root/DEBIAN/control"

mkdir -p "$output_directory"
dpkg-deb --build --root-owner-group "$package_root" \
  "$output_directory/bandhub-firmware-update_${version}_all.deb"
