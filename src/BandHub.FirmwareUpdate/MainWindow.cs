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
    private readonly Label deviceSummary = new("Connect a BandHub Dongle and refresh.");
    private readonly Label controllerSummary = new(string.Empty);
    private readonly ProgressBar progress = new();
    private readonly TextView log = new() { Editable = false, WrapMode = WrapMode.WordChar };
    private readonly Button refreshButton = new("Refresh");
    private readonly Button updateButton = new("Update all") { Sensitive = false };
    private readonly Button localButton = new("Use local bundle...") { Sensitive = false };
    private readonly Button cancelButton = new("Cancel") { Sensitive = false };
    private IReadOnlyList<DongleDescriptor> descriptors = Array.Empty<DongleDescriptor>();
    private CancellationTokenSource? operation;

    public MainWindow() : base("BandHub Firmware Update")
    {
        updateService = new FirmwareUpdateService(discovery);
        DefaultWidth = 680;
        DefaultHeight = 520;
        BorderWidth = 18;
        WindowPosition = WindowPosition.Center;

        var root = new Box(Orientation.Vertical, 12);
        var title = new Label { Markup = "<span size='xx-large' weight='bold'>BandHub Firmware Update</span>", Xalign = 0 };
        var description = new Label("Update the Dongle and its paired Controller using signed stable releases.")
        {
            Xalign = 0,
            LineWrap = true,
        };
        root.PackStart(title, false, false, 0);
        root.PackStart(description, false, false, 0);

        var selector = new Box(Orientation.Horizontal, 8);
        selector.PackStart(new Label("Dongle:") { Xalign = 0 }, false, false, 0);
        selector.PackStart(dongles, true, true, 0);
        selector.PackStart(refreshButton, false, false, 0);
        root.PackStart(selector, false, false, 0);

        var statusFrame = new Frame("Device status") { BorderWidth = 8 };
        var statusBox = new Box(Orientation.Vertical, 6) { BorderWidth = 10 };
        deviceSummary.Xalign = 0;
        controllerSummary.Xalign = 0;
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
            operation?.Cancel();
            Application.Quit();
            args.RetVal = true;
        };
        refreshButton.Clicked += async (_, _) => await RefreshAsync();
        dongles.Changed += (_, _) => ShowSelectedDevice();
        updateButton.Clicked += async (_, _) => await RunOnlineUpdateAsync();
        localButton.Clicked += async (_, _) => await RunLocalUpdateAsync();
        cancelButton.Clicked += (_, _) => operation?.Cancel();

        Shown += async (_, _) => await RefreshAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
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
        var preferredMac = SelectedDongleMac();
        SetBusy(true, "Searching for Dongles...");
        try
        {
            var discovered = await discovery.DiscoverAsync(CancellationToken.None);
            ReplaceDescriptors(discovered, preferredMac);
            AppendLog($"Found {descriptors.Count} BandHub Dongle interface(s).");
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
        var descriptor = SelectedDescriptor();
        if (descriptor == null)
        {
            updateButton.Sensitive = false;
            localButton.Sensitive = false;
            return;
        }
        if (!descriptor.Supported || descriptor.DeviceInfo == null)
        {
            deviceSummary.Text = descriptor.UnsupportedReason;
            controllerSummary.Text = string.Empty;
            updateButton.Sensitive = false;
            localButton.Sensitive = false;
            return;
        }

        var info = descriptor.DeviceInfo;
        deviceSummary.Text =
            $"Dongle: {info.DongleTarget}, firmware 0x{info.DongleFirmwareVersion:X8}";
        controllerSummary.Text = !info.ControllerConnected
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
        var filter = new FileFilter { Name = "BandHub release bundles (*.bhrelease)" };
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
        if (descriptor == null || operation != null)
        {
            return;
        }
        operation = new CancellationTokenSource();
        SetBusy(true, "Preparing update...");
        var reporter = new Progress<UpdateProgress>(value =>
        {
            progress.Fraction = value.Percent / 100.0;
            progress.Text = $"{value.Percent}% — {value.Message}";
            AppendLog(value.Message);
        });
        try
        {
            await updateService.UpdateAllAsync(
                descriptor,
                source,
                ConfirmDevicesReadyAsync,
                reporter,
                operation.Token);
            await RefreshAfterOperationAsync();
        }
        catch (OperationCanceledException)
        {
            AppendLog("Update cancelled before completion.");
        }
        catch (Exception error)
        {
            ShowError(error);
        }
        finally
        {
            if (source is IDisposable disposable)
            {
                disposable.Dispose();
            }
            operation.Dispose();
            operation = null;
            SetBusy(false, "Idle");
            ShowSelectedDevice();
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
                dialog.SecondaryText =
                    "Before continuing, make sure:\n\n" +
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

    private async Task RefreshAfterOperationAsync()
    {
        var preferredMac = SelectedDongleMac();
        var discovered = await discovery.DiscoverAsync(CancellationToken.None);
        ReplaceDescriptors(discovered, preferredMac);
        AppendLog("Device information refreshed after update.");
    }

    private void ReplaceDescriptors(
        IReadOnlyList<DongleDescriptor> discovered,
        byte[]? preferredMac)
    {
        descriptors = discovered;
        dongles.RemoveAll();
        foreach (var descriptor in descriptors)
        {
            dongles.AppendText(descriptor.DisplayName);
        }

        var selectedIndex = DongleDescriptorSelection.FindByMac(
            descriptors, preferredMac);
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

    private DongleDescriptor? SelectedDescriptor()
    {
        var index = dongles.Active;
        return index >= 0 && index < descriptors.Count ? descriptors[index] : null;
    }

    private static bool ControllerIdentityReady(DeviceInfo info) =>
        info.SupportsControllerOta && info.SupportsControllerPackageV2 &&
        info.ControllerBound && info.ControllerConnected &&
        info.ControllerFirmwareVersion != 0 &&
        (info.ControllerTarget == FirmwareTarget.ControllerGh3 ||
         info.ControllerTarget == FirmwareTarget.ControllerGh5);

    private void SetBusy(bool busy, string text)
    {
        refreshButton.Sensitive = !busy;
        dongles.Sensitive = !busy;
        var selected = SelectedDescriptor();
        updateButton.Sensitive = !busy && selected?.Supported == true &&
                                 selected.DeviceInfo != null &&
                                 ControllerIdentityReady(selected.DeviceInfo);
        localButton.Sensitive = updateButton.Sensitive;
        cancelButton.Sensitive = busy && operation != null;
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
