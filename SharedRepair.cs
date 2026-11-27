using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using Microsoft.Win32;

namespace Kumpuni;

internal sealed record RegistrySetting(int? Value, int Error = 0);
internal sealed record ServiceReading(uint? State, int? Start, int Error);
internal sealed record FirewallReading(int ActiveProfiles, int EnabledLocally);
internal sealed record SharingEvidence(bool LocalHost, bool? SharedQueuePresent, int? SharedQueueError,
    ServiceReading Spooler, ServiceReading Server, ServiceReading FirewallService, FirewallReading? Firewall,
    RegistrySetting Privacy, RegistrySetting RpcProtocols, RegistrySetting RpcTcpPort, RegistrySetting RpcKerberos,
    RegistrySetting Smb1, RegistrySetting Smb2, RegistrySetting ServerSigning, RegistrySetting ClientSigning,
    RegistrySetting GuestPolicy, RegistrySetting RpcAuthentication, RegistrySetting RemoteSpooler,
    RegistrySetting? LocalAccountModel = null);

internal static class SharingSettings
{
    private const string Print = @"SYSTEM\CurrentControlSet\Control\Print";
    private const string PrinterPolicy = @"SOFTWARE\Policies\Microsoft\Windows NT\Printers";
    private const string Rpc = PrinterPolicy + @"\RPC";
    private const string SmbServer = @"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters";
    private const string SmbClient = @"SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters";
    private const string SmbClientPolicy = @"SOFTWARE\Policies\Microsoft\Windows\LanmanWorkstation";
    internal const string PrivacyValue = "RpcAuthnLevelPrivacyEnabled";

    public static bool IsLocalHost(Target target)
    {
        string host = target.Host.TrimEnd('.');
        if (Diagnose.Same(host, "localhost") || Diagnose.Same(host, Environment.MachineName))
            return true;
        try
        {
            IPGlobalProperties identity = IPGlobalProperties.GetIPGlobalProperties();
            return !string.IsNullOrEmpty(identity.DomainName)
                && Diagnose.Same(host, $"{identity.HostName}.{identity.DomainName}");
        }
        catch (NetworkInformationException) { return false; }
    }

    public static async Task<SharingEvidence> ReadAsync(Target target)
    {
        bool local = IsLocalHost(target);
        ServiceReading spooler = NativePrinting.ReadService("Spooler");
        Task<(bool? Present, int? Error)>? share = local && spooler.State == 4 ? Task.Run(() => CheckLocalShare(target)) : null;
        var evidence = new SharingEvidence(local, null, null, spooler, NativePrinting.ReadService("LanmanServer"),
            NativePrinting.ReadService("MpsSvc"), ReadFirewall(),
            ReadSetting(Print, PrivacyValue), ReadSetting(Rpc, "RpcProtocols"), ReadSetting(Rpc, "RpcTcpPort"),
            ReadSetting(Rpc, "ForceKerberosForRpc"), ReadSetting(SmbServer, "SMB1"), ReadSetting(SmbServer, "SMB2"),
            ReadSetting(SmbServer, "RequireSecuritySignature"), ReadSetting(SmbClient, "RequireSecuritySignature"),
            ReadSetting(SmbClientPolicy, "AllowInsecureGuestAuth"), ReadSetting(Rpc, "RpcAuthentication"),
            ReadSetting(PrinterPolicy, "RegisterSpoolerRemoteRpcEndPoint"), local ? ReadLocalAccountModel() : null);
        if (share is null)
            return evidence;
        if (await Task.WhenAny(share, Task.Delay(TimeSpan.FromSeconds(2))) != share)
            return evidence with { SharedQueueError = 1460 };
        if (share.IsFaulted)
            return evidence with { SharedQueueError = 13 };

        var (present, error) = await share;
        return evidence with { SharedQueuePresent = present, SharedQueueError = error };
    }

