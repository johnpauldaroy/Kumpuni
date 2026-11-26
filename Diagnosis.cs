using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Kumpuni;

internal enum TaskKind { Connect, Default, Print }
internal enum Repair { None, Connect, Reconnect, SetDefault, TestPage, StartSpooler, StartHostSpooler, StartLanmanServer, RestoreRpcPrivacy, SignInToHost }

internal sealed record Target(string Host, string Share)
{
    public string Path => $@"\\{Host}\{Share}";

    public static Target? Parse(string input)
    {
        string path = input.Trim();
        if (!path.StartsWith(@"\\", StringComparison.Ordinal))
            return null;

        string[] parts = path[2..].Split('\\');
        if (parts.Length != 2)
            return null;

        string host = parts[0];
        string share = parts[1];
        if (host.Length is 0 or > 253 || host.StartsWith('.') || host.Contains("..", StringComparison.Ordinal)
            || !host.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_')
            || !host.Any(char.IsLetterOrDigit)
            || share.Length is 0 or > 256 || share is "." or ".." || share.Trim() != share
            || share.Any(c => char.IsControl(c) || c is '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|'))
            return null;

        return new Target(host, share);
    }
}

internal sealed record Machine(string Edition, int Build, int? Revision, string Architecture, bool Workstation)
{
    public static Machine Read()
    {
        using var version = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        using var product = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions");
        return new Machine(
            version?.GetValue("EditionID") as string ?? "Unknown",
            Environment.OSVersion.Version.Build,
            version?.GetValue("UBR") as int?,
            RuntimeInformation.OSArchitecture.ToString(),
            string.Equals(product?.GetValue("ProductType") as string, "WinNT", StringComparison.OrdinalIgnoreCase));
    }

    public string Version => Revision is int revision ? $"{Build}.{revision}" : Build.ToString(CultureInfo.InvariantCulture);
}

internal sealed record Observation(int Code, uint? QueueStatus = null, uint? Jobs = null,
    bool DriverKnown = false, bool PortKnown = false, int DetailsError = 0);

internal sealed record Inventory(string[] Connections, string? Default, int DefaultError,
    int InventoryError, uint? SpoolerState, int SpoolerError, bool AutomaticStart);

internal sealed record NetworkEvidence(bool? NameResolved, bool? RpcMapper, bool? Smb);
internal sealed record PrintEvent(int Id, DateTime? Time, bool DriverRelated);
internal sealed record PrintPolicy(bool? ProtectedPrintRequired, bool? DriverInstallationAdminOnly, string? RpcTransport);
internal sealed record Report(Machine Machine, Inventory? Inventory, NetworkEvidence? Network,
    Observation? Queue, PrintEvent[] Events, bool EventsAvailable, string? UnavailableReason, PrintPolicy? Policy = null,
    SharingEvidence? Sharing = null, AdvancedSharing? Advanced = null, Observation? CachedQueue = null);
internal sealed record Decision(string Finding, string Owner, string Details, Repair Action = Repair.None);
internal sealed record ActionResult(string Message, bool Verified = false, int? Code = null, string? JobState = null);
internal sealed record WorkerReply(Report? Report = null, Inventory? Inventory = null, ActionResult? Action = null,
    ConsoleSnapshot? Console = null, ConsoleCheck? Check = null, Catalog<JobSelection>? Jobs = null, QuickLocalEvidence? QuickLocal = null);

internal static class Diagnose
{
    internal const int SecureSmbBuild = 26100;
    internal const uint QueueNeedsAttention = 0x1 | 0x2 | 0x8 | 0x10 | 0x40 | 0x80 | 0x1000 | 0x100000;

