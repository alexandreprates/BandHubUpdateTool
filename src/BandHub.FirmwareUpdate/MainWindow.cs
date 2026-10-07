using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BandHub.FirmwareUpdate.Core;
using Gtk;

namespace BandHub.FirmwareUpdate;

internal sealed class MainWindow : Window
{
    private readonly HidDongleDiscovery discovery = new();
    private readonly FirmwareUpdateService updateService;
    private readonly ComboBoxText dongles = new();
    private readonly ComboBoxText transportChoice = new();
    private readonly ControllerUsbDiscovery usbDiscovery = new();
    private readonly Button usbProfilesButton = new("USB profiles...");
    private IReadOnlyList<ControllerUpdateDescriptor> usbDescriptors = Array.Empty<ControllerUpdateDescriptor>();
    private bool DirectUsb => transportChoice.Active == 1;
    private bool busy;
    private bool closeRequested;
    private readonly Label deviceSummary = new("Connect a BandHub Dongle and refresh.");
    private readonly Label controllerSummary = new(string.Empty);
    private readonly ProgressBar progress = new();
    private readonly TextView log = new() { Editable = false, WrapMode = WrapMode.WordChar };
    private readonly Button calibrationButton = new("Controller diagnostics...");
    private readonly Button refreshButton = new("Refresh");
    private readonly Button updateButton = new("Update all") { Sensitive = false };
    private readonly Button localButton = new("Use local bundle...") { Sensitive = false };
    private readonly MenuButton toolsButton = new()
    {
        Label = "Tools",
        Direction = ArrowType.Down,
        UsePopover = false,
        Sensitive = false,
    };
    private readonly Gtk.Menu toolsMenu = new();
    private readonly MenuItem unpairControllerItem = new("Unpair Controller...");
    private readonly MenuItem unpairDongleItem = new("Unpair Dongle...");
    private readonly MenuItem updateDongleItem = new("Update Dongle only...");
    private bool updatingDongleOnly;
    private readonly MenuItem profileItem = new("USB profiles...");
    private readonly Button cancelButton = new("Cancel") { Sensitive = false };
    private readonly DongleBindingService bindingService;
    private IReadOnlyList<DongleDescriptor> descriptors = Array.Empty<DongleDescriptor>();
    private CancellationTokenSource? operation;
    private MessageDialog? controllerWakeDialog;

