# BandHub Firmware Update

BandHub Firmware Update is the installed GTK# client for signed Controller and
Dongle updates. It targets .NET Framework 4.7.2 on Windows and Mono on Linux.

Version 1 supports only PC HID Dongles that advertise the Dongle self-OTA
capability. Legacy and PS3-profile Dongles are detected but are not flashed.

## Development

```bash
dotnet restore FirmwareUpdate/BandHub.FirmwareUpdate.sln
dotnet build FirmwareUpdate/BandHub.FirmwareUpdate.sln -c Release
dotnet test FirmwareUpdate/test/BandHub.FirmwareUpdate.Tests -c Release
```

The application reads stable releases from
`alexandreprates/BandHub-Releases`. A local `.bhrelease` ZIP bundle must contain
the same signed `release.json`, `release.json.sig`, and `.bhfw` assets used by an
online release.

The application performs an Update All transaction: it stages the Controller
package without starting it, updates and validates the Dongle, reconnects to
the same Dongle MAC, and only then arms the Controller update.

## Packaging

Publish the application before invoking a platform packager:

```bash
dotnet publish FirmwareUpdate/src/BandHub.FirmwareUpdate -c Release \
  -o .tmp/updater-linux
FirmwareUpdate/packaging/linux/build-deb.sh \
  0.3.0 .tmp/updater-linux .tmp/installers
```

The Windows release job uses WiX Toolset 5.0.2 and bundles the GTK 3 runtime
from MSYS2. The workflow generates a relocatable gdk-pixbuf loader cache before
building the MSI.

The private release workflow requires `BANDHUB_RELEASES_TOKEN` with permission
to create releases in `alexandreprates/BandHub-Releases`.
