using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BandHub.FirmwareUpdate.Core;
using Gtk;

namespace BandHub.FirmwareUpdate;

internal sealed class UsbProfilesWindow : Window
{
    private sealed record Entry(string Label, UsbProfileDevice Device, DongleDescriptor? Dongle);
    private readonly ComboBoxText devices = new(), profiles = new();
    private readonly Button refresh = new("Refresh"), apply = new("Apply profile");
    private readonly Label status = new("Connect the device directly to this computer. Each device stores its own profile.") { Xalign = 0, LineWrap = true };
    private readonly CancellationTokenSource lifetime = new();
    private readonly HidDongleDiscovery dongles = new();
    private List<Entry> entries = new();
    private bool busy, closed;
    public UsbProfilesWindow(Window parent) : base("USB profiles")
    {
        TransientFor = parent; Modal = true; DefaultWidth = 560; BorderWidth = 16;
        var root = new Box(Orientation.Vertical, 12);
        root.PackStart(status, false, false, 0);
        root.PackStart(devices, false, false, 0);
        root.PackStart(profiles, false, false, 0);
        root.PackStart(new Label("Xbox 360 Guitar Hero is experimental until tested on your console.\nTo recover PC HID without the app: after startup, hold BOOT for five seconds, then release.")
            { Xalign = 0, LineWrap = true }, false, false, 0);
        var actions = new Box(Orientation.Horizontal, 8);
        actions.PackStart(refresh, false, false, 0); actions.PackEnd(apply, false, false, 0);
        root.PackStart(actions, false, false, 0); Add(root);
        devices.Changed += (_, _) => FillProfiles();
        refresh.Clicked += async (_, _) => await Run(RefreshAsync);
        apply.Clicked += async (_, _) =>
        {
            if (devices.Active < 0 || profiles.ActiveId == null) return;
            var selected = entries[devices.Active]; var target = (UsbProfile)byte.Parse(profiles.ActiveId);
            await Run(async () =>
            {
                if (selected.Dongle != null)
                    await dongles.SwitchUsbProfileAsync(selected.Dongle, target, TimeSpan.FromSeconds(30), lifetime.Token);
                else if (selected.Device.Profile != target)
                {
                    await UsbProfileManagement.SendProfileAsync(selected.Device, target, lifetime.Token);
                    await UsbProfileManagement.WaitAsync(selected.Device.Serial, 1, target, TimeSpan.FromSeconds(30), lifetime.Token);
                }
                await RefreshAsync();
                Ui(() => status.Text = $"Profile changed to {ProfileName(target)}. You can now connect the device to the target system.");
            });
        };
        Destroyed += (_, _) => { closed = true; lifetime.Cancel(); };
        _ = Run(RefreshAsync);
    }
    private void Ui(System.Action action) => Application.Invoke((_, _) => { if (!closed) action(); });
    private async Task Run(Func<Task> action)
    {
        if (busy || closed) return;
        busy = true; refresh.Sensitive = apply.Sensitive = devices.Sensitive = profiles.Sensitive = false;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Ui(() => status.Text = error.Message); }
        finally { Ui(() => { busy = false; refresh.Sensitive = devices.Sensitive = true; FillProfiles(); }); }
    }
    private async Task RefreshAsync()
    {
        var found = new List<Entry>();
        foreach (var dongle in await dongles.DiscoverAsync(lifetime.Token))
        {
            if (!dongle.CanSwitchUsbProfile) continue;
            byte mask = dongle.UsbProfile == UsbProfile.Xbox360GuitarHero || dongle.DeviceInfo?.SupportsXbox360Profile == true ? (byte)14 : (byte)6;
            var device = new UsbProfileDevice(dongle.Path, dongle.SerialNumber, 2, dongle.UsbProfile, mask);
            found.Add(new($"Dongle {device.Serial} — {ProfileName(device.Profile)}", device, dongle));
        }
        foreach (var device in (await UsbProfileManagement.DiscoverAsync(lifetime.Token)).Where(x => x.Role == 1))
            found.Add(new($"Controller {device.Serial} — {ProfileName(device.Profile)}", device, null));
        Ui(() =>
        {
            entries = found; devices.RemoveAll(); foreach (var entry in found) devices.AppendText(entry.Label);
            devices.Active = found.Count == 0 ? -1 : 0;
            status.Text = found.Count == 0 ? "No compatible profile interface found. Update older firmware in PC mode and check USB permissions." : "Select a device and its USB profile. Calibration and pairing are preserved.";
            FillProfiles();
        });
    }
    private void FillProfiles()
    {
        profiles.RemoveAll();
        if (devices.Active < 0 || devices.Active >= entries.Count) { apply.Sensitive = profiles.Sensitive = false; return; }
        var current = entries[devices.Active].Device;
        foreach (var target in new[] { UsbProfile.PcHid, UsbProfile.Ps3RockBandGuitar, UsbProfile.Xbox360GuitarHero })
            if (current.Supports(target) && (current.Profile == UsbProfile.PcHid || target == UsbProfile.PcHid || target == current.Profile))
                profiles.Append(((byte)target).ToString(), ProfileName(target));
        profiles.ActiveId = ((byte)current.Profile).ToString();
        apply.Sensitive = profiles.Sensitive = !busy;
    }
    private static string ProfileName(UsbProfile profile) => profile switch
    {
        UsbProfile.PcHid => "PC HID",
        UsbProfile.Ps3RockBandGuitar => "PS3 Rock Band Guitar",
        UsbProfile.Xbox360GuitarHero => "Xbox 360 Guitar Hero",
        _ => "Unknown",
    };
}
