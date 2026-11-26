using System.Net;
using System.Text.Json;

namespace Kumpuni;

internal enum QueueScope { Local, SharedHere, SharedConnection }
internal enum PortKind { Unknown, Usb, TcpIp, Ipp, Wsd, Local, SharedPath, Virtual, Other, Pooled }
internal enum QueueHealth { Unknown, Available, Busy, Attention }

internal sealed record Catalog<T>(T[] Items, int Error);
internal sealed record QueueDetails(string? Driver, string? Port, string? Share, uint Status, uint Jobs, uint Attributes);
internal sealed record QueueEntry(string Name, QueueScope Scope, QueueDetails? Details = null);
internal sealed record DriverEntry(string Name, string Environment, uint Model, string? Version);
internal sealed record PortEntry(string Name, string? Monitor, string? Description, uint Flags);
internal sealed record QueueProbe(int OpenError, int DetailsError, QueueDetails? Details)
{
    public Observation Observation() => new(OpenError, Details?.Status, Details?.Jobs,
        !string.IsNullOrWhiteSpace(Details?.Driver), !string.IsNullOrWhiteSpace(Details?.Port), DetailsError);
}

internal sealed record HostSpooler(uint? State, int Error, string? DefaultQueue, int DefaultError);
internal sealed record ConsoleSnapshot(DateTimeOffset CapturedAt, Machine Machine, HostSpooler? Spooler,
    Catalog<QueueEntry> Local, Catalog<QueueEntry> Shared, Catalog<DriverEntry> Drivers, Catalog<PortEntry> Ports,
    PrintPolicy? Policy)
{
    public QueueEntry[] Queues() => Local.Items.Concat(Shared.Items)
        .DistinctBy(queue => queue.Name, StringComparer.OrdinalIgnoreCase)
        .OrderBy(queue => queue.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

internal sealed record ConsoleCheck(DateTimeOffset CheckedAt, QueueProbe Probe, PrintEvent[] Events, bool EventsAvailable);
internal sealed record ConsoleTest(DateTimeOffset TestedAt, ActionResult Result, bool PageConfirmed = false);

internal static class ConsoleHealth
{
    private const uint AttentionFlags = 0xFF | 0x800 | 0x1000 | 0x20000 | 0x40000 | 0x80000
        | 0x100000 | 0x200000 | 0x400000 | 0x800000 | 0x2000000 | 0x4000000;
    private const uint ActiveFlags = 0x100 | 0x200 | 0x400 | 0x2000 | 0x4000 | 0x8000 | 0x10000;

    private static readonly (uint Flag, string Name)[] StatusNames =
    [
        (0x1, "Paused"), (0x2, "Error"), (0x4, "Pending deletion"),
        (0x8, "Paper jam"), (0x10, "Paper out"), (0x20, "Manual feed"),
        (0x40, "Paper problem"), (0x80, "Offline"), (0x100, "I/O active"),
        (0x200, "Busy"), (0x400, "Printing"), (0x800, "Output bin full"),
        (0x1000, "Unavailable"), (0x2000, "Waiting"), (0x4000, "Processing"),
        (0x8000, "Initializing"), (0x10000, "Warming up"), (0x20000, "Toner low"),
        (0x40000, "No toner"), (0x80000, "Page cannot print"), (0x100000, "User intervention"),
        (0x200000, "Out of memory"), (0x400000, "Door open"), (0x800000, "Server status unknown"),
        (0x1000000, "Power save"), (0x2000000, "Server offline"), (0x4000000, "Driver update needed")
    ];

    public static QueueHealth Health(QueueDetails? details, QueueProbe? probe = null)
    {
        if (probe is { OpenError: not 0 } or { DetailsError: not 0 } || details is null)
            return QueueHealth.Unknown;
        if ((details.Status & AttentionFlags) != 0)
            return QueueHealth.Attention;
        if ((details.Status & ActiveFlags) != 0 || details.Jobs != 0)
            return QueueHealth.Busy;
        return (details.Status & ~0x01000000u) != 0 ? QueueHealth.Unknown : QueueHealth.Available;
    }

    public static string StatusText(QueueDetails? details, QueueProbe? probe = null)
    {
        if (probe is { OpenError: not 0 } or { DetailsError: not 0 })
            return "Check failed";
        if (details is null)
            return "Status unavailable";
        if (details.Status == 0)
            return details.Jobs == 0 ? "No issue reported" : "Jobs in queue";

        string[] labels = StatusNames.Where(item => (details.Status & item.Flag) != 0).Select(item => item.Name).ToArray();
        return labels.Length == 0 ? $"Windows status 0x{details.Status:X8}" : string.Join(", ", labels);
    }

    public static string ScopeText(QueueScope scope) => scope switch
    {
        QueueScope.SharedHere => "Shared by this PC",
        QueueScope.SharedConnection => "Connected share",
        _ => "Local queue"
    };

    public static string SpoolerText(uint? state) => state switch
    {
        1 => "Stopped", 2 => "Start pending", 3 => "Stop pending", 4 => "Running",
        5 => "Continue pending", 6 => "Pause pending", 7 => "Paused", _ => "Unknown"
    };

    public static IEnumerable<string> PortNames(string? name) => (name ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public static PortKind Kind(QueueDetails? details, PortEntry[] ports)
    {
        if (details is null)
            return PortKind.Unknown;
        if (IsVirtualDriver(details.Driver))
            return PortKind.Virtual;

        string[] names = PortNames(details.Port).ToArray();
        if (names.Length > 1)
            return names.Any(name => GetPortKind(name, ports) == PortKind.Virtual) ? PortKind.Virtual : PortKind.Pooled;
        return names.Length == 0 ? PortKind.Unknown : GetPortKind(names[0], ports);
    }

    private static PortKind GetPortKind(string name, PortEntry[] ports) =>
        GetPortKind(name, ports.FirstOrDefault(port => Diagnose.Same(port.Name, name))?.Monitor);

    public static PortKind GetPortKind(string name, string? monitor)
    {
        if (name is "" || name.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return PortKind.Unknown;
        if (new[] { "FILE:", "NUL:", "PORTPROMPT:", "PROMPT:", "FAX:" }.Any(v => Diagnose.Same(v, name))
            || Starts(name, "Microsoft.Office.OneNote") || Contains(monitor, "OneNote"))
            return PortKind.Virtual;
        if (name.StartsWith(@"\\", StringComparison.Ordinal))
            return Target.Parse(name) is not null ? PortKind.SharedPath : PortKind.Virtual;
        if (Path.IsPathFullyQualified(name))
            return PortKind.Virtual;
        if (Starts(name, "USB") || Contains(monitor, "USB"))
            return PortKind.Usb;
        if (Starts(name, "WSD") || Contains(monitor, "WSD"))
            return PortKind.Wsd;
        if (Starts(name, "IPP") || Contains(monitor, "IPP")
            || (Uri.TryCreate(name, UriKind.Absolute, out Uri? uri)
                && (uri.Scheme is "ipp" or "ipps" || uri.Scheme is "http" or "https" && Contains(uri.AbsolutePath, "/ipp"))))
            return PortKind.Ipp;
        if (Starts(name, "IP_") || IPAddress.TryParse(name, out _) || Contains(monitor, "TCP/IP"))
            return PortKind.TcpIp;
        if (Starts(name, "LPT") || Starts(name, "COM") || Contains(monitor, "Local Port"))
            return PortKind.Local;
        return PortKind.Other;
    }

    public static string PortKindText(PortKind kind) => kind switch
    {
        PortKind.Usb => "USB", PortKind.TcpIp => "TCP/IP", PortKind.Ipp => "IPP", PortKind.Wsd => "WSD",
        PortKind.SharedPath => "Shared path", _ => kind.ToString()
    };

    public static bool MatchesConnection(PrinterConnection connection, QueueScope scope, QueueDetails? details, PortEntry[] ports)
    {
        if (connection == PrinterConnection.All)
            return true;
        if (connection == PrinterConnection.Shared && scope != QueueScope.Local)
            return true;
        if (scope == QueueScope.SharedConnection)
            return false;

        PortKind kind = Kind(details, ports);
        return kind == PortKind.Pooled
            ? PortNames(details?.Port).Any(name => MatchesKind(GetPortKind(name, ports)))
            : MatchesKind(kind);

        bool MatchesKind(PortKind candidate) => connection switch
        {
            PrinterConnection.Usb => candidate == PortKind.Usb,
            PrinterConnection.Network => candidate is PortKind.TcpIp or PortKind.Ipp or PortKind.Wsd,
            PrinterConnection.Shared => candidate == PortKind.SharedPath,
            _ => false
        };
    }

    public static bool MaySendTest(QueueDetails? details, PortEntry[] ports, QueueProbe? probe = null) =>
        details is not null && Health(details, probe) != QueueHealth.Attention
        && (probe is null or { OpenError: 0, DetailsError: 0 })
        && !string.IsNullOrWhiteSpace(details.Driver) && Kind(details, ports) is not (PortKind.Virtual or PortKind.Unknown);

    public static string Guidance(QueueEntry queue, QueueDetails? details, ConsoleSnapshot snapshot, QueueProbe? probe)
    {
        if (snapshot.Spooler?.State == 1)
            return "Check the local Print Spooler and device policy before testing queues.";
        if (probe?.OpenError == 5)
            return "Windows denied opening the queue. Verify Print permission and the Windows identity being used.";
        if (probe is { OpenError: not 0 })
            return queue.Scope == QueueScope.SharedConnection
                ? "Check the UNC path, the host spooler and print RPC. Use Shared printer repair for connection errors."
                : "Refresh the inventory, then verify the queue still exists and that your account can print.";
        if (probe is { DetailsError: not 0 })
            return "Windows opened the queue but did not return its properties. Verify access and try the check again.";
        if (details is null)
            return "Use Check queue to inspect this connection.";
        if (Kind(details, snapshot.Ports.Items) == PortKind.Virtual)
            return "This queue uses a virtual output destination. Verify output from its Windows application.";
        if ((details.Status & 0x80) != 0)
            return Kind(details, snapshot.Ports.Items) switch
            {
                _ when queue.Scope == QueueScope.SharedConnection => "Check queue availability on the print host and use Shared printer repair to verify the UNC path.",
                PortKind.Usb => "Check printer power and its USB cable. Use All-Round Printer Help to check for an active USB printer interface.",
                PortKind.TcpIp or PortKind.Ipp or PortKind.Wsd => "Check printer power and the configured Windows port. Use All-Round Printer Help to check a known network endpoint.",
                PortKind.SharedPath => "Verify the port's UNC path and the host queue's accessibility.",
                _ => "Check device power and connectivity, the Windows port and the queue's offline setting."
            };
        if ((details.Status & 1) != 0)
            return "Check whether the printer was paused intentionally before resuming it in Windows.";
        if (Health(details) == QueueHealth.Attention)
            return "Review the reported device status, then check the panel, paper path or consumables.";
        if (string.IsNullOrWhiteSpace(details.Driver) || string.IsNullOrWhiteSpace(details.Port))
            return "Verify the queue's driver and port assignment in Windows.";
        if (details.Jobs != 0)
            return "Review the queue in Windows if jobs remain here longer than expected.";
        return "Send a test page if you need to verify physical output.";
    }

    private static bool IsVirtualDriver(string? value) => Contains(value, "Microsoft Print to PDF")
        || Contains(value, "XPS Document Writer") || Contains(value, "OneNote") || Diagnose.Same(value, "Microsoft Shared Fax Driver")
        || Diagnose.Same(value, "Fax") || Contains(value, "Adobe PDF") || Contains(value, "PDFCreator") || Contains(value, "CutePDF");

    private static bool Starts(string value, string prefix) => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    private static bool Contains(string? value, string term) => value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
}

internal static class ConsoleReport
{
    public static string Serialize(ConsoleSnapshot snapshot, IReadOnlyDictionary<string, ConsoleCheck> checks,
        IReadOnlyDictionary<string, ConsoleTest> tests, PrinterHelpEvidence? help = null)
    {
        DriverEntry[] sortedDrivers = snapshot.Drivers.Items.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Environment, StringComparer.OrdinalIgnoreCase).ToArray();
        PortEntry[] sortedPorts = snapshot.Ports.Items.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var driverRows = sortedDrivers.Select((driver, index) => new { Driver = driver, Id = $"D{index + 1:000}" }).ToArray();
        var portRows = sortedPorts.Select((port, index) => new { Port = port, Id = $"P{index + 1:000}" }).ToArray();
        var driverIds = driverRows.ToLookup(row => row.Driver.Name, row => row.Id, StringComparer.OrdinalIgnoreCase);
        var portIds = portRows.ToLookup(row => row.Port.Name, row => row.Id, StringComparer.OrdinalIgnoreCase);

        // Emit a projection of known fields. Spooler-supplied names and free text remain in the UI.
        var document = new
        {
            SchemaVersion = 1,
            ReportType = "TechnicianConsole",
            snapshot.CapturedAt,
            ExportedAt = DateTimeOffset.UtcNow,
            snapshot.Machine,
            SpoolerState = snapshot.Spooler?.State,
            SpoolerError = snapshot.Spooler?.Error,
            DefaultError = snapshot.Spooler?.DefaultError,
            snapshot.Policy,
            PrinterHelp = help is null ? null : new
            {
                help.Usb,
                Network = help.Network is null ? null : new
                {
                    help.Network.CheckedAt, help.Network.TcpPort, Status = help.Network.Status.ToString()
                }
            },
            Errors = new { LocalQueues = snapshot.Local.Error, Connections = snapshot.Shared.Error,
                Drivers = snapshot.Drivers.Error, Ports = snapshot.Ports.Error },
            Drivers = driverRows.Select(row => new
            {
                row.Id, Environment = SafeEnvironment(row.Driver.Environment), row.Driver.Model,
                Version = SafeVersion(row.Driver.Version)
            }),
            Ports = portRows.Select(row => new
            {
                row.Id, Kind = ConsoleHealth.GetPortKind(row.Port.Name, row.Port.Monitor).ToString(), row.Port.Flags
            }),
            Queues = snapshot.Queues().Select((queue, index) =>
            {
                checks.TryGetValue(queue.Name, out ConsoleCheck? check);
                tests.TryGetValue(queue.Name, out ConsoleTest? test);
                QueueDetails? details = check?.Probe.Details ?? queue.Details;
                return new
                {
                    Id = $"Q{index + 1:000}",
                    Scope = queue.Scope.ToString(),
                    IsDefault = snapshot.Spooler is { DefaultError: 0 } spooler
                        ? Diagnose.Same(spooler.DefaultQueue, queue.Name) : (bool?)null,
                    Health = ConsoleHealth.Health(details, check?.Probe).ToString(),
                    Status = details?.Status,
                    Jobs = details?.Jobs,
                    PortKind = ConsoleHealth.Kind(details, sortedPorts).ToString(),
                    Drivers = details?.Driver is string driver ? driverIds[driver].ToArray() : [],
                    Ports = ConsoleHealth.PortNames(details?.Port).SelectMany(name => portIds[name]).Distinct().ToArray(),
                    Check = check is null ? null : new
                    {
                        check.CheckedAt, check.Probe.OpenError, check.Probe.DetailsError,
                        check.EventsAvailable, Events = check.Events.Select(item => new { item.Id, item.Time })
                    },
                    Test = test is null ? null : new
                    {
                        test.TestedAt, test.Result.Verified, test.Result.Code, test.PageConfirmed,
                        JobState = SafeJobState(test.Result.JobState)
                    }
                };
            })
        };
        return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string SafeEnvironment(string environment) => environment.ToUpperInvariant() switch
    {
        "WINDOWS X64" => "x64", "WINDOWS NT X86" or "WINDOWS X86" => "x86",
        "WINDOWS ARM64" => "ARM64", "WINDOWS ARM" => "ARM", "WINDOWS IA64" => "IA64", _ => "Other"
    };

    private static string? SafeVersion(string? value) => Version.TryParse(value, out Version? version) ? version.ToString() : null;
    private static string? SafeJobState(string? value) => value is "Submitted" or "Visible in Windows queue"
        or "Reported printed by Windows" or "Job needs attention" or "Submission uncertain" ? value : null;
}
