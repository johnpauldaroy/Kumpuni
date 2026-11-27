using System.ComponentModel;
using System.Text.Json;

namespace Kumpuni;

internal sealed record PrinterProfile(string Queue, bool Shared, bool MakeDefault)
{
    public bool IsValid() => !string.IsNullOrWhiteSpace(Queue) && Queue.Length <= 1024 && !Queue.Any(char.IsControl)
        && (Shared ? Target.Parse(Queue)?.Path == Queue : !Queue.StartsWith(@"\\", StringComparison.Ordinal));
}

internal static class PrinterProfileStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kumpuni", "printer-setup.json");

    public static PrinterProfile? Load() => Load(SettingsPath);
    public static void Save(PrinterProfile profile) => Save(SettingsPath, profile);

    internal static PrinterProfile? Load(string path)
    {
        if (!File.Exists(path))
            return null;
        using var file = File.OpenRead(path);
        if (file.Length > 16 * 1024)
            throw new InvalidDataException("The saved printer setup is too large.");
        PrinterProfile? setup = JsonSerializer.Deserialize<PrinterProfile>(file);
        return setup?.IsValid() == true ? setup : throw new InvalidDataException("The saved printer setup is invalid.");
    }

    internal static void Save(string path, PrinterProfile profile)
    {
        if (!profile.IsValid())
            throw new InvalidDataException("The printer setup is invalid.");
        string folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        string staging = Path.Combine(folder, $".printer-setup-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, profile);
            File.Move(staging, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(staging))
                File.Delete(staging);
        }
    }
}

internal sealed record QuickLocalEvidence(bool Workstation, ServiceReading Spooler, QueueProbe? Probe,
    PortKind PortKind, bool? IsDefault, int DefaultError);
internal enum QuickPhase { Diagnose, Repair, Verify }
internal sealed record QuickProgress(QuickPhase Phase, string Message);
internal sealed record QuickStep(string Action, bool Verified, int? Code, string Message);
internal sealed record QuickOutcome(string Title, string Detail, bool Ready, QuickStep[] Steps,
    DateTimeOffset RanAt, Report? SharedReport = null, Decision? Decision = null, SharedRepairRun? SharedRun = null,
    QuickLocalEvidence? LocalBefore = null, QuickLocalEvidence? LocalAfter = null);

internal static class QuickRepair
{
    public static async Task<QuickOutcome> RunAsync(PrinterProfile profile, Action<QuickProgress> progress)
    {
        if (!profile.IsValid())
            return Incomplete("Set up a valid printer before running the check.");
        if (!profile.Shared)
        {
            return await RunLocalAsync(profile, progress,
                () => ReadWorkerAsync("--quick-local", profile.Queue, reply => reply.QuickLocal),
                action => AttemptAsync(() => action == Repair.StartSpooler
                    ? ApplyElevatedAsync(new("localhost", "Printer"), action) : ApplyWorkerAsync("--quick-default", profile.Queue, action)));
        }

        Target target = Target.Parse(profile.Queue)!;
        return await RunSharedAsync(profile, target, Read, Apply, progress);

        Task<Report?> Read() => ReadWorkerAsync("--quick-shared", target.Path, reply => reply.Report);

        Task<ActionResult> Apply(Repair action)
        {
            if (action == Repair.SignInToHost)
                return AttemptAsync(() => Task.FromResult(HostSignIn.SignIn(target, Form.ActiveForm?.Handle ?? IntPtr.Zero)));
            return AttemptAsync(() => Diagnose.Elevated(action)
                ? ApplyElevatedAsync(target, action) : ApplyWorkerAsync("--repair", target.Path, action));
        }
    }