    internal static RegistrySetting ReadLocalAccountModel() =>
        ReadSetting(@"SYSTEM\CurrentControlSet\Control\Lsa", "ForceGuest");

    internal static SmbClientSettings ReadConfiguredSmbClient() => new(
        Flag(ReadSetting(SmbClient, "RequireSecuritySignature")),
        ConfiguredFlag("AllowInsecureGuestAuth"), ConfiguredFlag("RequireEncryption"), RegistryOnly: true);

    private static bool? ConfiguredFlag(string name)
    {
        RegistrySetting policy = ReadSetting(SmbClientPolicy, name);
        return Flag(policy is { Error: 0, Value: null } ? ReadSetting(SmbClient, name) : policy);
    }

    private static bool? Flag(RegistrySetting setting) => setting switch
    {
        { Error: 0, Value: 1 } => true,
        { Error: 0, Value: 0 } => false,
        _ => null
    };

    public static (bool? Present, int? Error) CheckLocalShare(Target target)
    {
        Catalog<QueueEntry> queues = NativePrinting.LocalQueues();
        if (queues.Error != 0)
            return (null, queues.Error);
        return (queues.Items.Any(q => q.Scope == QueueScope.SharedHere && Diagnose.Same(q.Details?.Share, target.Share)), 0);
    }

    public static bool ReadNamedPipesConfiguration() => NamedPipesConfigured(ReadSetting(Rpc, "RpcProtocols"));

    public static bool RemoteSpoolerBlocked(RegistrySetting setting) => setting is { Value: 2, Error: 0 };

    public static bool RemoteSpoolerMayAcceptClients() =>
        ReadSetting(PrinterPolicy, "RegisterSpoolerRemoteRpcEndPoint") is { Value: null or 1, Error: 0 };

    public static bool NamedPipesConfigured(RegistrySetting setting) => setting is { Value: 3 or 7, Error: 0 };

    public static int RestorePrivacy(Target target)
    {
        if (!IsLocalHost(target) || !RemoteSpoolerMayAcceptClients() || CheckLocalShare(target).Present != true)
            return 1801;

        try
        {
            using RegistryKey? print = Registry.LocalMachine.OpenSubKey(Print, writable: true);
            if (print is null || print.GetValue(PrivacyValue) is not 0 || print.GetValueKind(PrivacyValue) != RegistryValueKind.DWord)
                return 1237;
            print.SetValue(PrivacyValue, 1, RegistryValueKind.DWord);
            return print.GetValue(PrivacyValue) is 1 ? 0 : 13;
        }
        catch (UnauthorizedAccessException) { return 5; }
        catch (SecurityException) { return 5; }
        catch (IOException) { return 13; }
    }

    private static RegistrySetting ReadSetting(string path, string name)
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(path);
            object? value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is null)
                return new(null);
            return value is int number && key!.GetValueKind(name) == RegistryValueKind.DWord ? new(number) : new(null, 13);
        }
        catch (UnauthorizedAccessException) { return new(null, 5); }
        catch (SecurityException) { return new(null, 5); }
        catch (IOException) { return new(null, 13); }
    }

    private static FirewallReading? ReadFirewall()
    {
        object? policy = null;
        try
        {
            Type? type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type is null || (policy = Activator.CreateInstance(type)) is null)
                return null;

            Type dispatch = policy.GetType();
            int active = (int)dispatch.InvokeMember("CurrentProfileTypes", BindingFlags.GetProperty, null, policy, null)! & 7;
            int enabled = 0;
            foreach (int profile in new[] { 1, 2, 4 })
            {
                if ((active & profile) != 0 &&
                    dispatch.InvokeMember("FirewallEnabled", BindingFlags.GetProperty, null, policy, [profile]) is true)
                    enabled |= profile;
            }
            return new(active, enabled);
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or
            TargetInvocationException or MissingMemberException or SecurityException)
        {
            return null;
        }
        finally
        {
            if (policy is not null && Marshal.IsComObject(policy))
                Marshal.ReleaseComObject(policy);
        }
    }
}

