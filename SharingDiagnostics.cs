using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Kumpuni;

internal sealed record RuleCounts(int Allow, int Block, int Disabled);
internal sealed record FirewallRules(string Store, int ActiveStoreError, int Profiles,
    RuleCounts Smb, RuleCounts RpcMapper, RuleCounts PrintRpc, RuleCounts DiscoveryInbound, RuleCounts DiscoveryOutbound,
    RuleCounts? CustomInbound, int? CustomRulesError);
internal sealed record SpoolStorage(string Source, int FolderError, ulong? FreeBytes, ulong? TotalBytes, int VolumeError);
internal sealed record QueuePermissions(int Error, string? Dacl, uint? AceCount, int ManageError);
internal sealed record QueueJobs(int Error, int Sampled, int Blocked, int Paused, int Printing, bool SampleLimitReached);
internal sealed record QueueAdministration(QueuePermissions Permissions, QueueJobs Jobs);
internal sealed record DriverReadiness(int InventoryError, int? MatchingDriverCount, uint? DriverModel, string? Version);
internal sealed record PrintFrameworkFile(string Name, string? Version, int Error);
internal sealed record AdvancedSharing(ServiceReading DiscoveryProvider, ServiceReading DiscoveryPublication,
    FirewallRules? FirewallRules, int FirewallReadError, SpoolStorage Spool, QueueAdministration? Queue, DriverReadiness? Driver,
    PrintFrameworkFile[]? Framework = null);

internal static class SharingDiagnostics
{
    private const string PrinterRegistry = @"SYSTEM\CurrentControlSet\Control\Print\Printers";
    private const int JobLimit = 128;
    private const int DriveFixed = 3;

    public static async Task<AdvancedSharing> CollectAsync(Target target, Task<QueueProbe> live)
    {
        Task<(FirewallRules? Rules, int Error)> firewall = ReadFirewallRulesAsync();
        ServiceReading discovery = NativePrinting.ReadService("fdPHost");
        ServiceReading publication = NativePrinting.ReadService("FDResPub");

        Task<(SpoolStorage Storage, QueueAdministration? Queue, DriverReadiness? Driver)> printer =
            Task.Run(async () => await ReadPrinterAsync(target, live));
        (FirewallRules? rules, int firewallError) = await firewall;
        (SpoolStorage storage, QueueAdministration? queue, DriverReadiness? driver) =
            await Task.WhenAny(printer, Task.Delay(TimeSpan.FromSeconds(2))) == printer && printer.IsCompletedSuccessfully
            ? await printer : (ReadSpoolStorage(null), null, null);
        return new(discovery, publication, rules, firewallError, storage, queue, driver, ReadPrintFramework());
    }

    // Shared Type 3 print framework files. Differences between client and host were reported after the September 2026 updates.
    internal static readonly string[] FrameworkFiles = ["UNIDRV.DLL", "UNIDRVUI.DLL", "UNIRES.DLL", "PSCRIPT5.DLL", "PS5UI.DLL"];

    internal static PrintFrameworkFile[] ReadPrintFramework()
    {
        string? folder = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64", Architecture.Arm64 => "ARM64", Architecture.X86 => "W32X86", _ => null
        };
        if (folder is null)
            return [];
        string directory = Path.Combine(Environment.SystemDirectory, "spool", "drivers", folder, "3");
        return FrameworkFiles.Select(name =>
        {
            string path = Path.Combine(directory, name);
            try
            {
                if (!File.Exists(path))
                    return new PrintFrameworkFile(name, null, 2);
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                return info is { FileMajorPart: 0, FileMinorPart: 0, FileBuildPart: 0, FilePrivatePart: 0 }
                    ? new PrintFrameworkFile(name, null, 1813)
                    : new PrintFrameworkFile(name, $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}", 0);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
            {
                return new PrintFrameworkFile(name, null, error is IOException ? 13 : 5);
            }
        }).ToArray();
    }