    internal static async Task<QuickOutcome> RunSharedAsync(PrinterProfile profile, Target target,
        Func<Task<Report?>> read, Func<Repair, Task<ActionResult>> apply, Action<QuickProgress>? progress = null)
    {
        progress?.Invoke(new(QuickPhase.Diagnose, "Reading the shared queue and Windows settings."));
        Report? first = await read();
        if (first is null)
            return Incomplete("Windows did not return a result. Confirm that the print host is online and try again.");

        TaskKind task = profile.MakeDefault ? TaskKind.Default : TaskKind.Connect;
        SharedRepairRun run = await SharedRepair.ExecuteAsync(task, target, first, null, false, Apply, Verify, recoverStaleCache: true);
        Report last = run.Final ?? first;
        Decision finding = Diagnose.Choose(task, target, last, null);
        bool clean = run.Final is not null && run.Steps.All(step => step.Result.Verified && step.StateMatches != false);
        bool ready = clean && IsSharedReady(profile, target, last);
        string title = ready ? "Your printer is ready" : finding.Finding;
        string detail = ready ? last.Sharing?.LocalHost == true
            ? "Windows verified that this PC publishes the selected printer. Print from the affected computer to verify output."
            : "Windows verified the connection and queue. Print a document to confirm physical output."
            : run.Final is null ? run.Summary : finding.Details;

        if (clean && last is { Sharing.LocalHost: not true, Queue: { QueueStatus: uint status } }
            && (status & Diagnose.QueueNeedsAttention) != 0)
        {
            title = "The printer needs attention";
            detail = "Check the print host and the printer's display for an offline, paused, paper, or device error.";
        }
        else if (clean && HasStaleConnection(target, last))
        {
            title = "Windows still shows an offline connection";
            detail = "Check the printer connection and offline setting in Windows. The live queue responds.";
        }
        if (run.Steps.LastOrDefault()?.Result is { Verified: false } failed)
        {
            title = "Windows could not verify the repair";
            detail = failed.Message;
        }
        return new(title, detail, ready,
            run.Steps.Select(step => new QuickStep(MainForm.ActionLabel(step.Action),
                step.Result.Verified && step.StateMatches != false, step.Result.Code,
                step.StateMatches == false ? "The follow-up check found a different state." : step.Result.Message)).ToArray(),
            DateTimeOffset.UtcNow, last, finding, run);

        Task<ActionResult> Apply(Repair action)
        {
            progress?.Invoke(new(QuickPhase.Repair, MainForm.ActionLabel(action)));
            return apply(action);
        }

        Task<Report?> Verify()
        {
            progress?.Invoke(new(QuickPhase.Verify, "Reading the queue again after the repair."));
            return read();
        }
    }

    internal static bool HasStaleConnection(Target target, Report report) =>
        report.Sharing?.LocalHost != true
        && report.Inventory is { InventoryError: 0, DefaultError: 0 } inventory
        && inventory.Connections.Any(name => Diagnose.Same(name, target.Path))
        && report.Queue is { Code: 0, DetailsError: 0, Jobs: 0, DriverKnown: true, PortKnown: true, QueueStatus: 0 }
        && report.CachedQueue is { Code: 0, DetailsError: 0, Jobs: 0, QueueStatus: uint cached }
        && (cached & 0x80) != 0;

    private static bool IsSharedReady(PrinterProfile profile, Target target, Report report)
    {
        if (report.Sharing is { LocalHost: true } host)
            return host is { SharedQueuePresent: true, Spooler.State: 4, Privacy.Value: not 0 }
                && !SharingSettings.RemoteSpoolerBlocked(host.RemoteSpooler)
                && (!SharingSettings.NamedPipesConfigured(host.RpcProtocols) || host.Server.State == 4)
                && host.FirewallService.State != 1 && report.Advanced?.Spool.FreeBytes != 0
                && report.Advanced?.Queue?.Jobs.Blocked is not > 0
                && report.Advanced?.Queue?.Permissions.Dacl is not "Empty";
        return report.Queue is { Code: 0, DetailsError: 0, DriverKnown: true, PortKnown: true, QueueStatus: uint status }
            && (status & Diagnose.QueueNeedsAttention) == 0
            && report.Inventory is { InventoryError: 0 } inventory
            && inventory.Connections.Any(value => Diagnose.Same(value, target.Path))
            && !HasStaleConnection(target, report)
            && (!profile.MakeDefault || inventory.DefaultError == 0 && Diagnose.Same(inventory.Default, target.Path));
    }

    internal static async Task<QuickOutcome> RunLocalAsync(PrinterProfile profile, Action<QuickProgress> progress,
        Func<Task<QuickLocalEvidence?>> read, Func<Repair, Task<ActionResult>> apply)
    {
        progress(new(QuickPhase.Diagnose, "Reading the installed queue and Print Spooler status."));
        QuickLocalEvidence? first = await read();
        if (first is null)
            return Incomplete("Windows did not return the selected printer's status. Check that the printer is installed and try again.");

        QuickLocalEvidence? last = first;
        var actions = new List<QuickStep>();
        if (first is { Workstation: true, Spooler: { State: 1, Start: 2, Error: 0 } })
        {
            progress(new(QuickPhase.Repair, "Starting Print Spooler."));
            ActionResult action = await apply(Repair.StartSpooler);
            progress(new(QuickPhase.Verify, "Reading the queue again after the repair."));
            last = await read();
            actions.Add(new("Start Print Spooler", action.Verified && last?.Spooler.State == 4, action.Code, action.Message));
        }

        if (actions.All(action => action.Verified) && LocalReady(last) && profile.MakeDefault && last?.IsDefault == false)
        {
            progress(new(QuickPhase.Repair, "Setting the default printer."));
            ActionResult action = await apply(Repair.SetDefault);
            progress(new(QuickPhase.Verify, "Reading the queue again after the repair."));
            last = await read();
            actions.Add(new("Set default printer", action.Verified && last?.IsDefault == true, action.Code, action.Message));
        }

        bool ready = LocalReady(last) && (!profile.MakeDefault || last?.IsDefault == true)
            && actions.All(action => action.Verified);
        (string title, string detail) = LocalFinding(last, ready, actions.LastOrDefault());
        return new(title, detail, ready, actions.ToArray(), DateTimeOffset.UtcNow, LocalBefore: first, LocalAfter: last);
    }