    public static Decision Choose(TaskKind task, Target target, Report report, uint? reported)
    {
        Inventory? inventory = report.Inventory;
        Observation? queue = report.Queue;
        bool connected = inventory?.Connections.Any(p => Same(p, target.Path)) == true;
        bool driverEvent = report.Events.Any(e => e.DriverRelated);

        if (report.Sharing is { LocalHost: true } host)
        {
            if (SharingSettings.RemoteSpoolerBlocked(host.RemoteSpooler))
                return new("The host policy blocks incoming print connections.", "Print host administrator",
                    "Review the Allow Print Spooler to accept client connections policy for the intended print host.");
            if (host.Spooler.State == 1)
                return new("The print host Spooler is stopped.", "Print host administrator",
                    "Use Check and repair to start it if Windows configures the service for automatic startup.");
            if (host.SharedQueuePresent == false)
                return new("The host does not publish this printer share.", "Print host administrator",
                    "Confirm the share name on the queue's Sharing tab.");
            if (host.SharedQueuePresent is null)
                return new("Windows could not verify the local printer share.", "Print host administrator",
                    "Check the host Spooler and queue inventory.");
            if (host.Server.State == 1 && SharingSettings.NamedPipesConfigured(host.RpcProtocols))
                return new("The host Server service is stopped with print RPC over named pipes configured.", "Print host administrator",
                    "Use Check and repair if the Server service is configured for automatic startup.");
            if (host.FirewallService.State == 1)
                return new("The host Windows Defender Firewall service is stopped.", "Print host administrator",
                    "Verify the effective firewall policy and File and Printer Sharing rules for the affected client.");
            if (host.Privacy is { Value: 0, Error: 0 })
                return new("The host has an explicit RPC privacy override.", "Print host administrator",
                    "Confirm the host and clients are updated. Select Restore RPC privacy to enforce it and schedule a restart of the Spooler or host.");
            if (report.Advanced?.Spool.FreeBytes == 0)
                return new("The spool volume has no free space.", "Print host administrator",
                    "Free space through your storage maintenance process, then recheck the queue.");
            if (report.Advanced?.Queue?.Permissions is { Error: 0, Dacl: "Empty" })
                return new("The queue has an empty discretionary access list.", "Print host administrator",
                    "Grant Print only to approved groups through the queue's Security tab, then test from the affected Windows session.");
            if (report.Advanced?.Queue?.Jobs is { Error: 0, Blocked: > 0 })
                return new("Windows reports blocked jobs in the selected queue.", "Print host administrator",
                    "Inspect the printer and selected jobs in Windows before retrying or clearing a job.");
            return new("The selected share is published on this host.", "Print host administrator",
                "Compare this report with Check and repair on the affected client to verify its access, drivers and network path.");
        }

        if (inventory?.SpoolerState == 1 && inventory.AutomaticStart && report.Machine.Workstation)
            return new("The local Print Spooler is stopped.", "Client administrator",
                "Start the service if it is expected to run on this PC, then diagnose again.", Repair.StartSpooler);

        if (inventory?.SpoolerState == 1)
            return new("The local Print Spooler is stopped.", "Client or host administrator",
                "Check the service configuration and device policy. The app only starts an automatically configured spooler on a Windows workstation.");

        if (report.Network?.NameResolved == false && (queue is null or { Code: 53 or 67 or 1722 or 1753 }))
            return new("The print host name did not resolve.", "Client user or network administrator",
                "Confirm the Windows host name and network connection. Prefer the approved DNS name.");

        if (queue is null)
            return new("The live queue check did not complete.", "Client user or IT",
                "Check host availability and retry diagnosis. No repair is available until the selected queue is verified.");

        if (queue.Code != 0)
        {
            if (task == TaskKind.Connect && queue.Code == 1801
                && report.Machine is { Build: 22000, Revision: 258 or 282 or 318 })
                return new("The client matches a historical Windows 11 regression build.", "IT administrator",
                    "Confirm the host and patch history against KB5007262. Apply a current supported update through IT.");

            if (queue.Code == 1801 && report.Machine.Build >= SecureSmbBuild && report.Network is { NameResolved: true, Smb: true })
                return new("Windows 11 24H2 or later could not open the shared queue (0x00000709).", "Client user or print host operator",
                    "The host name resolved and TCP 445 (SMB) responded. This Windows version blocks guest and unsigned SMB access by default, "
                    + "which makes a correct share name fail with 0x00000709. Sign in to the print host with an account that has access to it; "
                    + "Kumpuni then checks the queue again. If the share was renamed, diagnose the confirmed new path.", Repair.SignInToHost);

            return queue.Code switch
            {
                1801 or 67 or 3012 => new("Windows could not open the selected queue by name.", "Client user or print host operator",
                    "Confirm the exact share name and use the approved Windows computer name. If the share was renamed, diagnose the confirmed new path."),
                53 => new("Windows could not reach the network path.", "Client user or network administrator",
                    "Verify the host name, route and authenticated SMB access where the connection uses SMB."),
                5 => new("Windows denied basic access to the selected queue.", "Print host operator",
                    "Check the identity Windows is using and the queue's Print permission. If credentials are required, sign in to the print host through Windows.",
                    Repair.SignInToHost),
                86 or 1326 or 1244 => new("Windows did not authenticate the print connection.", "Client user or print host operator",
                    "Sign in to the host with an authorized account through Windows and verify the queue's Print permission.", Repair.SignInToHost),
                1219 => new("Windows reports a conflicting connection to the same host.", "Client user or IT",
                    "Review existing Windows connections and use a consistent host identity. Other shares can depend on those sessions."),
                1272 => new("Windows policy blocked a guest SMB connection.", "Print host administrator",
                    "Sign in to the print host with an account that has access to it, and verify SMB signing compatibility.", Repair.SignInToHost),
                6 => FrameworkDecision(report),
                1722 or 1753 => new("The live print RPC probe failed.", "Print host or network administrator",
                    report.Network?.RpcMapper == true
                        ? "TCP 135 responded. Check the host spooler and the actual print RPC endpoint or configured port."
                        : "Check the host spooler, RPC listener and firewall for this client."),
                124 or 1764 => new("Windows reported a print RPC compatibility error.", "Print host operator or IT",
                    "Compare client and host Windows builds and update history. These codes were also observed in older print-server update regressions."),
                1797 or 3013 or 3014 or 3016 or 3019 or 3021 => DriverDecision(report.Policy),
                3022 => new("Windows reports that the selected printer cannot be shared.", "Print host operator",
                    "Review the queue and driver on the host and use a shareable Windows printer queue."),
                283 => new("The live print connection failed with 0x0000011B.", "Print host operator or IT",
                    "Compare the client and host builds, confirm both spoolers and check RPC transport and authentication policy."),
                _ => new("Windows could not open the selected queue.", "Print host operator or IT",
                    "Use the raw error and host evidence to isolate the problem before changing settings.")
            };
        }

        if (queue.DetailsError != 0)
            return new("Windows opened the queue but could not read its properties.", "Print host operator or IT",
                "Check the queue permission and host spooler before applying a repair.");

        if (inventory is null || inventory.InventoryError != 0)
            return new("The live queue is reachable. The local printer inventory is unavailable.", "Client user or IT",
                "Check the local spooler and retry. The app needs the current user's inventory before making changes.");

        if (!connected)
        {
            if (driverEvent || reported is 1797 or 3013 or 3014 or 3016 or 3019 or 3021)
                return DriverDecision(report.Policy);
            if (reported == 6)
                return FrameworkDecision(report);

            return new("The queue is reachable and has no connection for this user.", "Client user",
                "Connect the selected queue. Windows may require an administrator if a driver needs to be installed.", Repair.Connect);
        }

        if (task == TaskKind.Default)
        {
            if (inventory.DefaultError != 0)
                return new("Windows could not read the current default printer.", "Client user or IT",
                    "Check Printers & scanners in Settings before changing the default.");
            if (Same(inventory.Default, target.Path))
                return new("The selected queue is already the default.", "Client user",
                    "If the default changes again, check the Let Windows manage my default printer setting.");
            if (!queue.DriverKnown || !queue.PortKnown)
                return new("Windows did not return a valid driver and port for the queue.", "Print host operator",
                    "Verify the queue configuration on the print host before setting it as default.");
            return new("The queue is connected and can be selected as default.", "Client user",
                "Set it as the default for the current Windows user and read back the result.", Repair.SetDefault);
        }

        if (task == TaskKind.Print)
        {
            if (queue.QueueStatus is uint status && (status & QueueNeedsAttention) != 0)
                return new("The host queue reports a printer problem.", "Print host operator",
                    "Check whether the queue is paused, the device is online, and the host can print locally.");
            return new("The queue is connected and reachable.", "Client user",
                "Submit one test page and check the physical printer.", Repair.TestPage);
        }

        return new("The selected queue is already connected and reachable.", "Client user",
            "If the same connection still fails in Windows, you can reconnect it. Kumpuni checks for queued jobs before removing it.",
            queue.Jobs == 0 && inventory.DefaultError == 0 ? Repair.Reconnect : Repair.None);
    }

