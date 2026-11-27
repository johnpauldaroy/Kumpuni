using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Kumpuni;

internal enum TaskKind { Connect, Default, Print }
internal enum Repair { None, Connect, Reconnect, SetDefault, TestPage, StartSpooler, StartHostSpooler, StartLanmanServer, RestoreRpcPrivacy, SignInToHost,
    EnableSmbClient, EnableSmbServer, SecureHostSharing }

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
    SharingEvidence? Sharing = null, AdvancedSharing? Advanced = null, Observation? CachedQueue = null,
    ConnectivityEvidence? Connectivity = null);
internal sealed record Decision(string Finding, string Owner, string Details, Repair Action = Repair.None);
internal sealed record ActionResult(string Message, bool Verified = false, int? Code = null, string? JobState = null);
internal sealed record WorkerReply(Report? Report = null, Inventory? Inventory = null, ActionResult? Action = null,
    ConsoleSnapshot? Console = null, ConsoleCheck? Check = null, Catalog<JobSelection>? Jobs = null, QuickLocalEvidence? QuickLocal = null);

internal static class Diagnose
{
    internal const int SecureSmbBuild = 26100;
    internal const int PrintRpcSecurityError = 1825;
    internal static bool Elevated(Repair action) => action is Repair.StartSpooler or Repair.StartHostSpooler or Repair.StartLanmanServer
        or Repair.RestoreRpcPrivacy or Repair.EnableSmbClient or Repair.EnableSmbServer or Repair.SecureHostSharing;
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
            if (report.Connectivity is { ServerBindingOff: true })
                return new("File and Printer Sharing for Microsoft Networks is turned off on an active network adapter.", "Print host administrator",
                    "Clients reach this PC but cannot open a sharing session. Check and repair turns it back on for the active adapters.",
                    Repair.EnableSmbServer);
            if (report.Connectivity?.Session is { Error: not 0 } self)
                return new("This PC cannot open a file-sharing session to itself.", "Print host administrator",
                    $"Windows returned {CodeText(self.Error)} when listing this PC's own shares. "
                    + SecurityHint(report.Connectivity) + "Restart the PC and check again before changing other settings.");
            if (HostFirewallOff(host) || report.Connectivity is { PublicNetwork: true })
            {
                bool canRepair = report.Connectivity?.Health?.Categories is ["Private"] or ["Public"];
                return new(report.Connectivity is { PublicNetwork: true }
                        ? "This print host is on a Public network." : "Windows Firewall is turned off on this print host.",
                    "Print host administrator",
                    canRepair
                        ? "Check and repair enables the required inbound printing rules and the firewall for Private networks, then sets the active network to Private if needed. "
                            + "Only continue on a network you trust."
                        : "Automatic repair requires one active Public or Private network profile. Ask the host administrator to review the active network and firewall policy.",
                    canRepair ? Repair.SecureHostSharing : Repair.None);
            }
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
            if (host.LocalAccountModel is { Value: 1, Error: 0 })
                return new("This print host maps local network sign-ins to Guest.", "Print host administrator",
                    "Windows cannot sign a guest SMB session. Ask the host administrator to review the Network access: Sharing and security "
                    + "model for local accounts policy before creating a local printing account.");
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

        if (queue?.Code != 0 && SmbSecurityDecision(report, reported) is Decision smbSecurity)
            return smbSecurity;

        if (queue is { Code: PrintRpcSecurityError }
            || (queue is null or { Code: 3 or 5 or 1801 or 1722 or 1753 }
                && report.Connectivity is { PrintServerError: PrintRpcSecurityError, Session: { IpcError: 0 } or { Error: 0, SharePublished: true } }))
            return new("Windows could not authenticate print RPC (0x80070721).", "Client user or print host administrator",
                "Windows reported a security package error while opening the print server. Save an authorized host account as a Windows credential "
                + "for this user, then check print RPC and the selected queue again.", Repair.SignInToHost);