internal sealed record SharedRepairStep(Repair Action, DateTimeOffset StartedAt, DateTimeOffset FinishedAt,
    ActionResult Result, bool RecheckCompleted, bool? StateMatches);
internal sealed record SharedRepairRun(Report Initial, Report? Final, SharedRepairStep[] Steps, string Summary);

internal static class SharedRepair
{
    public static string SerializeReport(Target target, TaskKind task, uint? reported, Report report, Decision decision,
        ActionResult? lastResult, bool pageConfirmed, SharedRepairRun? run, IReadOnlyList<DriverDeploymentResult>? deployments = null)
    {
        var document = new
        {
            SchemaVersion = 2,
            ExportedAt = DateTimeOffset.UtcNow,
            Operation = task.ToString(),
            ReportedCode = reported,
            Evidence = RedactedEvidence(target, report),
            decision.Finding,
            decision.Owner,
            decision.Details,
            OfferedAction = decision.Action.ToString(),
            ActionResult = lastResult,
            PhysicalPageConfirmed = pageConfirmed,
            DriverDeployments = deployments ?? [],
            RepairRun = run is null ? null : new
            {
                run.Summary,
                Before = RedactedEvidence(target, run.Initial),
                After = run.Final is null ? null : RedactedEvidence(target, run.Final),
                Actions = run.Steps.Select(step => new
                {
                    Action = step.Action.ToString(), step.StartedAt, step.FinishedAt, step.Result,
                    step.RecheckCompleted, step.StateMatches
                })
            }
        };
        return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
    }

    private static object RedactedEvidence(Target target, Report report) => new
    {
        report.Machine, report.Network, report.Queue, report.Events, report.EventsAvailable, report.UnavailableReason,
        report.Policy, report.Sharing, report.Advanced, report.CachedQueue, report.Connectivity,
        Inventory = report.Inventory is not Inventory inventory ? null : new
        {
            inventory.InventoryError, inventory.DefaultError, inventory.SpoolerState, inventory.SpoolerError, inventory.AutomaticStart,
            ConnectionPresent = inventory.InventoryError == 0
                ? inventory.Connections.Any(value => Diagnose.Same(value, target.Path)) : (bool?)null,
            SelectedQueueIsDefault = inventory.DefaultError == 0 ? Diagnose.Same(inventory.Default, target.Path) : (bool?)null
        }
    };

    public static Repair Next(TaskKind task, Target target, Report report, uint? reported, bool restorePrivacy,
        IReadOnlySet<Repair> attempted, bool recoverStaleCache = false)
    {
        Repair next;
        if (report.Sharing is { LocalHost: true } host)
        {
            if (host.RemoteSpooler is not { Value: null or 1, Error: 0 })
                return Repair.None;
            next = host switch
            {
                { Spooler: { State: 1, Start: 2, Error: 0 } } => Repair.StartHostSpooler,
                { SharedQueuePresent: true, Server: { State: 1, Start: 2, Error: 0 } }
                    when SharingSettings.NamedPipesConfigured(host.RpcProtocols) => Repair.StartLanmanServer,
                { SharedQueuePresent: true } when report.Connectivity is { ServerBindingOff: true } => Repair.EnableSmbServer,
                { SharedQueuePresent: true } when Diagnose.Choose(task, target, report, reported).Action == Repair.SecureHostSharing
                    => Repair.SecureHostSharing,
                { SharedQueuePresent: true, Privacy: { Value: 0, Error: 0 } } when restorePrivacy => Repair.RestoreRpcPrivacy,
                _ => Repair.None
            };
        }
        else
        {
            if (report.Sharing is null && SharingSettings.IsLocalHost(target))
                return Repair.None;

            next = Diagnose.Choose(task, target, report, reported).Action;
            if (next == Repair.Reconnect && (reported is not (53 or 67 or 1801) || attempted.Contains(Repair.Connect)))
                next = Repair.None;
            if (recoverStaleCache && !attempted.Contains(Repair.Connect) && !attempted.Contains(Repair.Reconnect)
                && (next is Repair.None or Repair.SetDefault) && QuickRepair.HasStaleConnection(target, report))
                next = Repair.Reconnect;
        }

        return attempted.Contains(next) ? Repair.None : next;
    }