    private static bool LocalReady(QuickLocalEvidence? state) => state is { Spooler.State: 4,
        Probe: { OpenError: 0, DetailsError: 0, Details: QueueDetails details } }
        && !string.IsNullOrWhiteSpace(details.Driver) && !string.IsNullOrWhiteSpace(details.Port)
        && ConsoleHealth.Health(details) is QueueHealth.Available or QueueHealth.Busy;

    private static (string Title, string Detail) LocalFinding(QuickLocalEvidence? last, bool ready, QuickStep? step)
    {
        if (ready)
            return ("Your printer is ready", "Windows verified the selected queue. Print a document to confirm physical output.");
        if (step is { Verified: false })
            return ("Windows could not verify the repair", step.Message);
        if (last is null)
            return ("The check could not finish", "Windows did not return the follow-up result.");
        if (last.Spooler.State != 4)
            return ("The Print Spooler needs attention", "Ask your Windows administrator to verify the service status and startup policy.");
        if (last.Probe?.OpenError == 1801)
            return ("Windows could not find this printer", "Install the printer in Windows or change your saved printer.");
        if (last.Probe is { OpenError: not 0 } or { DetailsError: not 0 })
            return ("Windows could not inspect this printer", "Check the queue in Printer settings and verify your Print permission.");
        if (last.Probe?.Details is QueueDetails details && ConsoleHealth.Health(details) == QueueHealth.Attention)
        {
            string hint = last.PortKind switch
            {
                PortKind.Usb => "Check printer power, the USB data cable and the printer display.",
                PortKind.TcpIp or PortKind.Wsd or PortKind.Ipp => "Check printer power, network connectivity and the printer display.",
                _ => "Check the printer display and the queue status in Windows."
            };
            return (ConsoleHealth.StatusText(details), hint);
        }
        if (last.DefaultError != 0)
            return ("Windows could not verify the default printer", "Check Let Windows manage my default printer in Windows Settings.");
        return ("This printer needs attention", "Check that Windows has a suitable driver and port for the selected queue.");
    }

    public static string Serialize(PrinterProfile profile, QuickOutcome outcome)
    {
        if (profile.Shared && Target.Parse(profile.Queue) is Target target && outcome.SharedReport is Report report
            && outcome.Decision is Decision decision)
        {
            return SharedRepair.SerializeReport(target, profile.MakeDefault ? TaskKind.Default : TaskKind.Connect, null,
                report, decision, outcome.SharedRun?.Steps.LastOrDefault()?.Result, false, outcome.SharedRun);
        }

        return JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, ReportType = "QuickPrinterCheck", outcome.RanAt, outcome.Ready, outcome.Title, outcome.Detail,
            Actions = outcome.Steps, Before = RedactLocal(outcome.LocalBefore), After = RedactLocal(outcome.LocalAfter)
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static object? RedactLocal(QuickLocalEvidence? state) => state is null ? null : new
    {
        state.Spooler, PortKind = state.PortKind.ToString(), state.IsDefault, state.DefaultError,
        state.Probe?.OpenError, state.Probe?.DetailsError, state.Probe?.Details?.Status, state.Probe?.Details?.Jobs
    };

    private static QuickOutcome Incomplete(string reason) =>
        new("The check could not finish", reason, false, [], DateTimeOffset.UtcNow);

    private static async Task<TResult?> ReadWorkerAsync<TResult>(string command, string argument, Func<WorkerReply, TResult?> select)
        where TResult : class
    {
        try { return await Worker.RunAsync(command, argument) is WorkerReply reply ? select(reply) : null; }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException or JsonException) { return null; }
    }

    private static async Task<ActionResult> AttemptAsync(Func<Task<ActionResult>> apply)
    {
        try { return await apply(); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException or JsonException)
        {
            return new("Windows did not confirm the requested change. Review the report before retrying.",
                Code: (error as Win32Exception)?.NativeErrorCode);
        }
    }

    private static Task<ActionResult> ApplyElevatedAsync(Target target, Repair action) => Worker.RunElevatedRepairAsync(target, action);

    private static async Task<ActionResult> ApplyWorkerAsync(string command, string queue, Repair action) =>
        (await Worker.RunAsync(command, queue, action))?.Action
        ?? new("Windows did not confirm the requested change. Review the report before retrying.");
}