    private static async Task<(SpoolStorage, QueueAdministration?, DriverReadiness?)> ReadPrinterAsync(
        Target target, Task<QueueProbe> live)
    {
        QueueEntry? hosted = SharingSettings.IsLocalHost(target)
            ? NativePrinting.LocalQueues().Items.FirstOrDefault(queue => queue.Scope == QueueScope.SharedHere
                && Diagnose.Same(queue.Details?.Share, target.Share))
            : null;
        SpoolStorage storage = ReadSpoolStorage(hosted?.Name);
        QueueAdministration? queue = hosted is null ? null : NativePrinting.ReadQueueAdministration(hosted.Name, JobLimit);
        QueueProbe? probe = hosted is null
            && await Task.WhenAny(live, Task.Delay(TimeSpan.FromMilliseconds(1500))) == live
            && live.IsCompletedSuccessfully ? await live : null;
        string? driverName = hosted?.Details?.Driver ?? probe?.Details?.Driver;
        if (driverName is null)
            return (storage, queue, null);

        Catalog<DriverEntry> catalog = NativePrinting.InstalledDrivers();
        DriverEntry[] matching = catalog.Items.Where(driver => Diagnose.Same(driver.Name, driverName)
            && Diagnose.Same(driver.Environment, NativeEnvironment())).ToArray();
        DriverEntry? single = matching.Length == 1 ? matching[0] : null;
        return (storage, queue, new(catalog.Error, catalog.Error == 0 ? matching.Length : null, single?.Model,
            Version.TryParse(single?.Version, out Version? version) ? version.ToString() : null));
    }

