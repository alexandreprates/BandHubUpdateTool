# BandHub Firmware Update

BandHub Firmware Update is the installed GTK# client for signed Controller and
Dongle updates. It targets .NET 8 and is published as a RID-specific,
self-contained application on Linux and Windows. End users do not need to
install Mono, the .NET runtime, or the .NET SDK.

Version 1 supports only PC HID Dongles that advertise the Dongle self-OTA
capability. Legacy and PS3-profile Dongles are detected but are not flashed.

## Development

```bash
dotnet restore FirmwareUpdate/BandHub.FirmwareUpdate.sln
dotnet build FirmwareUpdate/BandHub.FirmwareUpdate.sln -c Release
dotnet test FirmwareUpdate/test/BandHub.FirmwareUpdate.Tests -c Release
```

The application reads independent signed Controller and Dongle catalogs from
the public domain configured for the Cloudflare R2 bucket
`bandhub-releases`. Firmware objects are immutable and versioned under
`controller/<release-version>/` and `dongle/<release-version>/`. Each component
has its own `release.json` and `release.json.sig` at the component root.

After downloading and verifying the selected packages, the application pauses
before writing firmware and asks the user to confirm that the Dongle remains
connected and that the paired Controller is powered on and connected. It then
refreshes the live device information and rejects a disconnected Controller or
a hardware-target change before continuing.

The application performs the remaining Update All transaction by staging the
Controller package without starting it, updating and validating the Dongle,
reconnecting to the same Dongle MAC, and only then arming the Controller update.

## Packaging

Publish the application before invoking a platform packager:

```bash
dotnet publish FirmwareUpdate/src/BandHub.FirmwareUpdate -c Release \
  -r linux-x64 --self-contained true \
  -o .tmp/updater-linux
FirmwareUpdate/packaging/linux/build-deb.sh \
  0.3.0 amd64 .tmp/updater-linux .tmp/installers
```

The Linux package is architecture-specific. Use `linux-x64` with `amd64`, or
`linux-arm64` with `arm64`. The self-contained application carries the .NET
runtime; the package declares the native host, globalization, TLS,
GTK 3, libudev, and CA-certificate packages supplied by the distribution.

The local and CI checks install the generated DEB and run
`FirmwareUpdate/test/run_linux_runtime_smoke.py` under Xvfb. The smoke test
removes `MONO_PATH` and requires the installed launcher to reach the GTK event
loop without a compatibility runtime.

The Windows package uses the `win-x64` self-contained application, WiX Toolset
5.0.2, and the GTK 3 runtime from MSYS2. The packaging script generates a
relocatable gdk-pixbuf loader cache before building the MSI.

## Local firmware deployment

Install Wrangler and authenticate the workstation once:

```bash
npm install --global wrangler
wrangler login
```

Controller and Dongle releases are intentionally independent:

```bash
python3 tools/deploy_controller_firmware.py \
  --release-version 0.3.0 --firmware-version 0x00030000
python3 tools/deploy_dongle_firmware.py \
  --release-version 0.4.0 --firmware-version 0x00040000
```

Each script builds both hardware variants for its component, embeds the
monotonic firmware version, signs exact-target `.bhfw` packages, creates the
component catalog, and uploads packages before the signed stable catalog. Use
`--no-upload` to build and inspect everything locally. Use `--skip-build` only
when the existing PlatformIO binaries contain the exact requested firmware
version.

Wrangler uses the locally cached Cloudflare login. No R2 Access Key ID or
Secret Access Key is stored in the repository. After uploading, each script
verifies the published files through `https://bandhub.alexandreprates.dev/`.
The same URL is embedded in the updater through `BandHubReleaseBaseUrl`. A
development machine may override both defaults with
`BANDHUB_RELEASES_PUBLIC_BASE_URL`, or only the deployment script with
`--public-base-url`.
