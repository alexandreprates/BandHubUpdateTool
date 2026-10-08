using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BandHub.FirmwareUpdate.Core;
using Gtk;

namespace BandHub.FirmwareUpdate;

internal sealed class ControllerCalibrationWindow : Window
{
    private readonly ComboBoxText devices = new();
    private readonly ComboBoxText connectionChoice = new();
    private readonly Button connect = new("Connect");
    private readonly Label status = new("Choose direct USB or wireless through a Dongle.") { Xalign = 0, LineWrap = true };
    private readonly Label live = new("Waiting for diagnostics.") { Xalign = 0, Selectable = true };
    private readonly SpinButton rest = new(0, 4095, 1), full = new(0, 4095, 1);
    private readonly SpinButton deadband = new(0, 32, 1), release = new(1, 20, 1), guard = new(0, 10, 1);
    private readonly CheckButton sleep = new("Enable wireless inactivity sleep (START wakes)");
    private readonly SpinButton disconnectedSleep = new(60, 3600, 1), connectedSleep = new(60, 7200, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Box settings = new(Orientation.Vertical, 8) { Sensitive = false };
    private IReadOnlyList<ControllerUsbDevice> discovered = Array.Empty<ControllerUsbDevice>();
    private ControllerManagement? connection;
    private ControllerCalibration current = new();
    private bool busy, closed;
    private ushort raw;
    private long lastSample;
    private readonly uint timer;

    public ControllerCalibrationWindow(Window parent, bool wireless = false, string? preferredPath = null)
        : base("Controller calibration and settings")
    {
        TransientFor = parent; Modal = true; DefaultWidth = 640; DefaultHeight = 490; BorderWidth = 16;
        var root = new Box(Orientation.Vertical, 10);
        root.PackStart(new Label("1. Choose USB or Dongle.  2. Capture whammy endpoints.  3. Release controls and save.")
            { Xalign = 0, LineWrap = true }, false, false, 0);
        connectionChoice.AppendText("Direct USB Controller");
        connectionChoice.AppendText("Wireless through Dongle");
        connectionChoice.Active = wireless ? 1 : 0;
        root.PackStart(connectionChoice, false, false, 0);
        var selector = new Box(Orientation.Horizontal, 8);
        var refresh = new Button("Refresh");
        selector.PackStart(devices, true, true, 0); selector.PackStart(refresh, false, false, 0); selector.PackStart(connect, false, false, 0);
        root.PackStart(selector, false, false, 0); root.PackStart(status, false, false, 0); root.PackStart(live, false, false, 0);
        var grid = new Grid { RowSpacing = 8, ColumnSpacing = 12 };
        AddRow(grid, 0, "Whammy at rest (raw ADC)", rest);
        AddRow(grid, 1, "Whammy fully pressed (raw ADC)", full);
        AddRow(grid, 2, "Neutral deadband (0–32)", deadband);
        AddRow(grid, 3, "Release debounce (1–20 ms)", release);
        AddRow(grid, 4, "Strum guard (0–10 ms)", guard);
        var captureRest = new Button("Capture rest"); var captureFull = new Button("Capture full");
        grid.Attach(captureRest, 2, 0, 1, 1); grid.Attach(captureFull, 2, 1, 1, 1);
        AddRow(grid, 5, "Disconnected idle timeout (seconds)", disconnectedSleep);
        AddRow(grid, 6, "Connected idle timeout (seconds)", connectedSleep);
        settings.PackStart(grid, false, false, 0);
        settings.PackStart(sleep, false, false, 0);
        settings.PackStart(new Label("Sleep is blocked on USB or while controls are held. Cable detection checks once per second.\nValidate battery wiring and START wake before enabling on an assembled guitar.") { Xalign = 0, LineWrap = true }, false, false, 0);
        settings.PackStart(new Label("Move the whammy through its full travel. Endpoint span must be at least 128 ADC units.\nInversion is detected from the endpoint order. Import changes the draft; Save applies it.")
            { Xalign = 0, LineWrap = true }, false, false, 0);
        var actions = new Box(Orientation.Horizontal, 8);
        var save = new Button("Save to Controller"); var reset = new Button("Restore defaults");
        var import = new Button("Import..."); var export = new Button("Export...");
        foreach (var button in new[] { save, reset, import, export }) actions.PackStart(button, false, false, 0);
        settings.PackStart(actions, false, false, 0); root.PackStart(settings, false, false, 0); Add(root);
        async Task RefreshDevices()
        {
            connection?.Dispose(); connection = null; lastSample = 0;
            var found = connectionChoice.Active == 1
                ? await ControllerManagement.DiscoverWirelessAsync(lifetime.Token)
                : await Task.Run(ControllerManagement.Discover, lifetime.Token);
            Ui(() =>
            {
                discovered = found; devices.RemoveAll();
                foreach (var item in found) devices.AppendText(item.Name);
                var preferred = -1;
                for (var i = 0; i < found.Count; ++i) if (found[i].Path == preferredPath) preferred = i;
                devices.Active = preferred >= 0 ? preferred : found.Count > 0 ? 0 : -1;
                status.Text = found.Count == 0
                    ? connectionChoice.Active == 1 ? "No Dongle found. Connect a PC HID Dongle and turn on the paired Controller." : "No Controller found. Connect the Controller by USB and refresh."
                    : found[devices.Active].Supported ? "Select the Controller and click Connect to read its settings." : found[devices.Active].UnsupportedReason;
            });
        }
        refresh.Clicked += async (_, _) => await Run(RefreshDevices);
        connectionChoice.Changed += async (_, _) => await Run(RefreshDevices);
        devices.Changed += (_, _) =>
        {
            connection?.Dispose(); connection = null; lastSample = 0; settings.Sensitive = false;
            if (devices.Active >= 0 && devices.Active < discovered.Count)
            {
                var selected = discovered[devices.Active];
                connect.Sensitive = !busy && selected.Supported;
                if (!selected.Supported) status.Text = selected.UnsupportedReason;
            }
        };
        connect.Clicked += async (_, _) =>
        {
            if (busy || devices.Active < 0) return;
            var selected = discovered[devices.Active];
            if (!selected.Supported) { status.Text = selected.UnsupportedReason; return; }
            await Run(async () =>
            {
                connection?.Dispose(); connection = null;
                var candidate = new ControllerManagement(selected);
                try
                {
                    await candidate.ConnectAsync(lifetime.Token);
                    var value = await candidate.ReadAsync(lifetime.Token);
                    connection = candidate;
                    var profile = candidate.Profile;
                    Ui(() => { ShowValue(value); status.Text = $"GH{profile} connected {(selected.Dongle != null ? "through Dongle" : "by USB")}. Diagnostics refresh four times per second."; });
                }
                catch { candidate.Dispose(); throw; }
            });
        };
        captureRest.Clicked += (_, _) => Capture(rest);
        captureFull.Clicked += (_, _) => Capture(full);
        save.Clicked += async (_, _) =>
        {
            if (busy || connection == null) return;
            ControllerCalibration draft;
            try { draft = Draft(); } catch (Exception e) { status.Text = e.Message; return; }
            await Run(async () => { var value = await connection.WriteAsync(draft, lifetime.Token); Ui(() => { ShowValue(value); status.Text = "Calibration saved and active."; }); });
        };
        reset.Clicked += async (_, _) =>
        {
            if (connection != null) await Run(async () => { var value = await connection.ResetAsync(lifetime.Token); Ui(() => { ShowValue(value); status.Text = "Defaults saved and active."; }); });
        };
        import.Clicked += (_, _) => FileAction(false);
        export.Clicked += (_, _) => FileAction(true);
        timer = GLib.Timeout.Add(250, () => { if (closed) return false; if (!busy && connection != null) _ = Run(ReadDiagnostics); return true; });
        DeleteEvent += (_, _) => CloseConnection();
        Destroyed += (_, _) => CloseConnection();
        ShowValue(current);
        Shown += async (_, _) => await Run(RefreshDevices);
    }
    private void CloseConnection()
    {
        if (closed) return;
        closed = true; lifetime.Cancel(); GLib.Source.Remove(timer); connection?.Dispose();
    }
    private static void AddRow(Grid grid, int row, string text, Widget field)
    { grid.Attach(new Label(text) { Xalign = 0 }, 0, row, 1, 1); grid.Attach(field, 1, row, 1, 1); }
    private void Ui(System.Action action) => Application.Invoke((_, _) => { if (!closed) action(); });
    private async Task Run(Func<Task> action)
    {
        if (busy || closed) return;
        busy = true;
        connectionChoice.Sensitive = devices.Sensitive = connect.Sensitive = false;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (IOException error) { connection?.Dispose(); connection = null; Ui(() => status.Text = error.Message); }
        catch (Exception error) { Ui(() => status.Text = error.Message); }
        finally { Ui(() => { busy = false; connectionChoice.Sensitive = devices.Sensitive = true;
            connect.Sensitive = devices.Active >= 0 && devices.Active < discovered.Count && discovered[devices.Active].Supported;
            settings.Sensitive = connection?.Profile is 3 or 5;
            sleep.Sensitive = disconnectedSleep.Sensitive = connectedSleep.Sensitive = connection?.CanSleep == true; }); }
    }
    private void Capture(SpinButton field)
    {
        if (Environment.TickCount64 - lastSample > 1000) { status.Text = "Wait for a fresh diagnostic sample."; return; }
        field.Value = raw;
    }
    private ControllerCalibration Draft()
    {
        var a = (ushort)rest.ValueAsInt; var b = (ushort)full.ValueAsInt;
        var value = current with { WhammyMin = Math.Min(a, b), WhammyMax = Math.Max(a, b), Inverted = a > b,
            Deadband = (byte)deadband.ValueAsInt, ReleaseMs = (byte)release.ValueAsInt, StrumGuardMs = (byte)guard.ValueAsInt, SleepEnabled = sleep.Active,
            DisconnectedSleepSeconds = (ushort)disconnectedSleep.ValueAsInt, ConnectedSleepSeconds = (ushort)connectedSleep.ValueAsInt };
        value.Validate(); return value;
    }
    private void ShowValue(ControllerCalibration value)
    {
        current = value; rest.Value = value.Inverted ? value.WhammyMax : value.WhammyMin;
        full.Value = value.Inverted ? value.WhammyMin : value.WhammyMax;
        deadband.Value = value.Deadband; release.Value = value.ReleaseMs; guard.Value = value.StrumGuardMs;
        sleep.Active = value.SleepEnabled; disconnectedSleep.Value = value.DisconnectedSleepSeconds; connectedSleep.Value = value.ConnectedSleepSeconds;
    }
    private async Task ReadDiagnostics()
    {
        var response = await connection!.DiagnosticsAsync(lifetime.Token);
        var d = response.AsMemory(12);
        var rawValue = BinaryPrimitives.ReadUInt16LittleEndian(d.Span[12..]);
        var text = $"Raw whammy: {rawValue}    Calibrated: {d.Span[14]}    GH5 neck: {(d.Span[15] != 0 ? "connected" : "absent")}\n" +
            $"Frets/strum: 0x{BinaryPrimitives.ReadUInt32LittleEndian(d.Span[8..]):X8}    Body: 0x{BinaryPrimitives.ReadUInt32LittleEndian(d.Span[4..]):X8}\n" +
            $"Transport queued: {BinaryPrimitives.ReadUInt32LittleEndian(d.Span[16..])}    Delivered: {BinaryPrimitives.ReadUInt32LittleEndian(d.Span[20..])}    Dropped: {BinaryPrimitives.ReadUInt32LittleEndian(d.Span[24..])}\n" +
            $"Overflows: {BinaryPrimitives.ReadUInt32LittleEndian(d.Span[28..])}    Expired: {BinaryPrimitives.ReadUInt32LittleEndian(d.Span[32..])}    Max queue age: {BinaryPrimitives.ReadUInt32LittleEndian(d.Span[36..])} ms\n" +
            $"GH5 read errors: {BinaryPrimitives.ReadUInt32LittleEndian(d.Span[40..])}    Max acquisition: {BinaryPrimitives.ReadUInt32LittleEndian(d.Span[44..])} µs";
        Ui(() => { raw = rawValue; lastSample = Environment.TickCount64; live.Text = text; });
    }
    private void FileAction(bool export)
    {
        if (busy || connection == null) return;
        using var chooser = new FileChooserDialog(export ? "Export calibration draft" : "Import calibration draft", this,
            export ? FileChooserAction.Save : FileChooserAction.Open, "Cancel", ResponseType.Cancel,
            export ? "Export" : "Import", ResponseType.Accept);
        chooser.DoOverwriteConfirmation = true;
        if ((ResponseType)chooser.Run() != ResponseType.Accept) return;
        try
        {
            if (export) File.WriteAllText(chooser.Filename, new CalibrationFile(1, connection.Profile, Draft()).Export());
            else ShowValue(CalibrationFile.Import(File.ReadAllText(chooser.Filename), connection.Profile, connection.CanSleep).Calibration);
            status.Text = export ? "Draft exported." : "Draft imported. Review values, then Save to Controller.";
        }
        catch (Exception error) { status.Text = error.Message; }
    }
}