    public MainWindow() : base("BandHub Firmware Update")
    {
        updateService = new FirmwareUpdateService(discovery);
        bindingService = new DongleBindingService(discovery);
        DefaultWidth = 680;
        DefaultHeight = 520;
        BorderWidth = 18;
        WindowPosition = WindowPosition.Center;

        var root = new Box(Orientation.Vertical, 12);
        var title = new Label { Markup = "<span size='xx-large' weight='bold'>BandHub Firmware Update</span>", Xalign = 0 };
        var description = new Label("Choose direct USB or wireless through a Dongle to install signed firmware.")
        {
            Xalign = 0,
            LineWrap = true,
        };
        root.PackStart(title, false, false, 0);
        root.PackStart(description, false, false, 0);
        root.PackStart(calibrationButton, false, false, 0);
        transportChoice.AppendText("Wireless through Dongle");
        transportChoice.AppendText("Direct USB Controller");
        transportChoice.Active = 0;
        root.PackStart(transportChoice, false, false, 0);
        transportChoice.Changed += async (_, _) => await RefreshAsync();
        usbProfilesButton.Clicked += (_, _) => OpenUsbProfiles();
        root.PackStart(usbProfilesButton, false, false, 0);
        calibrationButton.Clicked += (_, _) => new ControllerCalibrationWindow(this).ShowAll();

        var selector = new Box(Orientation.Horizontal, 8);
        selector.PackStart(new Label("Device:") { Xalign = 0 }, false, false, 0);
        selector.PackStart(dongles, true, true, 0);
        selector.PackStart(refreshButton, false, false, 0);
        selector.PackStart(toolsButton, false, false, 0);
        root.PackStart(selector, false, false, 0);

        toolsMenu.Append(updateDongleItem);
        toolsMenu.Append(new SeparatorMenuItem());
        toolsMenu.Append(unpairControllerItem);
        toolsMenu.Append(unpairDongleItem);
        toolsMenu.Append(new SeparatorMenuItem());
        toolsMenu.Append(profileItem);
        toolsButton.Popup = toolsMenu;
        toolsMenu.ShowAll();

        var statusFrame = new Frame("Device status") { BorderWidth = 8 };
        var statusBox = new Box(Orientation.Vertical, 6) { BorderWidth = 10 };
        deviceSummary.Xalign = 0;
        deviceSummary.LineWrap = true;
        deviceSummary.MaxWidthChars = 78;
        controllerSummary.Xalign = 0;
        controllerSummary.LineWrap = true;
        controllerSummary.MaxWidthChars = 78;
        statusBox.PackStart(deviceSummary, false, false, 0);
        statusBox.PackStart(controllerSummary, false, false, 0);
        statusFrame.Add(statusBox);
        root.PackStart(statusFrame, false, false, 0);

        progress.ShowText = true;
        progress.Text = "Idle";
        root.PackStart(progress, false, false, 0);
        var scroll = new ScrolledWindow { ShadowType = ShadowType.In };
        scroll.Add(log);
        root.PackStart(scroll, true, true, 0);

        var actions = new Box(Orientation.Horizontal, 8);
        actions.PackEnd(updateButton, false, false, 0);
        actions.PackEnd(cancelButton, false, false, 0);
        actions.PackEnd(localButton, false, false, 0);
        root.PackStart(actions, false, false, 0);
        Add(root);

        DeleteEvent += (_, args) =>
        {
            if (operation != null)
            {
                closeRequested = true;
                operation.Cancel();
                args.RetVal = true;
                return;
            }
            Application.Quit();
            args.RetVal = true;
        };
        refreshButton.Clicked += async (_, _) => await RefreshAsync();
        dongles.Changed += (_, _) => ShowSelectedDevice();
        unpairControllerItem.Activated += async (_, _) =>
            await UnpairControllerAsync();
        unpairDongleItem.Activated += async (_, _) =>
            await UnpairDongleAsync();
        profileItem.Activated += async (_, _) => await ChangeUsbProfileAsync();
        updateDongleItem.Activated += async (_, _) =>
        {
            updatingDongleOnly = true;
            try { await RunOnlineUpdateAsync(); }
            finally { updatingDongleOnly = false; }
        };
        updateButton.Clicked += async (_, _) => await RunOnlineUpdateAsync();
        localButton.Clicked += async (_, _) => await RunLocalUpdateAsync();
        cancelButton.Clicked += (_, _) => operation?.Cancel();

        Shown += async (_, _) => await RefreshAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CloseControllerWakeDialog();
            operation?.Dispose();
        }
        base.Dispose(disposing);
    }

    private async Task RefreshAsync()
    {
        if (operation != null)
        {
            return;
        }
        var preferredSerial = SelectedDongleSerial();
        var preferredMac = SelectedDongleMac();
        var usbSerial = SelectedUsbDescriptor()?.Serial;
        SetBusy(true, DirectUsb ? "Searching for USB Controllers..." : "Searching for Dongles...");
        try
        {
            if (DirectUsb)
            {
                await RefreshUsbAsync(usbSerial);
                AppendLog($"Found {usbDescriptors.Count} direct USB Controller interface(s).");
            }
            else
            {
                var discovered = await discovery.DiscoverAsync(CancellationToken.None);
                ReplaceDescriptors(discovered, preferredSerial, preferredMac);
                AppendLog($"Found {descriptors.Count} BandHub Dongle interface(s).");
            }
        }
        catch (Exception error)
        {
            ShowError(error);
        }
        finally
        {
            SetBusy(false, "Idle");
            ShowSelectedDevice();
        }
    }

    private void ShowSelectedDevice()
    {
        updateButton.Label = DirectUsb ? "Update Controller" : "Update all";
        localButton.Label = DirectUsb ? "Use local firmware..." : "Use local bundle...";
        if (DirectUsb)
        {
            var selected = SelectedUsbDescriptor();
            deviceSummary.Text = selected == null ? "Connect a Controller directly by USB and refresh." :
                $"{selected.Name} — serial {selected.Serial}";
            controllerSummary.Text = selected?.Info is { } usbInfo
                ? $"Firmware 0x{usbInfo.FirmwareVersion:X8} — {usbInfo.Health}. {selected.UnsupportedReason}"
                : selected?.UnsupportedReason ?? string.Empty;
            updateButton.Sensitive = !busy && operation == null && selected?.Supported == true;
            localButton.Sensitive = updateButton.Sensitive;
            UpdateToolsMenu();
            return;
        }
        var descriptor = SelectedDescriptor();
        if (descriptor == null)
        {
            updateButton.Sensitive = false;
            localButton.Sensitive = false;
            UpdateToolsMenu();
            return;
        }
        UpdateToolsMenu();
        if (!descriptor.Supported || descriptor.DeviceInfo == null)
        {
            deviceSummary.Text =
                $"USB profile: {UsbProfileName(descriptor.UsbProfile)}. " +
                descriptor.UnsupportedReason;
            controllerSummary.Text = string.Empty;
            updateButton.Sensitive = false;
            localButton.Sensitive = false;
            return;
        }

        var info = descriptor.DeviceInfo;
        deviceSummary.Text =
            $"Dongle: {info.DongleTarget}, firmware 0x{info.DongleFirmwareVersion:X8}, " +
            $"USB profile {UsbProfileName(descriptor.UsbProfile)}";
        controllerSummary.Text = (info.ControllerFeatureFlags & (1U << 13)) != 0 &&
                !OtaProtocol.IsSuperMiniTarget(info.ControllerTarget)
            ? "This Dongle cannot identify SuperMini firmware. Use Tools > Update Dongle only, then refresh."
            : !info.ControllerConnected
            ? "Controller: not connected"
            : !info.SupportsControllerPackageV2
                ? $"Controller: firmware 0x{info.ControllerFirmwareVersion:X8} requires a wired bridge update"
                : $"Controller: {info.ControllerTarget}, firmware 0x{info.ControllerFirmwareVersion:X8}, battery " +
                  (info.ControllerExternallyPowered
                      ? "absent (external power)"
                      : info.BatteryValid ? $"{info.ControllerBatteryPercent}%" : "unavailable");
        updateButton.Sensitive = operation == null && ControllerIdentityReady(info);
        localButton.Sensitive = updateButton.Sensitive;
    }

    private void UpdateToolsMenu()
    {
        var descriptor = SelectedDescriptor();
        var idle = operation == null && !busy;
        updateDongleItem.Sensitive = idle && descriptor?.Supported == true;

        unpairControllerItem.Sensitive = idle &&
            BindingToolAvailability.CanUnpairController(descriptor);
        unpairDongleItem.Sensitive = idle &&
            BindingToolAvailability.CanUnpairDongle(descriptor);
        profileItem.Label = "USB profiles...";
        profileItem.Sensitive = idle && descriptor?.CanSwitchUsbProfile == true;
        toolsButton.Sensitive = idle && descriptor != null &&
            (updateDongleItem.Sensitive || profileItem.Sensitive || unpairControllerItem.Sensitive ||
             unpairDongleItem.Sensitive);
    }

    private void OpenUsbProfiles()
    {
        if (operation != null) return;
        var window = new UsbProfilesWindow(this);
        window.Destroyed += async (_, _) => await RefreshAsync();
        window.ShowAll();
    }

    private Task ChangeUsbProfileAsync()
    {
        OpenUsbProfiles();
        return Task.CompletedTask;
    }

    private bool ConfirmPs3ProfileSelection()
    {
        using var dialog = new MessageDialog(
            this,
            DialogFlags.Modal,
            MessageType.Question,
            ButtonsType.None,
            "Switch this Dongle to the PS3 Rock Band Guitar profile?");
        dialog.Title = "Change USB profile";
        dialog.SecondaryText =
            "Firmware updates are available only in the PC HID profile. " +
            "You can return to PC HID from this application at any time.";
        dialog.AddButton("Cancel", ResponseType.Cancel);
        dialog.AddButton("Switch profile", ResponseType.Accept);
        dialog.DefaultResponse = ResponseType.Accept;
        return (ResponseType)dialog.Run() == ResponseType.Accept;
    }

    private async Task UnpairControllerAsync()
    {
        var descriptor = SelectedDescriptor();
        if (descriptor == null || operation != null ||
            !ConfirmControllerUnpair())
        {
            return;
        }
        await RunBindingActionAsync(descriptor, unpairController: true);
    }

    private async Task UnpairDongleAsync()
    {
        var descriptor = SelectedDescriptor();
        if (descriptor == null || operation != null || !ConfirmDongleUnpair())
        {
            return;
        }
        await RunBindingActionAsync(descriptor, unpairController: false);
    }

    private async Task RunBindingActionAsync(
        DongleDescriptor descriptor,
        bool unpairController)
    {
        operation = new CancellationTokenSource();
        SetBusy(
            true,
            unpairController ? "Unpairing Controller..." : "Unpairing Dongle...",
            allowCancel: false);
        AppendLog(unpairController
            ? "Requesting remote Controller unpair..."
            : "Clearing the saved Controller binding from the Dongle...");
        try
        {
            if (unpairController)
            {
                await bindingService.UnpairControllerAsync(
                    descriptor, TimeSpan.FromSeconds(12), operation.Token);
                AppendLog("Controller and Dongle were unpaired successfully.");
            }
            else
            {
                await bindingService.UnpairDongleAsync(
                    descriptor, TimeSpan.FromSeconds(5), operation.Token);
                AppendLog("Dongle binding was cleared successfully.");
            }
            await RefreshAfterOperationAsync();
        }
        catch (OperationCanceledException)
        {
            AppendLog("Binding operation cancelled.");
        }
        catch (Exception error)
        {
            ShowError(error);
        }
        finally
        {
            operation.Dispose();
            operation = null;
            SetBusy(false, "Idle");
            ShowSelectedDevice();
            if (closeRequested) Application.Quit();
        }
    }

    private bool ConfirmControllerUnpair()
    {
        using var dialog = new MessageDialog(
            this,
            DialogFlags.Modal,
            MessageType.Warning,
            ButtonsType.None,
            "Unpair the connected Controller?");
        dialog.Title = "Unpair Controller";
        dialog.SecondaryText =
            "The Controller must be awake. This removes the saved binding from both the Controller and Dongle. " +
            "The Controller will pause automatic pairing until it restarts.";
        dialog.AddButton("Cancel", ResponseType.Cancel);
        dialog.AddButton("Unpair Controller", ResponseType.Accept);
        dialog.DefaultResponse = ResponseType.Cancel;
        return (ResponseType)dialog.Run() == ResponseType.Accept;
    }

    private bool ConfirmDongleUnpair()
    {
        using var dialog = new MessageDialog(
            this,
            DialogFlags.Modal,
            MessageType.Warning,
            ButtonsType.None,
            "Clear the saved binding from this Dongle?");
        dialog.Title = "Unpair Dongle";
        dialog.SecondaryText =
            "This recovery action clears only the Dongle. The Controller remains bound to this Dongle and " +
            "cannot pair with another Dongle until its own binding is cleared.";
        dialog.AddButton("Cancel", ResponseType.Cancel);
        dialog.AddButton("Unpair Dongle", ResponseType.Accept);
        dialog.DefaultResponse = ResponseType.Cancel;
        return (ResponseType)dialog.Run() == ResponseType.Accept;
    }

    private async Task RunOnlineUpdateAsync()
    {
        try
        {
            await RunUpdateAsync(new CloudflareR2ReleaseSource());
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private async Task RunLocalUpdateAsync()
    {
        using var chooser = new FileChooserDialog(
            "Select a signed BandHub release bundle",
            this,
            FileChooserAction.Open,
            "Cancel", ResponseType.Cancel,
            "Open", ResponseType.Accept);
        var filter = new FileFilter { Name = DirectUsb ? "Signed Controller firmware (*.bhfw, *.bhrelease)" : "BandHub release bundles (*.bhrelease)" };
        if (DirectUsb) filter.AddPattern("*.bhfw");
        filter.AddPattern("*.bhrelease");
        chooser.AddFilter(filter);
        if ((ResponseType)chooser.Run() != ResponseType.Accept)
        {
            return;
        }
        await RunUpdateAsync(new LocalBundleReleaseSource(chooser.Filename));
    }

    private async Task RunUpdateAsync(IReleaseSource source)
    {
        var descriptor = SelectedDescriptor();
        var usbDescriptor = SelectedUsbDescriptor();
        if ((DirectUsb ? usbDescriptor == null : descriptor == null) || operation != null)
        {
            if (source is IDisposable unused) unused.Dispose();
            return;
        }
        operation = new CancellationTokenSource();
        SetBusy(true, "Preparing update...");
        var reporter = new Progress<UpdateProgress>(value =>
        {
            if (value.Stage == "controller-wake" && value.Percent < 100)
            {
                ShowControllerWakeDialog();
            }
            else
            {
                CloseControllerWakeDialog();
            }
            if (value.Stage == "usb-commit") cancelButton.Sensitive = false;
            progress.Fraction = value.Percent / 100.0;
            progress.Text = $"{value.Percent}% — {value.Message}";
            AppendLog(value.Message);
        });
        try
        {
            if (DirectUsb)
                await new ControllerUsbUpdateService(usbDiscovery).UpdateAsync(
                    usbDescriptor!, source, ConfirmDevicesReadyAsync, reporter, operation.Token);
            else
                await updateService.UpdateAllAsync(
                    descriptor!, source, ConfirmDevicesReadyAsync, reporter, operation.Token, updatingDongleOnly);
            await RefreshAfterOperationAsync();
        }
        catch (OperationCanceledException)
        {
            CloseControllerWakeDialog();
            AppendLog("Update cancelled before completion.");
        }
        catch (Exception error)
        {
            CloseControllerWakeDialog();
            ShowError(error);
        }
        finally
        {
            CloseControllerWakeDialog();
            if (source is IDisposable disposable)
            {
                disposable.Dispose();
            }
            operation.Dispose();
            operation = null;
            SetBusy(false, "Idle");
            ShowSelectedDevice();
            if (closeRequested) Application.Quit();
        }
    }

    private Task<bool> ConfirmDevicesReadyAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Application.Invoke(delegate
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                using var dialog = new MessageDialog(
                    this,
                    DialogFlags.Modal,
                    MessageType.Question,
                    ButtonsType.None,
                    "Firmware download completed.");
                dialog.Title = "Ready to install firmware";
                dialog.SecondaryText = DirectUsb
                    ? "Keep the selected Controller connected directly by USB. Release all controls. " +
                      "Pairing and calibration will be preserved. Cancellation is available until installation begins."
                    : updatingDongleOnly
                        ? "Keep the selected Dongle connected. Only the Dongle firmware will be updated; the Controller will not be flashed."
                        : "Before continuing, make sure:\n\n" +
                    "• The Controller is powered on.\n" +
                    "• The Controller is paired and connected to the Dongle.\n" +
                    "• The Dongle remains connected to this computer.\n\n" +
                    "Continue with firmware installation?";
                dialog.AddButton("Cancel", ResponseType.Cancel);
                dialog.AddButton("Continue", ResponseType.Accept);
                dialog.DefaultResponse = ResponseType.Accept;
                var response = (ResponseType)dialog.Run();
                completion.TrySetResult(response == ResponseType.Accept);
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        });
        return completion.Task;
    }

    private void ShowControllerWakeDialog()
    {
        if (controllerWakeDialog != null)
        {
            return;
        }

        var dialog = new MessageDialog(
            this,
            DialogFlags.Modal,
            MessageType.Info,
            ButtonsType.None,
            "Dongle update completed.");
        dialog.Title = "Wake the Controller";
        dialog.SecondaryText =
            "Press the PS button to wake the Controller.\n\n" +
            "The update will continue automatically as soon as the Controller reconnects.";
        dialog.AddButton("Cancel update", ResponseType.Cancel);
        dialog.Response += (_, _) => operation?.Cancel();
        dialog.DeleteEvent += (_, args) =>
        {
            operation?.Cancel();
            args.RetVal = true;
        };
        controllerWakeDialog = dialog;
        dialog.ShowAll();
    }

    private void CloseControllerWakeDialog()
    {
        var dialog = controllerWakeDialog;
        if (dialog == null)
        {
            return;
        }

        controllerWakeDialog = null;
        dialog.Destroy();
        dialog.Dispose();
    }

    private async Task RefreshAfterOperationAsync()
    {
        if (DirectUsb) { await RefreshUsbAsync(SelectedUsbDescriptor()?.Serial); return; }
        var preferredSerial = SelectedDongleSerial();
        var preferredMac = SelectedDongleMac();
        var discovered = await discovery.DiscoverAsync(CancellationToken.None);
        ReplaceDescriptors(discovered, preferredSerial, preferredMac);
        AppendLog("Device information refreshed after update.");
    }

    private void ReplaceDescriptors(
        IReadOnlyList<DongleDescriptor> discovered,
        string? preferredSerial,
        byte[]? preferredMac)
    {
        descriptors = discovered;
        dongles.RemoveAll();
        foreach (var descriptor in descriptors)
        {
            dongles.AppendText(descriptor.DisplayName);
        }

        var selectedIndex = DongleDescriptorSelection.FindByIdentity(
            descriptors, preferredSerial, preferredMac);
        if (selectedIndex < 0 && descriptors.Count > 0)
        {
            selectedIndex = 0;
        }
        dongles.Active = selectedIndex;
        if (selectedIndex < 0)
        {
            deviceSummary.Text = "No BandHub Dongle was found.";
            controllerSummary.Text = string.Empty;
        }
    }

    private byte[]? SelectedDongleMac() =>
        SelectedDescriptor()?.DeviceInfo?.DongleMac.ToArray();

    private string? SelectedDongleSerial() =>
        SelectedDescriptor()?.SerialNumber;

    private static string UsbProfileName(UsbProfile profile) => profile switch
    {
        UsbProfile.PcHid => "PC HID",
        UsbProfile.Ps3RockBandGuitar => "PS3 Rock Band Guitar",
        UsbProfile.Xbox360GuitarHero => "Xbox 360 Guitar Hero",
        _ => "unknown",
    };

    private async Task RefreshUsbAsync(string? preferredSerial)
    {
        usbDescriptors = await usbDiscovery.DiscoverAsync(CancellationToken.None);
        dongles.RemoveAll();
        foreach (var device in usbDescriptors) dongles.AppendText($"{device.Name} ({device.Serial})");
        var index = usbDescriptors.ToList().FindIndex(d => d.Serial == preferredSerial);
        dongles.Active = index >= 0 ? index : usbDescriptors.Count > 0 ? 0 : -1;
    }

    private ControllerUpdateDescriptor? SelectedUsbDescriptor() =>
        DirectUsb && dongles.Active >= 0 && dongles.Active < usbDescriptors.Count ? usbDescriptors[dongles.Active] : null;

    private DongleDescriptor? SelectedDescriptor()
    {
        if (DirectUsb) return null;
        var index = dongles.Active;
        return index >= 0 && index < descriptors.Count ? descriptors[index] : null;
    }

    private static bool ControllerIdentityReady(DeviceInfo info) =>
        info.SupportsControllerOta && info.SupportsControllerPackageV2 &&
        info.ControllerBound && info.ControllerConnected &&
        info.ControllerFirmwareVersion != 0 &&
        OtaProtocol.IsControllerTarget(info.ControllerTarget) &&
        (((info.ControllerFeatureFlags & (1U << 13)) != 0) == OtaProtocol.IsSuperMiniTarget(info.ControllerTarget));

    private void SetBusy(bool busy, string text, bool allowCancel = true)
    {
        this.busy = busy;
        transportChoice.Sensitive = !busy;
        usbProfilesButton.Sensitive = !busy;
        calibrationButton.Sensitive = !busy;
        refreshButton.Sensitive = !busy;
        dongles.Sensitive = !busy;
        var selected = SelectedDescriptor();
        updateButton.Sensitive = !busy && selected?.Supported == true &&
                                 selected.DeviceInfo != null &&
                                 ControllerIdentityReady(selected.DeviceInfo);
        if (DirectUsb) updateButton.Sensitive = !busy && SelectedUsbDescriptor()?.Supported == true;
        localButton.Sensitive = updateButton.Sensitive;
        UpdateToolsMenu();
        cancelButton.Sensitive = busy && allowCancel && operation != null;
        progress.Text = text;
        if (!busy)
        {
            progress.Fraction = 0;
        }
    }

    private void AppendLog(string message)
    {
        var timestamped = $"[{DateTime.Now:HH:mm:ss}] {message}\n";
        log.Buffer.InsertAtCursor(timestamped);
        log.ScrollToIter(log.Buffer.EndIter, 0, false, 0, 0);
    }

    private void ShowError(Exception error)
    {
        AppendLog("ERROR: " + error.Message);
        using var dialog = new MessageDialog(
            this,
            DialogFlags.Modal,
            MessageType.Error,
            ButtonsType.Close,
            error.Message);
        dialog.Title = "BandHub Firmware Update";
        dialog.Run();
    }
}