    internal static string? NativeEnvironment() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "Windows x64",
        Architecture.Arm64 => "Windows ARM64",
        Architecture.X86 => "Windows NT x86",
        _ => null
    };

    private static SpoolStorage ReadSpoolStorage(string? queueName)
    {
        string source = "SystemDefault";
        string? path = null;
        try
        {
            if (queueName is not null && !queueName.Contains('\\'))
            {
                using RegistryKey? queue = Registry.LocalMachine.OpenSubKey($@"{PrinterRegistry}\{queueName}");
                path = queue?.GetValue("SpoolDirectory") as string;
                if (!string.IsNullOrWhiteSpace(path))
                    source = "QueueOverride";
            }
            if (string.IsNullOrWhiteSpace(path))
            {
                using RegistryKey? printers = Registry.LocalMachine.OpenSubKey(PrinterRegistry);
                path = printers?.GetValue("DefaultSpoolDirectory") as string;
                if (!string.IsNullOrWhiteSpace(path))
                    source = "HostOverride";
            }
        }
        catch (Exception error) when (error is UnauthorizedAccessException or SecurityException or IOException)
        {
            return new("Unreadable", error is IOException ? 13 : 5, null, null, 0);
        }

        path = string.IsNullOrWhiteSpace(path) ? Path.Combine(Environment.SystemDirectory, "spool", "PRINTERS")
            : Environment.ExpandEnvironmentVariables(path);
        if (!IsLocalFilePath(path))
            return new(source, 50, null, null, 50);

        int folderError = DirectoryReadError(path);
        var mount = new StringBuilder(1024);
        if (!GetVolumePathNameW(path, mount, (uint)mount.Capacity))
            return new(source, folderError, null, null, Marshal.GetLastPInvokeError());
        string volume = mount.ToString();
        if (GetDriveTypeW(volume) != DriveFixed)
            return new(source, folderError, null, null, 50);
        return GetDiskFreeSpaceExW(volume, out _, out ulong capacity, out ulong free)
            ? new(source, folderError, free, capacity, 0)
            : new(source, folderError, null, null, Marshal.GetLastPInvokeError());
    }

    private static int DirectoryReadError(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.Directory) ? 0 : 267; }
        catch (UnauthorizedAccessException) { return 5; }
        catch (SecurityException) { return 5; }
        catch (FileNotFoundException) { return 3; }
        catch (DirectoryNotFoundException) { return 3; }
        catch (ArgumentException) { return 123; }
        catch (IOException error) { return error.HResult & 0xFFFF; }
    }

    public static bool IsLocalFilePath(string path)
    {
        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\'
            || path.AsSpan(3).Contains(':') || path.Any(char.IsControl) || path.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0)
            return false;
        return GetDriveTypeW(path[..3]) == DriveFixed;
    }

    private static async Task<(FirewallRules?, int)> ReadFirewallRulesAsync()
    {
        (string? json, int error) = await PowerShellJson.RunAsync(FirewallScript, TimeSpan.FromSeconds(5));
        if (json is null)
            return (null, error);
        try
        {
            FirewallRules? rules = JsonSerializer.Deserialize<FirewallRules>(json);
            return (rules, rules is null ? 13 : 0);
        }
        catch (JsonException) { return (null, 13); }
    }

    private const string FirewallScript = """
        $ErrorActionPreference = 'Stop'
        $sharing = '@FirewallAPI.dll,-28502'
        $discovery = '@FirewallAPI.dll,-32752'
        function New-Counters { @{ Allow = 0; Block = 0; Disabled = 0 } }
        function Update-Count($kind, $enabled, $isAllow) {
            if (!$enabled) { $result[$kind].Disabled++ }
            elseif ($isAllow) { $result[$kind].Allow++ }
            else { $result[$kind].Block++ }
        }

        try {
            $policy = New-Object -ComObject HNetCfg.FwPolicy2
            $mask = $policy.CurrentProfileTypes -band 7
            $result = @{
                Store = 'LocalRules'; ActiveStoreError = 5; Profiles = $mask
                Smb = New-Counters; RpcMapper = New-Counters; PrintRpc = New-Counters
                DiscoveryInbound = New-Counters; DiscoveryOutbound = New-Counters
                CustomInbound = $null; CustomRulesError = $null
            }
            $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
            $rules = $null
            if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
                try {
                    $rules = @(Get-NetFirewallRule -PolicyStore ActiveStore | Where-Object { $_.Group -in $sharing,$discovery })
                    $result.Store = 'ActiveStore'
                    $result.ActiveStoreError = 0
                } catch {
                    $result.ActiveStoreError = if ($_.CategoryInfo.Category -eq 'PermissionDenied') { 5 } else { 13 }
                }
            }
            if ($result.Store -eq 'ActiveStore') {
                foreach ($rule in $rules) {
                    $profiles = [int]$rule.Profile
                    if ($profiles -ne 0 -and ($profiles -band $mask) -eq 0) { continue }
                    $inbound = $rule.Direction -eq 'Inbound'
                    $key = if ($rule.Group -eq $discovery) {
                        if ($inbound) { 'DiscoveryInbound' } else { 'DiscoveryOutbound' }
                    } elseif ($inbound) {
                        if ($rule.Name -like 'FPS-SMB-In-TCP*') { 'Smb' }
                        elseif ($rule.Name -like 'FPS-RPCSS-In-TCP*') { 'RpcMapper' }
                        elseif ($rule.Name -like 'FPS-SpoolSvc-In-TCP*') { 'PrintRpc' }
                    }
                    if ($key) { Update-Count $key ($rule.Enabled -eq 'True') ($rule.Action -eq 'Allow') }
                }
                try {
                    $ports = @('135','445','RPC','RPC-EPMap')
                    $fixed = Get-ItemPropertyValue -LiteralPath 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Printers\RPC' -Name RpcTcpPort -ErrorAction SilentlyContinue
                    if ($fixed -gt 0 -and $fixed -le 65535) { $ports += [string]$fixed }
                    $filters = @(Get-NetFirewallPortFilter -PolicyStore ActiveStore -Protocol TCP |
                        Where-Object { @($_.LocalPort | Where-Object { $_ -in $ports }).Count -gt 0 })
                    $result.CustomInbound = New-Counters
                    if ($filters.Count -gt 0) {
                        $filters | Get-NetFirewallRule -PolicyStore ActiveStore | Where-Object {
                            $_.Group -notin $sharing,$discovery -and $_.Direction -eq 'Inbound' -and
                            ([int]$_.Profile -eq 0 -or ([int]$_.Profile -band $mask) -ne 0)
                        } | ForEach-Object { Update-Count 'CustomInbound' ($_.Enabled -eq 'True') ($_.Action -eq 'Allow') }
                    }
                    $result.CustomRulesError = 0
                } catch {
                    $result.CustomInbound = $null
                    $result.CustomRulesError = if ($_.CategoryInfo.Category -eq 'PermissionDenied') { 5 } else { 13 }
                }
            } else {
                foreach ($rule in $policy.Rules) {
                    if (($rule.Profiles -band $mask) -eq 0) { continue }
                    $inbound = $rule.Direction -eq 1
                    $key = if ($rule.Grouping -eq $discovery) {
                        if ($inbound) { 'DiscoveryInbound' } else { 'DiscoveryOutbound' }
                    } elseif ($rule.Grouping -eq $sharing -and $inbound -and $rule.Protocol -eq 6) {
                        $ports = ([string]$rule.LocalPorts) -split ','
                        if ($ports -contains '445') { 'Smb' }
                        elseif ($ports -contains '135' -or $ports -contains 'RPC-EPMap') { 'RpcMapper' }
                        elseif ($rule.ServiceName -eq 'Spooler' -and $ports -contains 'RPC') { 'PrintRpc' }
                    }
                    if ($key) { Update-Count $key $rule.Enabled ($rule.Action -eq 1) }
                }
            }
            $result | ConvertTo-Json -Compress -Depth 3
        } catch { exit 13 }
        """;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetDriveTypeW(string root);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool GetVolumePathNameW(string path, StringBuilder volumePath, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceExW(string path, out ulong available, out ulong capacity, out ulong free);
}
