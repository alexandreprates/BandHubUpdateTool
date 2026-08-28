# BandHub Update Tool

BandHub Update Tool is the public GTK# desktop client for installing signed
BandHub Controller and Dongle firmware. It targets .NET 8 and is distributed as
self-contained Linux and Windows installers, so end users do not need Mono or
the .NET runtime.

## Downloads

Download the current Windows x64 MSI or Linux amd64 DEB from the
[GitHub Releases](https://github.com/alexandreprates/BandHubUpdateTool/releases)
page. Installers are built automatically from the tagged source revision and
published with SHA-256 checksums and GitHub artifact attestations.

## Security model

The updater downloads independent signed Controller and Dongle catalogs from
`https://bandhub.alexandreprates.dev/`. Firmware packages and catalogs must
validate against the production public key embedded from
`security/release-public-key.hex` before the updater writes either device.

Only the public verification key is stored in this repository. Production
firmware signing keys and Cloudflare deployment credentials are intentionally
kept outside this public project. Tests use an isolated fixture key that is not
trusted by production builds.

Version 1 supports PC HID Dongles that advertise Dongle self-OTA. Legacy and
PS3-profile Dongles are detected but are not flashed.

## Development

```bash
dotnet restore BandHub.FirmwareUpdate.sln
dotnet build BandHub.FirmwareUpdate.sln -c Release
dotnet test test/BandHub.FirmwareUpdate.Tests -c Release
python3 test/test_packaging_icons.py
```

After downloading and cryptographically verifying the selected packages, the
application pauses before writing firmware. It asks the user to confirm that
the Dongle remains connected and that the paired Controller is powered on and
connected, then refreshes the live hardware identity before continuing.

The Update All transaction stages the Controller package without starting it,
updates and validates the Dongle, reconnects to the same Dongle MAC, and only
then arms the Controller update.

## Linux packaging

```bash
dotnet publish src/BandHub.FirmwareUpdate -c Release \
  -r linux-x64 --self-contained true \
  -o .tmp/updater-linux
packaging/linux/build-deb.sh \
  0.3.0 amd64 .tmp/updater-linux .tmp/installers
```

The DEB installs the application under `/usr/lib/bandhub-firmware-update`, a
launcher under `/usr/bin`, udev permissions for the Dongle, a desktop entry,
and `icon.png` under `/usr/share/pixmaps`. The package depends on the native GTK
3, libudev, globalization, TLS, and CA-certificate packages supplied by the
distribution.

## Windows packaging

The Windows package uses the `win-x64` self-contained application, WiX Toolset
5.0.2, and the GTK 3 runtime from MSYS2. The packaging script generates a
relocatable gdk-pixbuf loader cache before building the MSI. `icon.ico` is
embedded in the Windows executable and used by the Start Menu shortcut and the
Installed Apps entry.

`icon.png` is the canonical source asset. Regenerate the checked-in Windows
icon after replacing it:

```bash
magick icon.png -background none \
  -define icon:auto-resize=256,128,64,48,32,16 icon.ico
```

## Automated releases

Publishing a GitHub Release whose tag starts with `v` triggers Linux and
Windows builds. The workflow tests the source, builds the DEB and MSI, generates
`SHA256SUMS`, creates artifact attestations, and uploads every file to the same
release. Installers are currently unsigned and may trigger Windows SmartScreen
or distribution trust warnings until platform signing is configured.
