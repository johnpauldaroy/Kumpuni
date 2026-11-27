using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Kumpuni;

internal static class Checks
{
    public static async Task<int> CheckPrinterHelpAsync()
    {
        var (present, usbError) = PrinterHelp.ReadUsbPresence();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        PrinterEndpointStatus reachable = await Worker.ProbePrinterEndpointAsync("localhost", port);
        listener.Stop();

        using var closedPort = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        closedPort.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int unreachablePort = ((IPEndPoint)closedPort.LocalEndPoint!).Port;
        PrinterEndpointStatus unreachable = await Worker.ProbePrinterEndpointAsync("127.0.0.1", unreachablePort);

        return Program.Write(new { UsbPresent = present, UsbError = usbError,
            ListeningPort = reachable.ToString(), ClosedPort = unreachable.ToString() },
            usbError == 0 && present is not null && reachable == PrinterEndpointStatus.Reachable
                && unreachable == PrinterEndpointStatus.Unreachable ? 0 : 1);
    }

    public static int CheckSharing()
    {
        var missing = new Target("localhost", "KumpuniMissingShare-24D6A160");
        Report? result = Worker.RunAsync("--diagnose", missing.Path).GetAwaiter().GetResult()?.Report;
        SharingEvidence? sharing = result?.Sharing;
        int guard = NativePrinting.ApplyHostRepair(missing, Repair.RestoreRpcPrivacy);
        int remoteGuard = NativePrinting.ApplyHostRepair(new Target("fixture.invalid", "Printer"), Repair.StartHostSpooler);
        bool success = sharing is { LocalHost: true, SharedQueuePresent: false, SharedQueueError: 0, Spooler.Error: 0, Server.Error: 0 }
            && (guard == 1801 || (guard == 5 && sharing.RemoteSpooler is not { Value: null or 1, Error: 0 }))
            && remoteGuard == 5;
        return Program.Write(new
        {
            AuditReturned = sharing is not null, sharing?.LocalHost, sharing?.SharedQueuePresent,
            Spooler = sharing?.Spooler, LanmanServer = sharing?.Server, FirewallProfiles = sharing?.Firewall,
            MissingShareMutationBlocked = guard is 5 or 1801, RemoteMutationBlocked = remoteGuard == 5
        }, success ? 0 : 1);
    }

    public static int CheckSharingDetails()
    {
        var target = new Target("localhost", "KumpuniMissingShare-24D6A160");
        Report? report = Worker.RunAsync("--diagnose", target.Path).GetAwaiter().GetResult()?.Report;
        AdvancedSharing? advanced = report?.Advanced;
        QueueEntry? local = NativePrinting.LocalQueues().Items.FirstOrDefault();
        QueueAdministration? queue = local is null ? null : NativePrinting.ReadQueueAdministration(local.Name, 128);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        int inboxInf = DriverDeployment.ValidateInf(Path.Combine(windows, "INF", "ntprint.inf"));
        int invalidInf = DriverDeployment.ValidateInf(@"\\localhost\fixture\driver.inf");
        bool success = advanced is { FirewallReadError: 0, FirewallRules: not null }
            && (advanced.Spool.FreeBytes is not null || advanced.Spool.VolumeError != 0)
            && (local is null || queue is { Jobs.Error: 0, Permissions.Error: 0 or 5 })
            && inboxInf == 0 && invalidInf == 123
            && report?.Connectivity is { HealthError: 0, Health.Adapters.Length: > 0, Session: not null, PrintServerError: not null };
        return Program.Write(new
        {
            report?.Connectivity,
            AdvancedAuditReturned = advanced is not null, advanced?.FirewallReadError, advanced?.FirewallRules,
            advanced?.Spool, advanced?.DiscoveryProvider, advanced?.DiscoveryPublication,
            LocalQueueRead = queue is not null, queue?.Permissions, queue?.Jobs,
            PrinterInfValidated = inboxInf == 0, PrinterInfValidationCode = inboxInf, UncInfBlocked = invalidInf == 123
        }, success ? 0 : 1);
    }