        if (queue is null)
            return new("The live queue check did not complete.", "Client user or IT",
                "Check host availability and retry diagnosis. No repair is available until the selected queue is verified.");

        if (queue.Code != 0)
        {
            if (report.Connectivity is ConnectivityEvidence link && SessionDecision(link, report) is Decision session)
                return session;
            bool smbOpened = report.Connectivity?.Session is { Error: 0 } or { IpcError: 0 };
            if (task == TaskKind.Connect && queue.Code == 1801
                && report.Machine is { Build: 22000, Revision: 258 or 282 or 318 })
                return new("The client matches a historical Windows 11 regression build.", "IT administrator",
                    "Confirm the host and patch history against KB5007262. Apply a current supported update through IT.");

            if (queue.Code == 1801 && smbOpened)
                return new("SMB opened, but Windows could not open the shared print queue (0x00000709).", "Client user or print host operator",
                    "Confirm the exact share name and Print permission on the host. Try adding the printer in Windows to get the queue or driver error.");

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
                5 when smbOpened => new("Windows denied access to the print queue after SMB opened.", "Print host operator",
                    "Check that the account used for this connection has Print permission on the host printer's Security tab."),
                5 => new("Windows denied basic access to the selected queue.", "Print host operator",
                    "Check the identity Windows is using and the queue's Print permission. If credentials are required, sign in to the print host through Windows.",
                    Repair.SignInToHost),
                86 or 1326 or 1244 => new("Windows did not authenticate the print connection.", "Client user or print host operator",
                    "Sign in to the host with an authorized account through Windows and verify the queue's Print permission.", Repair.SignInToHost),
                1219 => SmbCredentialConflict(),
                1327 or 1385 => SmbAccountRestricted(queue.Code),
                6 => FrameworkDecision(report),
                3 when smbOpened => new("Windows could not find a path required to open the shared printer (0x00000003).", "Client user or print host operator",
                    "SMB opened. Check the host print server result and the selected queue's ShareName, Print permission and driver."),
                3 => new("Windows could not find the printer path on the host (0x00000003).", "Client user or print host operator",
                    "The host answered but did not return this printer. Confirm the share name on the host's Sharing tab, "
                    + "and run Shared printer repair on the host with the same path to compare the reports."),
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

    internal static string CodeText(int code) => $"0x{unchecked((uint)code):X8} ({code})";

    private static Decision? SmbSecurityDecision(Report report, uint? reported)
    {
        SmbSessionCheck? session = report.Connectivity?.Session;
        // Prefer a live SMB result. A reported error can fill the gap when Windows only returns a generic path error.
        bool sessionOpened = session is { Error: 0 } or { IpcError: 0 };
        int? code = new int?[] { session?.IpcError, session?.Error, report.Queue?.Code,
            !sessionOpened && reported is uint value ? unchecked((int)value) : null }.FirstOrDefault(error =>
            error is Connectivity.GuestSigningRequired or Connectivity.GuestEncryptionRequired or 1272
                or Connectivity.InvalidSignature or Connectivity.NoSigningAlgorithm);
        return code switch
        {
            Connectivity.GuestSigningRequired => GuestBlocked("SMB signing is required and Windows rejected the guest session.", code.Value),
            Connectivity.GuestEncryptionRequired => GuestBlocked("SMB encryption is required and Windows rejected the guest session.", code.Value),
            1272 => GuestBlocked("Windows policy blocked a guest SMB connection.", code.Value),
            Connectivity.InvalidSignature or Connectivity.NoSigningAlgorithm => new(
                "Windows could not establish a signed SMB connection.", "Print host or client administrator",
                $"Windows returned {CodeText(code.Value)}. Check SMB signing compatibility on the approved print host."),
            _ => null
        };

        static Decision GuestBlocked(string finding, int code) => new(finding, "Client user or print host administrator",
            $"Windows returned {CodeText(code)}. Sign in with a password-protected account that has Print permission on the host. "
            + "Save the credential in Windows so Windows apps can use it. Kumpuni checks print RPC and the queue again after sign-in.",
            Repair.SignInToHost);
    }

    private static bool HostFirewallOff(SharingEvidence host) =>
        host.Firewall is { ActiveProfiles: not 0 } firewall && (firewall.EnabledLocally & firewall.ActiveProfiles) != firewall.ActiveProfiles;

    private static string SecurityHint(ConnectivityEvidence link) => link.ThirdPartySecurity is { Length: > 0 } products
        ? $"Security software can block file sharing: {string.Join(", ", products)}. Allow file and printer sharing or mark this network as trusted in it. "
        : "";

    // Client-side findings from the session checks, ahead of the raw queue error.
    private static Decision? SessionDecision(ConnectivityEvidence link, Report report)
    {
        if (link.ClientBindingOff)
            return new("Client for Microsoft Networks is turned off on an active network adapter.", "Client administrator",
                "Without it this PC cannot open file-sharing sessions to any host. Check and repair turns it back on.", Repair.EnableSmbClient);
        if (link.Session is { Error: 0, SharePublished: false } listed)
            return new("The host is reachable but does not publish a printer with this share name.", "Client user or print host operator",
                $"Windows listed {listed.PrinterShares} shared printer(s) on the host. Use the exact share name from the host's Sharing tab or from net view.");
        int? signInError = link.Session?.IpcError ?? link.Session?.Error;
        if (signInError == 1219)
            return SmbCredentialConflict();
        if (signInError is 1327 or 1385)
            return SmbAccountRestricted(signInError.Value);
        if (signInError is 5 or 86 or 1326 or 1244 or 1907 or 1909 or 2242)
            return new("The host refused this PC's sign-in for file sharing.", "Client user or print host operator",
                $"Windows returned {CodeText(signInError.Value)} when opening a session. Sign in to the print host with an account that has a password "
                + "and access to the printer.", Repair.SignInToHost);
        bool portOpen = report.Network?.Smb == true || link.Addresses.Any(address => address.Smb);
        if (portOpen && link.Session is { Error: 53, IpcError: null or 53 }
            && link.Health?.SmbClient is { EnableInsecureGuestLogons: true, RequireSecuritySignature: not false })
            return new("SMB could not connect to the print host.", "Client user or print host administrator",
                "Windows returned error 53. If the host offered guest access, signing or guest restrictions can block it. "
                + "Try a password-protected host account with Print permission, then check the connection again.", Repair.SignInToHost);
        if (link.Session is { Error: not 0, IpcError: not 0 } failed && portOpen)
            return new("The host answers on TCP 445 but Windows cannot open a file-sharing session.", "Print host administrator or IT",
                $"Windows returned {CodeText(failed.Error)} when opening a session"
                + (failed.IpcError is int ipc && ipc != failed.Error ? $", {CodeText(ipc)} when connecting to IPC$" : "")
                + (link.PrintServerError is int rpc and not 0 ? $", and {CodeText(rpc)} when opening the host's print server" : "") + ". "
                + SecurityHint(link)
                + "Run Shared printer repair on the print host with the same path. It checks File and Printer Sharing on its adapters, "
                + "its network type and firewall, and whether it can open a session to itself."
                + (link.SmbClientEvents.Length > 0 ? $" Recent SMB client events: {string.Join(", ", link.SmbClientEvents)}." : ""));
        return null;
    }

    private static Decision SmbCredentialConflict() => new(
        "Windows reports a conflicting connection to the same host.", "Client user or IT",
        "Windows returned error 1219. Close files and finish jobs using the host before disconnecting its existing Windows sessions. "
        + "Then sign in with one account for the host.");

    private static Decision SmbAccountRestricted(int code) => new(
        "Windows restricted this account's network sign-in.", "Client user or print host administrator",
        $"Windows returned {CodeText(code)}. Use a password-protected account authorized for network sign-in and Print permission on the host.",
        Repair.SignInToHost);

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
