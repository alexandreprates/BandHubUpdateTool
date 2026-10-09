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

## Controller update connections

Select **Wireless through Dongle** or **Direct USB Controller** before refreshing
and choosing a device. Both paths install the same signed exact-model package.
SuperMini GH3 (`controller-gh3-supermini`, 0x0104) and GH5
(`controller-gh5-supermini`, 0x0103) have targets distinct from legacy ESP32
controllers; packages are never interchangeable.

Wireless updates retain **Update all**: USB-connected PC HID Dongle, paired
Controller, ESP-NOW negotiation and temporary Wi-Fi/TCP transfer. The Dongle must
run firmware that recognizes the SuperMini feature bit. If an older Dongle
cannot identify it, use **Tools > Update Dongle only...**, then refresh. This
explicit signed self-update loads only the Dongle catalog and does not flash
the Controller. Legacy Controller wireless updates remain supported.

Direct USB uses **Update Controller**, requires no Dongle or pairing, and loads
only the Controller catalog. **Use local firmware...** accepts a signed `.bhfw`
or `.bhrelease` bundle containing that Controller's signed manifest and package.
Connect a USB data cable and return the Controller to PC HID using **USB
profiles...** first. Firmware without the direct-update capability is listed
with a first-installation explanation. Install the first compatible firmware
with an external USB flashing tool; ROM recovery is not part of this flow.

Controller firmware that reports the USB product name `TinyUSB HID` is also
recognized when its VID/PID, BandHub manufacturer and serial match. The updater
reads the GH3/GH5 model from the validated update report before selecting firmware.

USB commands use reports 0x40/0x41/0x42 from
`BandHubControllerUsbUpdate.h` in Shared. The updater checks the selected serial,
model, version, capability and image health again before transfer. Session and
offset acknowledgements permit identical command retries. Transient HID read,
write, and malformed-response failures are retried within the command timeout;
only a validated acknowledgement advances the transfer. Finish verifies the
inactive image; Commit alone selects it for boot. Cancellation is available
before Commit. After Commit, the app waits for the same serial and expected
healthy firmware, even when the acknowledgement is lost or close is requested.
Commit is sent only once, including when the device keeps returning an older,
valid status while restarting. An uncertain installation is resolved by checking
the same Controller's expected firmware version and image health.
A disconnect before Commit leaves the old firmware selected; retry starts from
zero. Pairing, calibration and saved profile are preserved.

These host-side retries do not repair USB buffer corruption in Controller
firmware. Repeated USB resets can require corrected Controller firmware installed
through a Dongle or external USB recovery. The updater continues to accept only
signed packages; a locally flashed diagnostic image is not a published update.
Default transfer pacing is retained: slowing transfers was not required with
the corrected Controller firmware in the Linux GH5 SuperMini bench tests.

Source implementation and host tests do not establish physical acceptance.
GH3/GH5 updates on both connections, Windows/Linux HID behavior, transfer time,
power-cut recovery and rollback must be recorded on real devices before a
production rollout. No partition-table migration or ROM flashing is performed.

## USB profiles

The application displays the PC HID profile as **PC Dongle** in profile lists
and recovery instructions. Its protocol identifier remains `PcHid` (value `1`).

Current Dongle firmware contains the default PC HID identity and experimental
PS3 Rock Band Guitar and Xbox 360 Guitar Hero identities. Select the attached Dongle and use
`Tools > USB profiles...`; while PS3 is active, select
`PC Dongle`. The Dongle persists the selection and restarts with the
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

Use **Controller calibration and settings...** with a Controller connected directly
by USB or wirelessly through its paired PC HID Dongle. The window follows the
connection selected on the main screen, discovers devices when opened, and also
lets you change the connection. Select the Controller and click **Connect**.
Wireless access requires updated firmware on both devices: the Dongle advertises
management capability 0x80 and the Controller advertises feature bit 14. Older
firmware stays listed with instructions identifying which device needs an update.
USB console-profile changes and ROM flash mode still require direct USB.

Wireless read, save, reset, diagnostics and sleep settings use the same validated
management reports as USB. The relay checks the binding and selected Controller
identity, retries lost radio requests without repeating the current save, and
rejects writes during firmware updates or until controls have been idle for one
second. Firmware sources in BandHubController, BandHubDongle and their shared
BandHubProtocol module must be updated together before using this path. Physical
wireless calibration acceptance must be recorded on updated devices.

Refresh and choose its serial identity, capture the resting and full whammy ADC
values, then release digital controls and leave analog controls stable for one
second before saving. Inversion follows endpoint order. Debounce settings have
firmware-enforced bounds. **Restore defaults** persists the original values.

The live panel shows raw/calibrated whammy, input masks and USB or radio queue counters.
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

## USB flash mode without board buttons

Open **Tools > USB profiles**, select the directly connected Dongle or
Controller, and choose **Enter USB flash mode**. Compatible PC HID and Xbox 360
firmware advertise this capability; older firmware and PS3 mode leave the
action disabled. Release Controller controls for one second before using it.
The app rechecks identity/capability, waits for acknowledgement, and confirms
that the application HID interface disconnected. Continue with USB flashing;
the app does not infer a ROM serial port or install an image in this action.
Saved profile, pairing and calibration are retained. Active updates prevent
entry. For ordinary signed Dongle updates, return to PC HID and use the normal
update flow instead.

Older firmware must receive one update before software bootloader entry is
available. The existing Dongle self-update flow can perform that bootstrap
without board buttons. Direct Controller USB updates and SuperMini wireless
updates use the signed update flows above; entering ROM mode remains a separate
recovery action. Physical bootloader entry for this
new firmware remains to be validated on a flashed device.