    private static bool? ReadBack(Repair action, Target target, Report report) => action switch
    {
        Repair.StartSpooler => report.Inventory?.SpoolerState is uint spooler ? spooler == 4 : null,
        Repair.StartHostSpooler => report.Sharing?.Spooler.State is uint hostSpooler ? hostSpooler == 4 : null,
        Repair.StartLanmanServer => report.Sharing?.Server.State is uint server ? server == 4 : null,
        Repair.RestoreRpcPrivacy => report.Sharing?.Privacy is { Error: 0 } privacy ? privacy.Value == 1 : null,
        Repair.EnableSmbClient => report.Connectivity?.Health is null ? null : !report.Connectivity.ClientBindingOff,
        Repair.EnableSmbServer => report.Connectivity?.Health is null ? null : !report.Connectivity.ServerBindingOff,
        Repair.SecureHostSharing => report.Connectivity?.Health is null || report.Sharing?.Firewall is not FirewallReading firewall
            ? null : report.Connectivity.Health.Categories is ["Private"] && firewall.ActiveProfiles == 2 && (firewall.EnabledLocally & 2) != 0,
        Repair.SignInToHost => report.Queue is { Code: 0 } || report.Connectivity?.Session is { Error: 0 } or { IpcError: 0 }
            ? true : report.Connectivity?.Session is { IpcError: int } ? false : null,
        Repair.Connect or Repair.Reconnect => report.Inventory is { InventoryError: 0 } inventory
            ? inventory.Connections.Any(connection => Diagnose.Same(connection, target.Path)) : null,
        Repair.SetDefault => report.Inventory is { DefaultError: 0 } defaults ? Diagnose.Same(defaults.Default, target.Path) : null,
        _ => null
    };

    public static async Task<SharedRepairRun> ExecuteAsync(TaskKind task, Target target, Report initial,
        uint? reported, bool restorePrivacy, Func<Repair, Task<ActionResult>> apply, Func<Task<Report?>> recheck,
        bool recoverStaleCache = false)
    {
        var steps = new List<SharedRepairStep>();
        var attempted = new HashSet<Repair>();
        Report? latest = initial;
        while (Next(task, target, latest, reported, restorePrivacy, attempted, recoverStaleCache) is Repair action && action != Repair.None)
        {
            attempted.Add(action);
            DateTimeOffset started = DateTimeOffset.UtcNow;
            ActionResult result = await apply(action);
            DateTimeOffset finished = DateTimeOffset.UtcNow;
            latest = await recheck();
            bool? stateMatches = latest is null ? null : ReadBack(action, target, latest);
            steps.Add(new(action, started, finished, result, latest is not null, stateMatches));
            if (latest is null)
                return new(initial, null, steps.ToArray(), "Windows did not return post-repair evidence. Check the action results before retrying.");
            if (!result.Verified)
                return new(initial, latest, steps.ToArray(), "A repair could not be verified. Review the action log and remaining finding.");
            if (stateMatches == false)
                return new(initial, latest, steps.ToArray(), "The follow-up check found a different state from the repair result. Review the before and after evidence.");
        }

        string summary = steps.Count == 0
            ? "No automatic action applied. Review the findings below."
            : "Repair run finished. Review the verified results and any remaining findings.";
        if (attempted.Contains(Repair.RestoreRpcPrivacy))
            summary = "RPC privacy was saved. Arrange a Spooler or device restart and verify printing from the client.";
        return new(initial, latest, steps.ToArray(), summary);
    }
}