    public static async Task<int> CheckTechnicianActionsAsync()
    {
        FirewallCheck firewall = await ClientFirewall.CheckAsync(IPAddress.Loopback, 0, 445);
        QueueEntry? queue = NativePrinting.LocalQueues().Items.FirstOrDefault();
        Catalog<JobSelection>? jobs = queue is null ? null : (await Worker.RunAsync("--technician-jobs", queue.Name))?.Jobs;
        bool exports = NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, "winspool.drv"), out IntPtr library);
        if (exports)
        {
            try
            {
                exports = NativeLibrary.TryGetExport(library, "UploadPrinterDriverPackageW", out _)
                    && NativeLibrary.TryGetExport(library, "InstallPrinterDriverFromPackageW", out _)
                    && NativeLibrary.TryGetExport(library, "SetPrinterW", out _)
                    && NativeLibrary.TryGetExport(library, "SetJobW", out _);
            }
            finally { NativeLibrary.Free(library); }
        }
        bool invalidQueue = NativePrinting.QueueJobsForTechnician(@"\\localhost\KumpuniMissingQueue").Error == 1801;
        bool firewallRead = firewall.Store is "ActiveStore" or "LocalRules";
        bool jobsRead = queue is null || jobs is { Error: 0 };
        return Program.Write(new { FirewallStore = firewall.Store, FirewallPolicyError = firewall.PolicyError,
            Candidates = firewall.Matches.Length, LocalJobWorker = jobsRead, InvalidQueueBlocked = invalidQueue, SpoolerExports = exports },
            firewallRead && jobsRead && invalidQueue && exports ? 0 : 1);
    }

    public static int CheckQuickRepair()
    {
        QueueEntry? queue = NativePrinting.LocalQueues().Items.FirstOrDefault();
        QuickLocalEvidence? evidence = queue is null ? null
            : Worker.RunAsync("--quick-local", queue.Name).GetAwaiter().GetResult()?.QuickLocal;
        QuickLocalEvidence? missing = Worker.RunAsync("--quick-local", "KumpuniMissingQueue-24D6A160")
            .GetAwaiter().GetResult()?.QuickLocal;
        bool existingRead = queue is null || evidence is { Probe: { OpenError: 0, DetailsError: 0 } };
        bool missingRead = missing?.Probe?.OpenError == 1801;
        return Program.Write(new { ExistingLocalQueueRead = existingRead, MissingQueueIdentified = missingRead,
            SpoolerState = missing?.Spooler.State }, existingRead && missingRead ? 0 : 1);
    }

    public static int Run()
    {
        var passed = new List<string>();
        var target = new Target("PRINT01.example", "Office Printer");
        var other = new Target("PRINT01.example", "Renamed Printer");
        var machine = new Machine("Professional", 26300, 1, "X64", true);
        var absent = new Inventory([], null, 0, 0, 4, 0, true);
        var connected = absent with { Connections = [target.Path] };
        var live = new Observation(0, 0, 0, DriverKnown: true, PortKnown: true);
        var healthy = new Report(machine, connected, new(true, true, true), live, [], true, null);

        Check("UNC valid", Target.Parse(@"  \\PRINT01.example\Office Printer  ") == target);
        Check("UNC rejects local, device, nested and wildcard paths",
            new[] { @"C:\printer", @"\\?\C:", @"\\.\printer", @"\\HOST\share\child", @"\\HOST\p*" }.All(s => Target.Parse(s) is null));
        Check("709 normalizes standard forms",
            new[] { "709", "1801", "0x00000709", "0X80070709" }.All(s => Diagnose.ParseReported(s) == 1801));
        Check("Non-Win32 HRESULT stays raw", Diagnose.ParseReported("0x80041003") == 0x80041003);
        Check("Access-denied HRESULT", Diagnose.ParseReported("0x80070005") == 5);
        const string installedExe = @"C:\Kumpuni.Desktop\current\Kumpuni.exe";
        Check("Only another process from the same installation blocks an update when its path is known",
            AppUpdates.IsInstallPeer(installedExe, installedExe.ToLowerInvariant(), sameSession: false)
            && !AppUpdates.IsInstallPeer(installedExe, @"C:\Downloads\Kumpuni.exe", sameSession: true)
            && AppUpdates.IsInstallPeer(installedExe, null, sameSession: true)
            && !AppUpdates.IsInstallPeer(installedExe, null, sameSession: false));
        Check("Connect verified queue", DecisionFor(TaskKind.Connect, healthy with { Inventory = absent }).Action == Repair.Connect);
        Check("Renamed queue is a separate connection",
            Diagnose.Choose(TaskKind.Connect, other, healthy, 1801).Action == Repair.Connect);
        Check("Same connection only when known idle", DecisionFor(TaskKind.Connect, healthy).Action == Repair.Reconnect
            && DecisionFor(TaskKind.Connect, healthy with { Queue = live with { Jobs = 1 } }).Action == Repair.None
            && DecisionFor(TaskKind.Connect, healthy with { Queue = live with { Jobs = null } }).Action == Repair.None);
        Check("Default is a separate action", DecisionFor(TaskKind.Default, healthy, 1801).Action == Repair.SetDefault);
        Check("Default readback already matches",
            DecisionFor(TaskKind.Default, healthy with { Inventory = connected with { Default = target.Path } }).Action == Repair.None);
        Check("Default requires driver and port",
            DecisionFor(TaskKind.Default, healthy with { Queue = live with { DriverKnown = false } }).Action == Repair.None);
        Check("Test page needs a usable queue",
            DecisionFor(TaskKind.Print, healthy).Action == Repair.TestPage
            && DecisionFor(TaskKind.Print, healthy with { Queue = live with { QueueStatus = 1 } }).Action == Repair.None);
        Check("Current versus historical 709",
            DecisionFor(TaskKind.Connect, healthy with { Machine = machine with { Build = 19045 }, Queue = new(1801) }).Finding.Contains("by name")
            && DecisionFor(TaskKind.Connect, healthy with
            {
                Machine = machine with { Build = 22000, Revision = 318 }, Queue = new(1801)
            }).Finding.Contains("historical")
            && !DecisionFor(TaskKind.Default, healthy with
            {
                Machine = machine with { Build = 22000, Revision = 318 }, Queue = new(1801)
            }).Finding.Contains("historical"));
        Check("709 on 24H2 or later with SMB reachable offers a Windows sign-in to the host",
            DecisionFor(TaskKind.Connect, healthy with { Queue = new(1801) }) is { Action: Repair.SignInToHost } signIn
                && signIn.Finding.Contains("24H2")
            && DecisionFor(TaskKind.Connect, healthy with { Machine = machine with { Build = 26100 }, Queue = new(1801) }).Action == Repair.SignInToHost
            && DecisionFor(TaskKind.Connect, healthy with { Network = new(true, true, false), Queue = new(1801) }).Finding.Contains("by name")
            && DecisionFor(TaskKind.Connect, healthy with { Machine = machine with { Build = 22631 }, Queue = new(1801) }).Action == Repair.None);
        Check("Authentication and guest SMB errors offer sign-in; the repair loop tries it once",
            new[] { 5, 86, 1326, 1244, 1272 }.All(code => DecisionFor(TaskKind.Connect, healthy with { Queue = new(code) }).Action == Repair.SignInToHost)
            && SharedRepair.Next(TaskKind.Connect, target, healthy with { Queue = new(1272) }, null, false, new HashSet<Repair>()) == Repair.SignInToHost
            && SharedRepair.Next(TaskKind.Connect, target, healthy with { Queue = new(1272) }, null, false,
                new HashSet<Repair> { Repair.SignInToHost }) == Repair.None);
        Check("0x00000006 points to Type 3 print framework differences without offering a file change",
            DecisionFor(TaskKind.Connect, healthy with { Queue = new(6) }) is { Action: Repair.None } framework
                && framework.Details.Contains("UNIDRV.DLL") && framework.Details.Contains("Do not copy")
            && DecisionFor(TaskKind.Connect, healthy with { Inventory = absent }, 6).Finding.Contains("0x00000006")
            && Diagnose.ParseReported("0x00000006") == 6);
        var link = new ConnectivityEvidence(new(53, null, null), 1825, [new("IPv4", true, true)],
            new NetworkHealth([new("Wi-Fi", true, true)], ["Public"], ["Windows Defender Firewall"], ["Windows Defender"]), 0, [30803]);
        Report blockedSession = healthy with { Queue = new(3), Connectivity = link };
        Check("An open port with a failing session is reported as a session failure, naming both Windows errors",
            DecisionFor(TaskKind.Connect, blockedSession) is { Action: Repair.None } sessionFailure
                && sessionFailure.Finding.Contains("cannot open a file-sharing session")
                && sessionFailure.Details.Contains("(53)") && sessionFailure.Details.Contains("(1825)")
                && sessionFailure.Details.Contains("30803") && !sessionFailure.Details.Contains("Security software"));
        Check("Third-party security products are named; Microsoft Defender is not",
            DecisionFor(TaskKind.Connect, blockedSession with { Connectivity = link with { Health = link.Health! with { AntivirusProducts = ["ESET Security"] } } })
                .Details.Contains("ESET Security"));
        Report clientOff = blockedSession with { Connectivity = link with { Health = link.Health! with { Adapters = [new("Wi-Fi", false, true)] } } };
        Check("A disabled Client for Microsoft Networks is offered as an automatic repair",
            DecisionFor(TaskKind.Connect, clientOff).Action == Repair.EnableSmbClient
            && SharedRepair.Next(TaskKind.Connect, target, clientOff, null, false, new HashSet<Repair>()) == Repair.EnableSmbClient
            && Diagnose.Elevated(Repair.EnableSmbClient) && !Diagnose.Elevated(Repair.SignInToHost));
        Check("A listed host without the selected printer share and a refused sign-in get specific findings",
            DecisionFor(TaskKind.Connect, blockedSession with { Connectivity = link with { Session = new(0, 2, false) } }).Finding.Contains("does not publish")
            && DecisionFor(TaskKind.Connect, blockedSession with { Connectivity = link with { Session = new(1326, null, null) } }).Action == Repair.SignInToHost
            && DecisionFor(TaskKind.Connect, healthy with { Queue = new(3) }).Finding.Contains("0x00000003"));
        Report signingBlocked = blockedSession with
        {
            Inventory = absent,
            Connectivity = link with { Session = link.Session! with { IpcError = Connectivity.GuestSigningRequired } }
        };
        Check("The IPC$ probe distinguishes guest-signing rejection from an invalid signature",
            DecisionFor(TaskKind.Connect, signingBlocked) is { Action: Repair.SignInToHost } guestSigning
            && guestSigning.Finding.Contains("rejected the guest session") && guestSigning.Details.Contains("0xC05D0003")
            && DecisionFor(TaskKind.Connect, blockedSession with
            {
                Connectivity = link with { Session = link.Session! with { IpcError = Connectivity.InvalidSignature } }
            }) is { Action: Repair.None } invalidSignature && invalidSignature.Details.Contains("0xC000A000")
            && DecisionFor(TaskKind.Connect, blockedSession with
            {
                Connectivity = link with { Session = link.Session! with { IpcError = Connectivity.GuestEncryptionRequired } }
            }) is { Action: Repair.SignInToHost } guestEncryption && guestEncryption.Finding.Contains("encryption")
            && DecisionFor(TaskKind.Connect, blockedSession, Diagnose.ParseReported("3227320323")).Action == Repair.SignInToHost);
        Check("SMB settings suggest sign-in for error 53 without treating the policy alone as guest evidence",
            DecisionFor(TaskKind.Connect, blockedSession with
            {
                Connectivity = link with { Health = link.Health! with { SmbClient = new(true, true, null) } }
            }) is { Action: Repair.SignInToHost } ambiguous && ambiguous.Details.Contains("If the host offered guest access")
            && DecisionFor(TaskKind.Connect, blockedSession with
            {
                Connectivity = link with { Session = link.Session! with { IpcError = 0 } }
            }, 0xC05D0003).Action == Repair.None);
        Check("IPC credential conflicts preserve existing sessions and restrictions offer sign-in",
            DecisionFor(TaskKind.Connect, WithIpcError(1219)) is { Action: Repair.None } conflict
            && conflict.Finding.Contains("conflicting connection") && conflict.Details.Contains("1219")
            && new[] { 1327, 1385 }.All(code => DecisionFor(TaskKind.Connect, WithIpcError(code)) is
                { Action: Repair.SignInToHost } restriction && restriction.Finding.Contains("restricted")));

        Report WithIpcError(int code) => blockedSession with
        {
            Connectivity = link with { Session = link.Session! with { IpcError = code } }
        };
        Check("RPC 135 is not proof of print RPC",
            DecisionFor(TaskKind.Connect, healthy with { Queue = new(1722) }).Details.Contains("actual print RPC endpoint"));
        Check("Queue access denied is assigned to the host",
            DecisionFor(TaskKind.Connect, healthy with { Queue = new(5) }).Owner == "Print host operator");
        Check("Driver evidence is assigned to a client administrator",
            DecisionFor(TaskKind.Connect, healthy with { Inventory = absent, Events = [new(215, null, true)] }).Owner.Contains("Client administrator")
            && DecisionFor(TaskKind.Connect, healthy with { Inventory = absent }, 3019).Action == Repair.None);
        Check("Protected print evidence directs IT to a compatible queue",
            DecisionFor(TaskKind.Connect, healthy with { Inventory = absent, Policy = new(true, true, "TCP") }, 3019)
                .Details.Contains("IPP-backed"));
        Check("Unknown 0x11B cause cannot trigger repair",
            DecisionFor(TaskKind.Connect, healthy with { Queue = new(283) }).Action == Repair.None);
        Check("Stopped automatic workstation spooler can be started",
            DecisionFor(TaskKind.Connect, healthy with { Inventory = absent with { SpoolerState = 1 } }).Action == Repair.StartSpooler);
        Check("Managed or server spooler gets no automatic repair",
            DecisionFor(TaskKind.Connect, healthy with
            {
                Inventory = absent with { SpoolerState = 1, AutomaticStart = false }
            }).Action == Repair.None
            && DecisionFor(TaskKind.Connect, healthy with
            {
                Machine = machine with { Workstation = false }, Inventory = absent with { SpoolerState = 1 }
            }).Action == Repair.None);
        Check("Timed out probe cannot enable repair",
            DecisionFor(TaskKind.Connect, healthy with { Queue = null }).Action == Repair.None);
        Check("Usable live probe wins over DNS hint",
            DecisionFor(TaskKind.Connect, healthy with { Inventory = absent, Network = new(false, null, null) }).Action == Repair.Connect);
        Check("Worker data survives serialization",
            JsonSerializer.Deserialize<WorkerReply>(JsonSerializer.Serialize(new WorkerReply(Report: healthy)))?.Report
                is { Queue: { Code: 0 }, Inventory: { Connections.Length: 1 } });

        var localDetails = new QueueDetails("Private Driver Name", "Private Port Label", "Private Share Name", 0, 0, 8);
        var shared = new QueueEntry(@"\\Private Print Host\Private Remote Queue", QueueScope.SharedConnection);
        var hosted = new QueueEntry("Private Local Queue", QueueScope.SharedHere, localDetails);
        var ports = new[] { new PortEntry("Private Port Label", "Standard TCP/IP Port", "Private description 10.0.2.7", 3) };
        var snapshot = new ConsoleSnapshot(DateTimeOffset.UtcNow, machine, new(4, 0, hosted.Name, 0),
            new([hosted], 0), new([shared], 0),
            new([new("Private Driver Name", "Windows x64", 4, "1.2.3.4"), new("Private Driver Name", "Windows NT x86", 3, null)], 0),
            new(ports, 0), null);
        var technicianChecks = new Dictionary<string, ConsoleCheck> { [hosted.Name] = new(DateTimeOffset.UtcNow,
            new(0, 0, localDetails with { Status = 0x80 }), [new(215, null, true)], true) };
        var technicianTests = new Dictionary<string, ConsoleTest> { [hosted.Name] = new(DateTimeOffset.UtcNow,
            new("Private test error text", true, JobState: "Reported printed by Windows"), true) };
        var help = new PrinterHelpEvidence(new(DateTimeOffset.UtcNow, true, 0),
            new(DateTimeOffset.UtcNow, 9100, PrinterEndpointStatus.Reachable));
        string json = ConsoleReport.Serialize(snapshot, technicianChecks, technicianTests, help);
        using var exported = JsonDocument.Parse(json);
        JsonElement exportedQueue = exported.RootElement.GetProperty("Queues").EnumerateArray()
            .Single(element => element.GetProperty("Scope").GetString() == "SharedHere");
        Check("Console inventory distinguishes hosted and connected queues",
            snapshot.Queues() is { Length: 2 } && snapshot.Local.Items[0].Scope == QueueScope.SharedHere
            && snapshot.Shared.Items[0].Scope == QueueScope.SharedConnection);
        Check("Console projection preserves partial inventory errors",
            ConsoleReport.Serialize(snapshot with { Ports = new([], 1460) }, technicianChecks, technicianTests).Contains("1460"));
        Check("Console queue health preserves inactive, busy, paused and unknown",
            ConsoleHealth.Health(localDetails with { Status = 0x01000000 }) == QueueHealth.Available
            && ConsoleHealth.Health(localDetails with { Jobs = 3 }) == QueueHealth.Busy
            && ConsoleHealth.Health(localDetails with { Status = 0x100000 }) == QueueHealth.Attention
            && ConsoleHealth.Health(localDetails, new QueueProbe(5, 0, null)) == QueueHealth.Unknown);
        Check("Console selection tracks physical and virtual ports",
            ConsoleHealth.Kind(localDetails, ports) == PortKind.TcpIp
            && ConsoleHealth.Kind(localDetails with { Port = "USB001" }, ports) == PortKind.Usb
            && ConsoleHealth.Kind(localDetails with { Port = "https://printer.example/ipp/print" }, ports) == PortKind.Ipp
            && !ConsoleHealth.MaySendTest(localDetails with { Port = "PORTPROMPT:" }, ports)
            && !ConsoleHealth.MaySendTest(localDetails with { Port = @"C:\print.prn" }, ports)
            && !ConsoleHealth.MaySendTest(localDetails with { Port = @"\\SERVER\FILES\print.prn" }, ports)
            && !ConsoleHealth.MaySendTest(localDetails with { Port = "WSD-1", Status = 0x80 }, ports)
            && !ConsoleHealth.MaySendTest(localDetails, ports, new QueueProbe(0, 5, null))
            && ConsoleHealth.MaySendTest(localDetails, ports));
        Check("Console export retains relationships and test evidence",
            exportedQueue.GetProperty("IsDefault").GetBoolean()
            && exportedQueue.GetProperty("Health").GetString() == "Attention"
            && exportedQueue.GetProperty("Drivers").GetArrayLength() == 2
            && exportedQueue.GetProperty("Ports")[0].GetString() == "P001"
            && exportedQueue.GetProperty("Test").GetProperty("PageConfirmed").GetBoolean());
        Check("Console export excludes names, host addresses and free text", !json.Contains("Private", StringComparison.Ordinal)
            && !json.Contains("10.0.2.7", StringComparison.Ordinal));
        Check("Network endpoint input accepts hostnames and IP literals",
            PrinterHelp.ParseHost(" print01.example ") == "print01.example"
            && PrinterHelp.ParseHost("192.0.2.1") == "192.0.2.1"
            && PrinterHelp.ParseHost("[2001:db8::1]") == "2001:db8::1");
        Check("Network endpoint input rejects URLs, UNC paths and embedded ports",
            new[] { "https://printer.example/ipp/print", @"\\HOST\Print", "printer.example:9100",
                "[2001:db8::1]:9100", "2001:db8::1/64", "bad host", " ", "user@printer.example" }
            .All(input => PrinterHelp.ParseHost(input) is null));
        Check("Connection filter supports pooled ports and keeps remote shares distinct",
            ConsoleHealth.MatchesConnection(PrinterConnection.Usb, QueueScope.Local, localDetails with { Port = "USB001,WSD-1" }, ports)
            && ConsoleHealth.MatchesConnection(PrinterConnection.Network, QueueScope.Local, localDetails with { Port = "USB001,WSD-1" }, ports)
            && ConsoleHealth.MatchesConnection(PrinterConnection.Shared, QueueScope.Local, localDetails with { Port = @"\\HOST\Printer" }, ports)
            && ConsoleHealth.MatchesConnection(PrinterConnection.Shared, QueueScope.SharedConnection, null, ports)
            && !ConsoleHealth.MatchesConnection(PrinterConnection.Network, QueueScope.SharedConnection, localDetails, ports)
            && !ConsoleHealth.MatchesConnection(PrinterConnection.Usb, QueueScope.Local, localDetails with { Port = "USB001,FILE:" }, ports));
        JsonElement helpJson = exported.RootElement.GetProperty("PrinterHelp");
        JsonElement networkJson = helpJson.GetProperty("Network");
        Check("Redacted report includes only the last USB and TCP check evidence",
            helpJson.GetProperty("Usb").GetProperty("Present").GetBoolean()
            && networkJson.GetProperty("Status").GetString() == "Reachable"
            && networkJson.GetProperty("TcpPort").GetInt32() == 9100
            && networkJson.EnumerateObject().Count() == 3
            && helpJson.GetProperty("Usb").EnumerateObject().Count() == 3);
        Check("Console inventory survives worker serialization",
            JsonSerializer.Deserialize<WorkerReply>(JsonSerializer.Serialize(new WorkerReply(Console: snapshot)))?.Console
                is { Ports.Items.Length: 1, Drivers.Items.Length: 2 });

        Check("Queue details error prevents repair",
            DecisionFor(TaskKind.Connect, healthy with { Inventory = absent, Queue = live with { DetailsError = 5 } }).Action == Repair.None);
        Check("Authentication, existing sessions, and guest SMB report specific findings",
            DecisionFor(TaskKind.Connect, healthy with { Queue = new(1326) }).Finding.Contains("authenticate")
            && DecisionFor(TaskKind.Connect, healthy with { Queue = new(1219) }).Finding.Contains("conflicting connection")
            && DecisionFor(TaskKind.Connect, healthy with { Queue = new(1272) }).Finding.Contains("guest SMB"));
        Check("Print RPC compatibility and unavailable drivers are diagnosed without a policy rewrite",
            DecisionFor(TaskKind.Connect, healthy with { Queue = new(1764) }).Finding.Contains("compatibility")
            && DecisionFor(TaskKind.Connect, healthy with { Queue = new(124) }).Action == Repair.None
            && DecisionFor(TaskKind.Connect, healthy with { Queue = new(3014) }).Owner.Contains("administrator"));
        var host = new SharingEvidence(true, true, 0, new(4, 2, 0), new(1, 2, 0), new(4, 2, 0),
            new(2, 2), new(0), new(7), new(null), new(null), new(null), new(null), new(null), new(null), new(null), new(null), new(null));
        Report hostReport = healthy with { Sharing = host };
        var none = new HashSet<Repair>();
        Check("Host default repair starts the Server service only for a published share with named pipes configured",
            SharedRepair.Next(TaskKind.Connect, target, hostReport, null, false, none) == Repair.StartLanmanServer
            && SharedRepair.Next(TaskKind.Connect, target, hostReport with { Sharing = host with { RpcProtocols = new(5) } },
                null, false, none) == Repair.None
            && SharedRepair.Next(TaskKind.Connect, target, hostReport with { Sharing = host with { SharedQueuePresent = false } },
                null, false, none) == Repair.None
            && SharedRepair.Next(TaskKind.Connect, target, hostReport with { Sharing = host with { Server = new(1, 4, 0) } },
                null, false, none) == Repair.None);
        var hostLink = link with { Session = new(0, 1, true), Health = link.Health! with { Categories = ["Private"] } };
        Report servingOpen = hostReport with { Sharing = host with { Server = new(4, 2, 0), Firewall = new(2, 0) }, Connectivity = hostLink };
        Check("A host with Windows Firewall off is flagged before the RPC privacy override and repaired automatically",
            DecisionFor(TaskKind.Connect, servingOpen) is { Action: Repair.SecureHostSharing } firewallOff && firewallOff.Finding.Contains("Firewall is turned off")
            && SharedRepair.Next(TaskKind.Connect, target, servingOpen, null, false, none) == Repair.SecureHostSharing
            && SharedRepair.Next(TaskKind.Connect, target, servingOpen, null, false, new HashSet<Repair> { Repair.SecureHostSharing }) == Repair.None);
        Check("Host sharing does not change the firewall or category with multiple or domain network profiles",
            new[] { new[] { "Private", "Public" }, ["DomainAuthenticated"], [] }.All(categories =>
                DecisionFor(TaskKind.Connect, servingOpen with
                {
                    Connectivity = hostLink with { Health = hostLink.Health! with { Categories = categories } }
                }).Action == Repair.None));
        Report secureHost = servingOpen with { Sharing = servingOpen.Sharing! with { Firewall = new(2, 2) } };
        SharedRepairRun securedHostRun = SharedRepair.ExecuteAsync(TaskKind.Connect, target, servingOpen, null, false,
            _ => Task.FromResult(new ActionResult("Windows confirmed the action.", Verified: true)),
            () => Task.FromResult<Report?>(secureHost)).GetAwaiter().GetResult();
        SharedRepairRun insecureHostRun = SharedRepair.ExecuteAsync(TaskKind.Connect, target, servingOpen, null, false,
            _ => Task.FromResult(new ActionResult("Windows confirmed the action.", Verified: true)),
            () => Task.FromResult<Report?>(servingOpen)).GetAwaiter().GetResult();
        Check("Host sharing verifies that the active Private firewall is enabled",
            securedHostRun.Steps is [{ Action: Repair.SecureHostSharing, StateMatches: true }]
            && insecureHostRun.Steps is [{ Action: Repair.SecureHostSharing, StateMatches: false }]);
        Report serverOff = hostReport with { Sharing = host with { Server = new(4, 2, 0) },
            Connectivity = hostLink with { Health = hostLink.Health! with { Adapters = [new("Wi-Fi", true, false)] } } };
        Check("Host-side File and Printer Sharing binding, Public network and self-session failures are diagnosed",
            DecisionFor(TaskKind.Connect, serverOff).Action == Repair.EnableSmbServer
            && SharedRepair.Next(TaskKind.Connect, target, serverOff, null, false, none) == Repair.EnableSmbServer
            && DecisionFor(TaskKind.Connect, serverOff with { Connectivity = hostLink with { Health = hostLink.Health! with { Categories = ["Public"] } } })
                .Finding.Contains("Public network")
            && DecisionFor(TaskKind.Connect, serverOff with { Connectivity = hostLink with { Session = new(53, null, null) } })
                .Finding.Contains("to itself"));
        Report blockedByPolicy = hostReport with { Sharing = host with { RemoteSpooler = new(2) } };
        Check("The host client-connection policy blocks repairs and receives a specific finding",
            SharedRepair.Next(TaskKind.Connect, target, blockedByPolicy, null, true, none) == Repair.None
            && DecisionFor(TaskKind.Connect, blockedByPolicy).Finding.Contains("policy blocks", StringComparison.Ordinal));
        Report servingHost = hostReport with { Sharing = host with { Server = new(4, 2, 0) } };
        Report accountHost = servingHost with { Sharing = servingHost.Sharing! with { Privacy = new(1), LocalAccountModel = new(0) } };
        Report guestOnlyHost = accountHost with { Sharing = accountHost.Sharing! with { LocalAccountModel = new(1) } };
        Check("Printing account creation is optional and limited to a publishing workstation with authenticated local access",
            PrintingAccount.CanCreate(accountHost) && !PrintingAccount.CanCreate(guestOnlyHost)
            && !PrintingAccount.CanCreate(accountHost with { Machine = machine with { Workstation = false } })
            && !PrintingAccount.CanCreate(blockedSession)
            && PrintingAccount.CreateAsync(new("fixture.invalid", "Printer")).GetAwaiter().GetResult() is { Verified: false, Code: 87 }
            && SharedRepair.Next(TaskKind.Connect, target, accountHost, null, false, none) == Repair.None
            && DecisionFor(TaskKind.Connect, guestOnlyHost).Finding.Contains("maps local network sign-ins to Guest"));
        Check("RPC privacy repair requires explicit consent, local published share and a disabled DWORD",
            SharedRepair.Next(TaskKind.Connect, target, servingHost, null, false, none) == Repair.None
            && SharedRepair.Next(TaskKind.Connect, target, servingHost, null, true, none) == Repair.RestoreRpcPrivacy
            && SharedRepair.Next(TaskKind.Connect, target, servingHost with { Sharing = host with { Server = new(4, 2, 0), Privacy = new(null) } },
                null, true, none) == Repair.None);
        Check("Automation leaves a live healthy connection alone unless the user reported a matching error",
            SharedRepair.Next(TaskKind.Connect, target, healthy, null, false, none) == Repair.None
            && SharedRepair.Next(TaskKind.Connect, target, healthy, 1801, false, none) == Repair.Reconnect
            && SharedRepair.Next(TaskKind.Connect, target, healthy, 283, false, none) == Repair.None);
        Report before = healthy with { Inventory = absent };
        Report after = healthy with { Inventory = connected with { Default = target.Path } };
        var signInSnapshots = new Queue<Report?>([before with { Connectivity = link with { Session = new(0, 2, true) } }, after]);
        SharedRepairRun signInRun = SharedRepair.ExecuteAsync(TaskKind.Connect, target, signingBlocked, null, false,
            _ => Task.FromResult(new ActionResult("Windows confirmed the action.", Verified: true)),
            () => Task.FromResult(signInSnapshots.Dequeue())).GetAwaiter().GetResult();
        SharedRepairRun unconfirmedSignIn = SharedRepair.ExecuteAsync(TaskKind.Connect, target, signingBlocked, null, false,
            _ => Task.FromResult(new ActionResult("Windows confirmed the action.", Verified: true)),
            () => Task.FromResult<Report?>(signingBlocked)).GetAwaiter().GetResult();
        Check("Signing repair prompts once, rechecks SMB, then connects only after verification",
            signInRun.Steps is [{ Action: Repair.SignInToHost, StateMatches: true }, { Action: Repair.Connect, StateMatches: true }]
            && signInRun.Final == after && signInSnapshots.Count == 0
            && unconfirmedSignIn.Steps is [{ Action: Repair.SignInToHost, StateMatches: false }]);
        var snapshots = new Queue<Report?>([healthy, after]);
        SharedRepairRun run = SharedRepair.ExecuteAsync(TaskKind.Default, target, before, null, false,
            _ => Task.FromResult(new ActionResult("Verified by Windows.", Verified: true)),
            () => Task.FromResult(snapshots.Dequeue())).GetAwaiter().GetResult();
        Check("Automatic default repair connects, rechecks and sets default once",
            run.Steps.Select(step => step.Action).SequenceEqual([Repair.Connect, Repair.SetDefault])
            && run.Steps.All(step => step.RecheckCompleted) && run.Final == after && snapshots.Count == 0);
        var stopped = healthy with { Inventory = absent with { SpoolerState = 1 } };
        SharedRepairRun failed = SharedRepair.ExecuteAsync(TaskKind.Connect, target, stopped, null, false,
            _ => Task.FromResult(new ActionResult("Elevation was cancelled.", Code: 1223)),
            () => Task.FromResult<Report?>(before)).GetAwaiter().GetResult();
        Check("An unverified repair rechecks state and stops without applying another action",
            failed.Steps is [{ Action: Repair.StartSpooler, Result.Code: 1223, RecheckCompleted: true }]
            && failed.Final == before);
        SharedRepairRun timeout = SharedRepair.ExecuteAsync(TaskKind.Default, target, before, null, false,
            _ => Task.FromResult(new ActionResult("Verified by Windows.", Verified: true)),
            () => Task.FromResult<Report?>(null)).GetAwaiter().GetResult();
        Check("Report preserves a completed action when the post-repair check times out",
            timeout.Steps is [{ Action: Repair.Connect, Result.Verified: true, RecheckCompleted: false }] && timeout.Final is null);
        var hostSnapshots = new Queue<Report?>([hostReport, servingHost,
            servingHost with { Sharing = servingHost.Sharing! with { Privacy = new(1) } }]);
        SharedRepairRun hostRun = SharedRepair.ExecuteAsync(TaskKind.Connect, target,
            hostReport with { Sharing = host with { Spooler = new(1, 2, 0), SharedQueuePresent = null } }, null, true,
            _ => Task.FromResult(new ActionResult("Verified by Windows.", Verified: true)),
            () => Task.FromResult(hostSnapshots.Dequeue())).GetAwaiter().GetResult();
        Check("Host repairs verify a published share after starting the Spooler and report restart after RPC privacy",
            hostRun.Steps.Select(step => step.Action).SequenceEqual([Repair.StartHostSpooler, Repair.StartLanmanServer, Repair.RestoreRpcPrivacy])
            && hostRun.Final?.Sharing?.Privacy.Value == 1
            && hostRun.Summary.Contains("restart", StringComparison.Ordinal));
        SharedRepairRun reverted = SharedRepair.ExecuteAsync(TaskKind.Connect, target, servingHost, null, true,
            _ => Task.FromResult(new ActionResult("Verified by Windows.", Verified: true)),
            () => Task.FromResult<Report?>(servingHost)).GetAwaiter().GetResult();
        Check("Post-repair verification detects a registry override that reappears",
            reverted.Steps is [{ Action: Repair.RestoreRpcPrivacy, StateMatches: false }]
            && reverted.Summary.Contains("different state", StringComparison.Ordinal));
        var printSnapshots = new Queue<Report?>([healthy, healthy]);
        SharedRepairRun printRun = SharedRepair.ExecuteAsync(TaskKind.Print, target, before, null, false,
            action => Task.FromResult(new ActionResult("Verified by Windows.", Verified: true,
                JobState: action == Repair.TestPage ? "Submitted" : null)),
            () => Task.FromResult(printSnapshots.Dequeue())).GetAwaiter().GetResult();
        Check("Automatic Print operation submits at most one test page after connecting the queue",
            printRun.Steps.Select(step => step.Action).SequenceEqual([Repair.Connect, Repair.TestPage])
            && printRun.Steps[^1].Result.JobState == "Submitted" && printSnapshots.Count == 0);
        string sharedJson = SharedRepair.SerializeReport(target, TaskKind.Default, null, after,
            DecisionFor(TaskKind.Default, after), run.Steps[^1].Result, false, run);
        using var sharedReport = JsonDocument.Parse(sharedJson);
        JsonElement sharedRun = sharedReport.RootElement.GetProperty("RepairRun");
        Check("Shared JSON records redacted before and after evidence and the ordered action log",
            !sharedJson.Contains(target.Host, StringComparison.OrdinalIgnoreCase)
            && !sharedJson.Contains(target.Share, StringComparison.OrdinalIgnoreCase)
            && !sharedRun.GetProperty("Before").GetProperty("Inventory").GetProperty("ConnectionPresent").GetBoolean()
            && sharedRun.GetProperty("After").GetProperty("Inventory").GetProperty("SelectedQueueIsDefault").GetBoolean()
            && sharedRun.GetProperty("Actions").GetArrayLength() == 2
            && sharedRun.GetProperty("Actions")[1].GetProperty("Action").GetString() == "SetDefault");

        QueueJobs counts = NativePrinting.SummarizeJobs([0x3, 0x8, 0x200, 0x10, 0x80], 0, 5);
        Check("Job audit counts spool flags without exposing job identity",
            counts is { Sampled: 5, Paused: 1, Blocked: 2, Printing: 2, SampleLimitReached: true });
        var current = new AdvancedSharing(new(1, 3, 0), new(4, 3, 0), null, 5,
            new("QueueOverride", 0, 0, 1024, 0),
            new(new(0, "Configured", 3, 5), counts), new(0, 0, null, null));
        Report advancedReport = servingHost with { Sharing = servingHost.Sharing! with { Privacy = new(1) }, Advanced = current };
        Check("A full spool volume becomes a host finding",
            DecisionFor(TaskKind.Print, advancedReport).Finding.Contains("no free space", StringComparison.Ordinal));
        Report emptyAcl = advancedReport with
        {
            Advanced = current with { Spool = current.Spool with { FreeBytes = 100 }, Queue = new(new(0, "Empty", 0, 5), counts) }
        };
        Check("Queue diagnostics distinguish an empty ACL from a Windows access failure",
            DecisionFor(TaskKind.Print, emptyAcl).Finding.Contains("empty discretionary access", StringComparison.Ordinal));
        string advanceJson = SharedRepair.SerializeReport(target, TaskKind.Connect, null, advancedReport,
            DecisionFor(TaskKind.Connect, advancedReport), null, false, null, [new(DateTimeOffset.UtcNow,
                "StagePrinterInf", false, 1223, "Elevation was cancelled.")]);
        using var advanceReport = JsonDocument.Parse(advanceJson);
        JsonElement advancedJson = advanceReport.RootElement.GetProperty("Evidence").GetProperty("Advanced");
        Check("Additional diagnostics and driver outcomes are in the redacted report",
            advancedJson.GetProperty("Spool").GetProperty("FreeBytes").GetUInt64() == 0
            && advancedJson.GetProperty("Queue").GetProperty("Permissions").GetProperty("AceCount").GetUInt32() == 3
            && advanceReport.RootElement.GetProperty("DriverDeployments")[0].GetProperty("Code").GetInt32() == 1223
            && !advanceJson.Contains(target.Host, StringComparison.OrdinalIgnoreCase)
            && !advanceJson.Contains(target.Share, StringComparison.OrdinalIgnoreCase));

        using var clientImport = JsonDocument.Parse("""
            {"SchemaVersion":2,"Evidence":{"Sharing":{"LocalHost":false},"Machine":{"Build":26100,"Revision":12},"Queue":{"Code":5},"Policy":{"RpcTransport":"Named pipes"}}}
            """);
        using var hostImport = JsonDocument.Parse("""
            {"SchemaVersion":2,"Evidence":{"Sharing":{"LocalHost":true,"SharedQueuePresent":true,"RpcProtocols":{"Value":5}},"Machine":{"Build":26200,"Revision":1},"Queue":{"Code":0}}}
            """);
        ReportDifference[] differences = SharingReportComparison.Compare(clientImport.RootElement, hostImport.RootElement);
        Check("Paired reports compare live access and known RPC protocols",
            differences.Any(row => row is { Field: "Print RPC protocol", Client: "Named pipes", Host: "TCP" })
            && differences.Any(row => row is { Field: "Live queue access", Client: "Open error 5", Host: "Opened" }));
        using var clientFramework = JsonDocument.Parse("""
            {"SchemaVersion":2,"Evidence":{"Sharing":{"LocalHost":false},"Advanced":{"Framework":[
              {"Name":"UNIDRV.DLL","Version":"0.3.26100.6584","Error":0},{"Name":"UNIDRVUI.DLL","Version":"0.3.26100.6584","Error":0},
              {"Name":"UNIRES.DLL","Version":"0.3.26100.6584","Error":0},{"Name":"PSCRIPT5.DLL","Version":"0.3.26100.6584","Error":0},
              {"Name":"PS5UI.DLL","Version":"0.3.26100.6584","Error":0}]}}}
            """);
        using var hostFramework = JsonDocument.Parse("""
            {"SchemaVersion":2,"Evidence":{"Sharing":{"LocalHost":true},"Advanced":{"Framework":[
              {"Name":"UNIDRV.DLL","Version":"0.3.20348.4171","Error":0},{"Name":"UNIDRVUI.DLL","Version":"0.3.26100.6584","Error":0},
              {"Name":"UNIRES.DLL","Version":"0.3.26100.6584","Error":0},{"Name":"PSCRIPT5.DLL","Version":"0.3.26100.6584","Error":0},
              {"Name":"PS5UI.DLL","Error":2}]}}}
            """);
        ReportDifference[] frameworkRows = SharingReportComparison.Compare(clientFramework.RootElement, hostFramework.RootElement);
        Check("Paired reports compare Type 3 print framework file versions",
            frameworkRows.Any(row => row is { Field: "Print framework UNIDRV.DLL", Client: "0.3.26100.6584", Host: "0.3.20348.4171" })
            && frameworkRows.Any(row => row is { Field: "Print framework PS5UI.DLL", Host: "Not installed" })
            && frameworkRows.Any(row => row is { Field: "Type 3 print framework files", Client: "Unknown" })
            && differences.Any(row => row is { Field: "Print framework UNIDRV.DLL", Client: "Not in report" }));
        using var clientLink = JsonDocument.Parse("""
            {"SchemaVersion":2,"Evidence":{"Sharing":{"LocalHost":false,"Firewall":{"ActiveProfiles":4,"EnabledLocally":0}},
             "Connectivity":{"Session":{"Error":53},"PrintServerError":1825,"Health":{"Adapters":[{"Adapter":"Wi-Fi","Client":true,"Server":true}],
             "Categories":["Public"],"FirewallProducts":[],"AntivirusProducts":["Windows Defender"]}}}}
            """);
        using var hostLinkJson = JsonDocument.Parse("""
            {"SchemaVersion":2,"Evidence":{"Sharing":{"LocalHost":true,"Firewall":{"ActiveProfiles":2,"EnabledLocally":2}},
             "Connectivity":{"Session":{"Error":0,"SharePublished":true},"PrintServerError":0,"Health":{"Adapters":[{"Adapter":"Ethernet","Client":true,"Server":false}],
             "Categories":["Private"],"FirewallProducts":[],"AntivirusProducts":[]}}}}
            """);
        ReportDifference[] linkRows = SharingReportComparison.Compare(clientLink.RootElement, hostLinkJson.RootElement);
        Check("Paired reports compare session, print server, network type, firewall, security products and bindings",
            linkRows.Any(row => row is { Field: "File-sharing session", Client: "Error 53", Host: "Opened, share listed" })
            && linkRows.Any(row => row is { Field: "Host print server (print RPC)", Client: "Error 1825", Host: "Opened" })
            && linkRows.Any(row => row is { Field: "Network type", Client: "Public", Host: "Private" })
            && linkRows.Any(row => row is { Field: "Windows Firewall on active network", Client: "Off", Host: "On" })
            && linkRows.Any(row => row is { Field: "Security products", Client: "Windows Defender", Host: "None reported" })
            && linkRows.Any(row => row is { Field: "File and Printer Sharing binding", Host: "Off on an active adapter" })
            && differences.Any(row => row is { Field: "File-sharing session", Client: "Not in report" }));
        PrintFrameworkFile[] localFramework = SharingDiagnostics.ReadPrintFramework();
        Check("This PC's print framework files are read without errors other than missing files",
            localFramework.Length == SharingDiagnostics.FrameworkFiles.Length && localFramework.All(file => file.Error is 0 or 2));

        KnownIssue[] bundledIssues = KnownIssues.Bundled();
        Check("Bundled known issues load and match by build, revision and error code",
            bundledIssues.Length >= 3
            && KnownIssues.Match(bundledIssues, machine with { Build = 26100 }, 1801).Any(issue => issue.Id == "secure-smb-24h2")
            && !KnownIssues.Match(bundledIssues, machine with { Build = 22631 }, 1801).Any(issue => issue.Id == "secure-smb-24h2")
            && KnownIssues.Match(bundledIssues, machine with { Build = 20348 }, 6).Any(issue => issue.Id == "type3-framework-2026-09")
            && !KnownIssues.Match(bundledIssues, machine with { Build = 20348 }, null).Any(issue => issue.Id == "type3-framework-2026-09")
            && KnownIssues.Match([new("r", "t", "d", "reported", null, MinRevision: 100, MaxRevision: 200)], machine with { Revision = 150 }, null).Length == 1
            && KnownIssues.Match([new("r", "t", "d", "reported", null, MinRevision: 100, MaxRevision: 200)], machine with { Revision = 99 }, null).Length == 0);
        Check("Downloaded known issues reject unsafe or malformed content",
            KnownIssues.Parse("""{"schema":1,"issues":[{"id":"a","title":"t","detail":"d","status":"reported","url":"http://example.test/"}]}""") is null
            && KnownIssues.Parse("""{"schema":2,"issues":[]}""") is null
            && KnownIssues.Parse("not json") is null
            && KnownIssues.Parse("""{"schema":1,"issues":[{"id":"a","title":"t","detail":"d","status":"reported","url":"https://example.test/a"}]}""") is { Length: 1 }
            && KnownIssues.RemoteUri(new Uri("https://github.com/owner/repo"))?.AbsoluteUri
                == "https://github.com/owner/repo/releases/download/known-issues/known-issues.json"
            && KnownIssues.RemoteUri(new Uri("https://updates.example.test/kumpuni"))?.AbsoluteUri
                == "https://updates.example.test/kumpuni/known-issues.json"
            && KnownIssues.RelevantCode(healthy with { Queue = new(6) }, 1801) == 6
            && KnownIssues.RelevantCode(healthy, 1801) == 1801);

        bool rejected = false;
        try { _ = SharingReportComparison.Compare(hostImport.RootElement, clientImport.RootElement); }
        catch (InvalidDataException) { rejected = true; }
        Check("Report comparison validates client and host roles", rejected);
        Check("Firewall scope uses exact addresses, masked subnets, ranges and unknown keywords",
            ClientFirewall.MatchAddress(["192.0.2.0/255.255.255.0"], IPAddress.Parse("192.0.2.10")) == "Matches"
            && ClientFirewall.MatchAddress(["192.0.2.2-192.0.2.9"], IPAddress.Parse("192.0.2.10")) == "Outside scope"
            && ClientFirewall.MatchAddress(["2001:db8::/65"], IPAddress.Parse("2001:db8::987")) == "Matches"
            && ClientFirewall.MatchAddress(["LocalSubnet"], IPAddress.Loopback) == "Needs review"
            && ClientFirewall.MatchAddress(["192.0.2.10"], IPAddress.Parse("::ffff:192.0.2.10")) == "Matches");
        Check("Firewall port scope recognizes RPC mapper, explicit ranges and unresolved dynamic RPC",
            ClientFirewall.MatchPort(["RPC-EPMap"], 135) == "Matches"
            && ClientFirewall.MatchPort(["5000-6000"], 5500) == "Matches"
            && ClientFirewall.MatchPort(["RPC"], 5500) == "Needs review"
            && ClientFirewall.MatchPort(["445"], 5500) == "Outside scope");
        var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var selectedSid = new SecurityIdentifier("S-1-5-21-1-2-3-1111");
        var descriptor = new RawSecurityDescriptor("D:P(D;;0x00020008;;;BA)(A;;0x000F000C;;;SY)");
        RawSecurityDescriptor? granted = NativePrinting.AddExplicitPrint(descriptor, selectedSid);
        Check("ACL edit preserves explicit denies and grants only Print to the selected identity",
            granted?.DiscretionaryAcl is { Count: 3 } acl
            && acl[0] is CommonAce { AceQualifier: AceQualifier.AccessDenied } deny && deny.SecurityIdentifier == adminSid
            && acl[2] is CommonAce { AceQualifier: AceQualifier.AccessAllowed, AccessMask: 0x20008 } allow
            && allow.SecurityIdentifier == selectedSid
            && (granted.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0
            && ReferenceEquals(granted, NativePrinting.AddExplicitPrint(granted, selectedSid)));
        Check("ACL edit rejects broad or unrestricted permission changes",
            NativePrinting.AddExplicitPrint(descriptor, new SecurityIdentifier(WellKnownSidType.WorldSid, null)) is null
            && NativePrinting.AddExplicitPrint(new RawSecurityDescriptor("O:SYG:SY"), selectedSid) is null);
        Check("Queue sharing and individual job commands validate the selected operation",
            NativePrinting.ValidShareName("Accounting Printer") && !NativePrinting.ValidShareName("ADMIN$")
            && !NativePrinting.ValidShareName(@"Host\Printer")
            && NativePrinting.CanApplyJobAction(PrintJobAction.Resume, 1)
            && !NativePrinting.CanApplyJobAction(PrintJobAction.Restart, 1)
            && NativePrinting.CanApplyJobAction(PrintJobAction.Restart, 0x10)
            && !NativePrinting.CanApplyJobAction(PrintJobAction.Cancel, 0x80));

        var quickProfile = new PrinterProfile(target.Path, true, false);
        string setupFile = Path.Combine(Path.GetTempPath(), $"KumpuniSetupCheck-{Guid.NewGuid():N}.json");
        try
        {
            PrinterProfileStore.Save(setupFile, quickProfile);
            Check("One-time setup persists and can be replaced", PrinterProfileStore.Load(setupFile) == quickProfile);
            PrinterProfileStore.Save(setupFile, quickProfile with { MakeDefault = true });
            Check("Updated setup retains explicit default preference", PrinterProfileStore.Load(setupFile)?.MakeDefault == true
                && !(quickProfile with { Queue = "not a shared path" }).IsValid());
        }
        finally { File.Delete(setupFile); }

        Report stale = healthy with { CachedQueue = live with { QueueStatus = 0x80 } };
        var quickSnapshots = new Queue<Report?>([stale, healthy]);
        var quickActions = new List<Repair>();
        var quickPhases = new List<QuickPhase>();
        QuickOutcome quickResult = QuickRepair.RunSharedAsync(quickProfile, target,
            () => Task.FromResult(quickSnapshots.Dequeue()), action =>
            {
                quickActions.Add(action);
                return Task.FromResult(new ActionResult("Connection verified.", Verified: true));
            }, update => quickPhases.Add(update.Phase)).GetAwaiter().GetResult();
        Check("One click repairs a verified idle stale shared connection and rechecks readiness",
            quickResult.Ready && quickActions.SequenceEqual([Repair.Reconnect]) && quickSnapshots.Count == 0);
        Check("Shared quick repair reports the phases in Windows operation order",
            quickPhases.SequenceEqual([QuickPhase.Diagnose, QuickPhase.Repair, QuickPhase.Verify]));
        Check("One click preserves a stale connection with jobs or a host fault",
            !QuickRepair.HasStaleConnection(target, stale with { CachedQueue = stale.CachedQueue! with { Jobs = 1 } })
            && !QuickRepair.HasStaleConnection(target, stale with { Queue = live with { QueueStatus = 0x80 } }));
        var localProfile = new PrinterProfile("Fixture Private Local Queue", false, true);
        var localReady = new QuickLocalEvidence(true, new(4, 2, 0), new(0, 0, localDetails with { Jobs = 0 }),
            PortKind.Usb, false, 0);
        var localSnapshots = new Queue<QuickLocalEvidence?>([localReady with { Spooler = new(1, 2, 0), Probe = null }, localReady,
            localReady with { IsDefault = true }]);
        var appliedLocal = new List<Repair>();
        QuickOutcome localResult = QuickRepair.RunLocalAsync(localProfile, _ => { },
            () => Task.FromResult(localSnapshots.Dequeue()), action =>
            {
                appliedLocal.Add(action);
                return Task.FromResult(new ActionResult("Windows verified the change.", Verified: true));
            }).GetAwaiter().GetResult();
        Check("One click rechecks the spooler before setting the opted-in default printer",
            localResult.Ready && appliedLocal.SequenceEqual([Repair.StartSpooler, Repair.SetDefault]) && localSnapshots.Count == 0);
        string localExport = QuickRepair.Serialize(localProfile, localResult);
        Check("Quick report omits queue, driver and port names",
            !localExport.Contains("Private", StringComparison.OrdinalIgnoreCase) && localExport.Contains("Set default printer", StringComparison.Ordinal));

        return Program.Write(new { Passed = passed.Count, Checks = passed });

        Decision DecisionFor(TaskKind task, Report report, uint? reported = null) => Diagnose.Choose(task, target, report, reported);

        void Check(string name, bool success)
        {
            if (!success)
            {
                Program.Write(new { Failed = name, Passed = passed.Count });
                Environment.Exit(1);
            }
            passed.Add(name);
        }
    }
}
