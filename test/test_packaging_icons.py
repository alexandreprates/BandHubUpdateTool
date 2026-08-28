#!/usr/bin/env python3

import struct
from pathlib import Path
import unittest
import xml.etree.ElementTree as element_tree


FIRMWARE_UPDATE_ROOT = Path(__file__).resolve().parents[1]


class PackagingIconTests(unittest.TestCase):
    def test_source_png_is_square_rgba_and_large_enough(self) -> None:
        data = (FIRMWARE_UPDATE_ROOT / "icon.png").read_bytes()
        self.assertEqual(data[:8], b"\x89PNG\r\n\x1a\n")
        self.assertEqual(data[12:16], b"IHDR")
        width, height, bit_depth, color_type = struct.unpack(">IIBB", data[16:26])
        self.assertEqual(width, height)
        self.assertGreaterEqual(width, 256)
        self.assertEqual(bit_depth, 8)
        self.assertEqual(color_type, 6)

    def test_windows_icon_contains_standard_sizes(self) -> None:
        data = (FIRMWARE_UPDATE_ROOT / "icon.ico").read_bytes()
        reserved, icon_type, count = struct.unpack_from("<HHH", data)
        self.assertEqual((reserved, icon_type), (0, 1))
        sizes = set()
        for index in range(count):
            width, height = struct.unpack_from("BB", data, 6 + index * 16)
            sizes.add((width or 256, height or 256))
        self.assertTrue({16, 32, 48, 64, 128, 256}.issubset({width for width, _ in sizes}))
        self.assertTrue(all(width == height for width, height in sizes))

    def test_application_embeds_png_and_assigns_windows_icon(self) -> None:
        project = element_tree.parse(
            FIRMWARE_UPDATE_ROOT
            / "src/BandHub.FirmwareUpdate/BandHub.FirmwareUpdate.csproj"
        )
        application_icon = project.find(".//ApplicationIcon")
        self.assertIsNotNone(application_icon)
        self.assertEqual(application_icon.text, "../../icon.ico")
        embedded_icon = project.find(".//EmbeddedResource[@Include='../../icon.png']")
        self.assertIsNotNone(embedded_icon)
        self.assertEqual(
            embedded_icon.findtext("LogicalName"),
            "BandHub.FirmwareUpdate.icon.png",
        )
        program = (
            FIRMWARE_UPDATE_ROOT / "src/BandHub.FirmwareUpdate/Program.cs"
        ).read_text(encoding="utf-8")
        self.assertIn("new Gdk.Pixbuf", program)
        self.assertIn("new MainWindow { Icon = icon }", program)

    def test_linux_package_installs_desktop_icon(self) -> None:
        desktop = (
            FIRMWARE_UPDATE_ROOT
            / "packaging/linux/bandhub-firmware-update.desktop"
        ).read_text(encoding="utf-8")
        build_script = (
            FIRMWARE_UPDATE_ROOT / "packaging/linux/build-deb.sh"
        ).read_text(encoding="utf-8")
        self.assertIn("Icon=bandhub-firmware-update", desktop)
        self.assertIn('$repository_root/icon.png', build_script)
        self.assertIn(
            "/usr/share/pixmaps/bandhub-firmware-update.png", build_script
        )

    def test_windows_installer_uses_icon_for_arp_and_shortcut(self) -> None:
        namespace = {"wix": "http://wixtoolset.org/schemas/v4/wxs"}
        product = element_tree.parse(
            FIRMWARE_UPDATE_ROOT / "packaging/windows/Product.wxs"
        )
        icon = product.find(".//wix:Icon", namespace)
        self.assertIsNotNone(icon)
        self.assertEqual(icon.get("Id"), "BandHubFirmwareUpdate.ico")
        self.assertEqual(icon.get("SourceFile"), "$(var.IconPath)")
        arp_icon = product.find(
            ".//wix:Property[@Id='ARPPRODUCTICON']", namespace
        )
        self.assertIsNotNone(arp_icon)
        self.assertEqual(arp_icon.get("Value"), "BandHubFirmwareUpdate.ico")
        shortcut = product.find(".//wix:Shortcut", namespace)
        self.assertIsNotNone(shortcut)
        self.assertEqual(shortcut.get("Icon"), "BandHubFirmwareUpdate.ico")
        build_script = (
            FIRMWARE_UPDATE_ROOT / "packaging/windows/build-msi.ps1"
        ).read_text(encoding="utf-8")
        self.assertIn('$iconPath = Join-Path $repositoryRoot "icon.ico"', build_script)
        self.assertIn("-d IconPath=$iconPath", build_script)


if __name__ == "__main__":
    unittest.main()