    public static uint? ParseReported(string text)
    {
        text = text.Trim();
        uint code;
        if (text.Equals("709", StringComparison.Ordinal))
            return 1801;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code))
                return null;
        }
        else if (!uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out code))
        {
            return null;
        }

        return (code & 0xFFFF0000u) == 0x80070000u ? code & 0xFFFFu : code;
    }

    public static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static Decision FrameworkDecision(Report report) => new("Windows returned 0x00000006 for this shared queue.", "IT administrator",
        (report.Advanced?.Driver?.DriverModel == 3 ? "The queue uses a Type 3 driver. " : "")
        + "Since the September 2026 Windows updates, this error has been reported when the shared Type 3 print framework files "
        + "(UNIDRV.DLL, UNIDRVUI.DLL, UNIRES.DLL, PSCRIPT5.DLL, PS5UI.DLL) differ between the client and the print host. "
        + "Export a Shared printer repair report on both PCs and use Compare reports to check the framework file versions. "
        + "Bring both PCs to the same update level. Do not copy system files between PCs.");

    private static Decision DriverDecision(PrintPolicy? policy) => new("The evidence points to driver installation or compatibility.", "Client administrator or IT",
        policy?.ProtectedPrintRequired == true
            ? "Policy requires Windows protected print. Ask IT for a compatible IPP-backed shared queue that does not need a third-party print driver."
            : "Verify the approved package, client architecture and Point and Print or Windows protected print mode policy. Stage a compatible driver or use a compatible shared queue, then retry as the original user.");
}
