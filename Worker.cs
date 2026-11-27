using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace Kumpuni;

internal static class Worker
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(3);

    public static async Task<WorkerReply?> RunAsync(string command, string? argument = null, Repair? repair = null,
        bool printJob = false)
    {
        ProcessStartInfo info = StartInfo(command);
        if (argument is not null)
            info.ArgumentList.Add(argument);
        if (repair is Repair action)
            info.ArgumentList.Add(action.ToString());
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        using var process = Process.Start(info);
        if (process is null)
            return null;

        Task<string> output = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource();
        if (repair is null && !printJob)
            timeout.CancelAfter(ReadTimeout);

        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            return null;
        }

        string json = await output;
        return process.ExitCode == 0 ? JsonSerializer.Deserialize<WorkerReply>(json) : null;
    }

    public static async Task<ActionResult> RunElevatedRepairAsync(Target target, Repair action)
    {
        bool networkAction = action is Repair.EnableSmbClient or Repair.EnableSmbServer or Repair.SecureHostSharing;
        bool hostAction = action is Repair.StartHostSpooler or Repair.StartLanmanServer or Repair.RestoreRpcPrivacy;
        ProcessStartInfo info = StartInfo(networkAction ? "--network-repair" : hostAction ? "--host-repair" : "--start-spooler");
        if (hostAction || networkAction)
        {
            info.ArgumentList.Add(target.Path);
            info.ArgumentList.Add(action.ToString());
        }
        string subject = action switch
        {
            Repair.StartLanmanServer => "the Server service (LanmanServer)",
            Repair.RestoreRpcPrivacy => SharingSettings.PrivacyValue,
            Repair.EnableSmbClient => "Client for Microsoft Networks",
            Repair.EnableSmbServer => "File and Printer Sharing for Microsoft Networks",
            Repair.SecureHostSharing => "the network type and Windows Firewall",
            _ => "the Print Spooler"
        };
        info.UseShellExecute = true;
        info.Verb = "runas";
        try
        {
            using var process = Process.Start(info);
            if (process is null)
                return new("Windows did not start the elevated helper.");
            await process.WaitForExitAsync();
            if (action == Repair.SecureHostSharing)
            {
                if (process.ExitCode == 50)
                    return new("Windows must report one active Public or Private network to apply this repair.", Code: 50);
                if (process.ExitCode == 1260)
                    return new("The active Windows firewall policy did not verify the required Private printing rules or firewall state.", Code: 1260);
            }
            if (process.ExitCode != 0)
                return new($"Windows did not verify the requested change to {subject}.", Code: process.ExitCode);
            return action switch
            {
                Repair.RestoreRpcPrivacy => new("Windows read back RpcAuthnLevelPrivacyEnabled = 1. Restart the Spooler or host during maintenance.", Verified: true),
                Repair.EnableSmbClient or Repair.EnableSmbServer => new($"Windows confirms {subject} is turned on for the active adapters.", Verified: true),
                Repair.SecureHostSharing => new("Windows confirms the network is Private, File and Printer Sharing is allowed and the firewall is on.", Verified: true),
                _ => new($"Windows confirms {subject} is running.", Verified: true)
            };
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            return new("Elevation was cancelled. The requested elevated action was not applied.", Code: error.NativeErrorCode);
        }
    }

    public static async Task<Report> CollectAsync(Target target, bool compareLocalCache = false)
    {
        Machine machine = Machine.Read();
        Task<NetworkEvidence> network = ProbeNetworkAsync(target.Host);
        Task<QueueProbe> queue = Task.Run(() => NativePrinting.ReadQueue(target.Path, live: true));
        Task<AdvancedSharing> advanced = Task.Run(() => SharingDiagnostics.CollectAsync(target, queue));
        Task<(PrintEvent[] Events, bool Available)> events = Task.Run(() => ReadEvents(target.Path, target.Share, isShared: true));
        Task<Inventory> inventory = Task.Run(NativePrinting.ReadInventory);
        Task<SharingEvidence> sharing = Task.Run(() => SharingSettings.ReadAsync(target));
        Task<ConnectivityEvidence> connectivity = Task.Run(() => Connectivity.CollectAsync(target));

        // Preserve the transport and service evidence when the Windows print probe stalls.
        Observation? live = await Task.WhenAny(queue, Task.Delay(TimeSpan.FromSeconds(10))) == queue && queue.IsCompletedSuccessfully
            ? (await queue).Observation() : null;
        await Task.WhenAny(Task.WhenAll(events, inventory, sharing, advanced, connectivity), Task.Delay(TimeSpan.FromSeconds(10)));
        (PrintEvent[] Events, bool Available) printEvents = events.IsCompletedSuccessfully ? events.Result : ([], false);
        Inventory? current = inventory.IsCompletedSuccessfully ? inventory.Result : null;
        Observation? cached = null;
        if (compareLocalCache && live is { Code: 0, DetailsError: 0, Jobs: 0 }
            && current is { InventoryError: 0, DefaultError: 0 } && current.Connections.Any(name => Diagnose.Same(name, target.Path)))
        {
            Task<Observation> clientCache = Task.Run(() => NativePrinting.Inspect(target.Path, live: false));
            if (await Task.WhenAny(clientCache, Task.Delay(800)) == clientCache && clientCache.IsCompletedSuccessfully)
                cached = await clientCache;
        }
        return new(machine, current, await network, live,
            printEvents.Events, printEvents.Available, live is null ? "Live queue probe timed out." : null, ReadPolicy(),
            sharing.IsCompletedSuccessfully ? sharing.Result : null, advanced.IsCompletedSuccessfully ? advanced.Result : null, cached,
            connectivity.IsCompletedSuccessfully ? connectivity.Result : null);
    }

    public static async Task<ConsoleSnapshot> ScanConsoleAsync()
    {
        Machine machine = Machine.Read();
        Task<HostSpooler> spooler = Task.Run(NativePrinting.ReadHostSpooler);
        Task<Catalog<QueueEntry>> local = Task.Run(NativePrinting.LocalQueues);
        Task<Catalog<QueueEntry>> shared = Task.Run(NativePrinting.SharedConnections);
        Task<Catalog<DriverEntry>> drivers = Task.Run(NativePrinting.InstalledDrivers);
        Task<Catalog<PortEntry>> ports = Task.Run(NativePrinting.InstalledPorts);
        Task<PrintPolicy?> policy = Task.Run(ReadPolicy);

        await Task.WhenAny(Task.WhenAll(spooler, local, shared, drivers, ports, policy),
            Task.Delay(TimeSpan.FromSeconds(12)));
        return new(DateTimeOffset.Now, machine, ReadCompleted(spooler), ReadCatalog(local), ReadCatalog(shared),
            ReadCatalog(drivers), ReadCatalog(ports), ReadCompleted(policy));

        static T? ReadCompleted<T>(Task<T> task) where T : class? =>
            task.IsCompletedSuccessfully ? task.Result : null;

        static Catalog<T> ReadCatalog<T>(Task<Catalog<T>> task) => task.IsCompletedSuccessfully
            ? task.Result
            : new([], task.IsFaulted ? (task.Exception?.GetBaseException() as Win32Exception)?.NativeErrorCode ?? 13 : 1460);
    }

    public static async Task<ConsoleCheck> CheckConsoleQueueAsync(string name)
    {
        QueueProbe probe = NativePrinting.CheckInstalledQueue(name);
        Target? shared = Target.Parse(name);
        Task<(PrintEvent[] Events, bool Available)> history =
            Task.Run(() => ReadEvents(name, shared?.Share ?? probe.Details?.Share, shared is not null));
        (PrintEvent[] events, bool available) =
            await Task.WhenAny(history, Task.Delay(TimeSpan.FromSeconds(2))) == history ? await history : ([], false);
        return new(DateTimeOffset.Now, probe, events, available);
    }

    public static ActionResult Apply(Target target, Repair action) => WithPrintLock(() => action switch
    {
        Repair.Connect => NativePrinting.Connect(target),
        Repair.Reconnect => NativePrinting.Reconnect(target),
        Repair.SetDefault => NativePrinting.SetDefault(target),
        Repair.TestPage => NativePrinting.PrintTestPage(target),
        _ => new("The requested repair is unavailable.")
    });

    public static ActionResult WithPrintLock(Func<ActionResult> apply)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        using var mutex = new Mutex(false, $@"Local\Kumpuni-print-{identity.User?.Value ?? "current-user"}");
        bool acquired;

        try { acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }

        if (!acquired)
            return new("Another Kumpuni repair is still running for this Windows user. Wait for it to finish, then diagnose again.");

        try { return apply(); }
        finally { mutex.ReleaseMutex(); }
    }

    public static async Task<PrinterEndpointStatus> ProbePrinterEndpointAsync(string host, int port)
    {
        IPAddress[]? addresses = await ResolveHostAsync(host);
        if (addresses is null)
            return PrinterEndpointStatus.NameResolutionTimedOut;
        if (addresses.Length == 0)
            return PrinterEndpointStatus.NameNotResolved;

        return await ProbePortAsync(addresses, port) ? PrinterEndpointStatus.Reachable : PrinterEndpointStatus.Unreachable;
    }

    private static async Task<NetworkEvidence> ProbeNetworkAsync(string host)
    {
        IPAddress[]? addresses = await ResolveHostAsync(host);
        if (addresses is not { Length: > 0 })
            return new(addresses is null ? null : false, null, null);

        Task<bool> rpc = ProbePortAsync(addresses, 135);
        Task<bool> smb = ProbePortAsync(addresses, 445);
        return new(true, await rpc, await smb);
    }

    private static async Task<IPAddress[]?> ResolveHostAsync(string host)
    {
        if (IPAddress.TryParse(host, out IPAddress? address))
            return [address];

        using var timeout = new CancellationTokenSource(NetworkTimeout);
        try { return await Dns.GetHostAddressesAsync(host, timeout.Token); }
        catch (ArgumentException) { return []; }
        catch (SocketException) { return []; }
        catch (OperationCanceledException) { return null; }
    }

    private static async Task<bool> ProbePortAsync(IPAddress[] addresses, int port)
    {
        using var timeout = new CancellationTokenSource(NetworkTimeout);
        try
        {
            using var client = new TcpClient(Socket.OSSupportsIPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
            if (Socket.OSSupportsIPv6)
                client.Client.DualMode = true;
            await client.ConnectAsync(addresses, port, timeout.Token);
            return true;
        }
        catch (SocketException) { return false; }
        catch (OperationCanceledException) { return false; }
    }

    private static (PrintEvent[], bool) ReadEvents(string name, string? share, bool isShared)
    {
        const string channel = "Microsoft-Windows-PrintService/Admin";
        const string filter = "*[System[TimeCreated[timediff(@SystemTime) <= 1800000]]]";
        try
        {
            var query = new EventLogQuery(channel, PathType.LogName, filter) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            var evidence = new List<PrintEvent>();

            for (int i = 0; i < 60; i++)
            {
                using EventRecord? entry = reader.ReadEvent(TimeSpan.FromMilliseconds(150));
                if (entry is null)
                    break;

                bool matches = entry.Properties.Any(property => property.Value is string value
                    && (Diagnose.Same(value, name)
                        || (share is not null && Diagnose.Same(value, share))
                        || (isShared && value.Contains(name, StringComparison.OrdinalIgnoreCase))));
                if (!matches)
                    continue;

                evidence.Add(new(entry.Id, entry.TimeCreated, entry.Id is 215 or 216));
            }

            return (evidence.ToArray(), true);
        }
        catch (EventLogException) { return ([], false); }
        catch (UnauthorizedAccessException) { return ([], false); }
        catch (SecurityException) { return ([], false); }
    }

    internal static PrintPolicy? ReadPolicy()
    {
        const string printers = @"SOFTWARE\Policies\Microsoft\Windows NT\Printers";
        try
        {
            using var protectedPrint = Registry.LocalMachine.OpenSubKey($@"{printers}\WPP");
            using var pointAndPrint = Registry.LocalMachine.OpenSubKey($@"{printers}\PointAndPrint");
            using var rpc = Registry.LocalMachine.OpenSubKey($@"{printers}\RPC");
            return new(
                ReadSwitch(protectedPrint?.GetValue("WindowsProtectedPrintGroupPolicyState")),
                ReadSwitch(pointAndPrint?.GetValue("RestrictDriverInstallationToAdministrators")),
                rpc?.GetValue("RpcUseNamedPipeProtocol") switch { 0 => "TCP", 1 => "Named pipes", _ => null });
        }
        catch (UnauthorizedAccessException) { return null; }
        catch (SecurityException) { return null; }

        static bool? ReadSwitch(object? value) => value switch { 0 => false, 1 => true, _ => null };
    }

    internal static async Task<int> RunTechnicianActionAsync(TechnicianAction action, string[] arguments)
    {
        ProcessStartInfo info = StartInfo("--technician-action");
        info.UseShellExecute = true;
        info.Verb = "runas";
        info.ArgumentList.Add(action.ToString());
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);
        try
        {
            using Process? process = Process.Start(info);
            if (process is null)
                return 13;
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch (Win32Exception exception) { return exception.NativeErrorCode; }
        catch (InvalidOperationException) { return 13; }
    }

    internal static int ApplyTechnicianAction(TechnicianAction action, string[] arguments)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return 5;
        using var gate = new Mutex(false, @"Global\Kumpuni-local-technician-repair");
        bool acquired;
        try { acquired = gate.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
            return 170;
        try
        {
            return (action, arguments) switch
            {
                (TechnicianAction.RegisterDriver, [string inf, string model]) => DriverRegistration.Register(inf, model),
                (TechnicianAction.ShareQueue, [string name, string share]) => NativePrinting.PublishQueue(name, share),
                (TechnicianAction.GrantPrint, [string name, string account]) => NativePrinting.GrantQueuePrint(name, account),
                (TechnicianAction.ResumeJob or TechnicianAction.RestartJob or TechnicianAction.CancelJob,
                    [string name, string number, string stamp]) when uint.TryParse(number, out uint id) && id > 0
                    && stamp.Length == 17 && stamp.All(char.IsAsciiDigit) => NativePrinting.ChangeQueueJob(name, id, stamp, action switch
                    {
                        TechnicianAction.ResumeJob => PrintJobAction.Resume,
                        TechnicianAction.RestartJob => PrintJobAction.Restart,
                        _ => PrintJobAction.Cancel
                    }),
                _ => 87
            };
        }
        finally { gate.ReleaseMutex(); }
    }

    internal static ProcessStartInfo ElevatedDriverStageInfo(string path)
    {
        ProcessStartInfo info = StartInfo("--stage-driver");
        info.UseShellExecute = true;
        info.Verb = "runas";
        info.ArgumentList.Add(path);
        return info;
    }

    private static ProcessStartInfo StartInfo(string command)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unavailable.");
        var info = new ProcessStartInfo(executable);
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        info.ArgumentList.Add(command);
        return info;
    }
}
