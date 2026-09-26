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

Firmware installation supports PC HID Dongles that advertise Dongle self-OTA.
Current PS3-profile Dongles are detected and can be returned to PC HID from the
`Tools` menu, but are not flashed while the PS3 profile is active.
Legacy Dongles without the self-OTA agent remain unsupported.

## USB profiles

Current Dongle firmware contains the default PC HID identity and experimental
PS3 Rock Band Guitar and Xbox 360 Guitar Hero identities. Select the attached Dongle and use
`Tools > USB profiles...`; while PS3 is active, select
`PC HID`. The Dongle persists the selection and restarts with the
requested VID/PID. The application follows the same physical Dongle across
re-enumeration using its stable USB serial.

The PS3 descriptor intentionally omits the vendor update interface. Its only
management command is the fixed recovery report used by this application to
return to PC HID. Switch back to PC HID before installing Controller or Dongle
firmware updates. Recovery discovery checks the exact BandHub manufacturer,
product and serial, then revalidates the identity immediately before opening
the device; a shared Rock Band VID/PID alone is insufficient. PC HID supports
battery and production link diagnostics; the PS3 gameplay descriptor does not
expose those reports.

Signed release catalogs keep the existing `dongle-devkit-pc` and
`dongle-zero-pc` target names for compatibility, even though those packages now
contain the universal runtime-selectable firmware.

## Binding tools

The `Tools` menu also contains two confirmed binding actions for current PC HID
firmware. `Unpair Controller` requires the Controller to be awake and connected;
it remotely clears the Controller first and clears the Dongle after the matching
ESP-NOW acknowledgement. `Unpair Dongle` clears only the local Dongle binding
and is available whenever a compatible PC HID Dongle is detected, including
when no Controller is connected or no saved binding is currently reported. It
is intended as an idempotent recovery action. Its warning explains that a
Controller with a saved binding remains bound and must be cleared separately.

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
then asks the user to press the PS button while it waits for the live Controller
connection. After the Controller reconnects, the updater revalidates its target,
firmware version, identity, and power before arming the Controller update.

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

## Controller diagnostics and calibration

Use **Controller diagnostics...** with a Controller connected directly by USB.
Refresh and choose its serial identity, capture the resting and full whammy ADC
values, then release digital controls and leave analog controls stable for one
second before saving. Inversion follows endpoint order. Debounce settings have
firmware-enforced bounds. **Restore defaults** persists the original values.

The live panel shows raw/calibrated whammy, input masks and USB queue counters.
Import/export uses version 1 JSON for the selected GH3/GH5 assembly; import edits
a draft and Save applies it. A failed save keeps the previous runtime values.
Production Controller firmware must expose management reports 0x30/0x31;
legacy firmware remains usable for gameplay but is not listed for calibration.
Linux packages include a scoped BandHub Controller hidraw access rule.

Firmware advertising wireless-sleep support also exposes an opt-in switch and
connected/disconnected timeouts. Sleep stays off by default, is blocked on USB,
and wakes through START or cable detection (checked once per second). Validate
battery isolation and assembled-hardware wake before enabling it. Firmware
without this capability rejects incompatible sleep-enabled imports.

## Experimental Xbox 360 profiles

Open **USB profiles...** to choose PC HID, experimental PS3 (dongle only), or
experimental Xbox 360 Guitar Hero. The Controller must be connected directly by
USB to change its own profile. Each device persists its selection independently.
Older firmware without the new capability does not offer Xbox selection.

Xbox mode permits identification and return to PC, not firmware installation,
calibration or pairing changes. After changing modes, the app confirms the same
USB serial before reporting success. Return through PC to select another console
profile. If a switch times out, boot the device normally, hold BOOT for five
seconds, then release to recover PC without erasing settings.

Xbox 360/Guitar Hero 5 physical acceptance remains pending. Automated tests and
successful enumeration on a computer must not be described as console validation.
