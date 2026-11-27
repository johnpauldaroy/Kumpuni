using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Kumpuni;

// Session-level evidence between this PC and the print host. TCP probes only prove a port answers;
// these checks show whether Windows can open a file-sharing session and talk to the host's print server.
internal sealed record SmbSessionCheck(int Error, int? PrinterShares, bool? SharePublished);
internal sealed record AddressProbe(string Family, bool Smb, bool RpcMapper);
internal sealed record AdapterBinding(string Adapter, bool? Client, bool? Server);
internal sealed record NetworkHealth(AdapterBinding[] Adapters, string[] Categories, string[] FirewallProducts, string[] AntivirusProducts);
internal sealed record ConnectivityEvidence(SmbSessionCheck? Session, int? PrintServerError, AddressProbe[] Addresses,
    NetworkHealth? Health, int HealthError, int[] SmbClientEvents)
{
    // Security products other than Microsoft Defender that report as a firewall or antivirus.
    public string[] ThirdPartySecurity => (Health?.FirewallProducts ?? []).Concat(Health?.AntivirusProducts ?? [])
        .Where(name => !name.Contains("Defender", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public bool ClientBindingOff => Health?.Adapters.Any(adapter => adapter.Client == false) == true;
    public bool ServerBindingOff => Health?.Adapters.Any(adapter => adapter.Server == false) == true;
    public bool PublicNetwork => Health?.Categories.Any(category => category == "Public") == true;
}

internal static class Connectivity
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(8);
    private const int SessionTimedOut = 1460;

    public static async Task<ConnectivityEvidence> CollectAsync(Target target)
    {
        Task<SmbSessionCheck?> session = WithTimeout(Task.Run(() => CheckSession(target)));
        Task<int?> printServer = WithTimeout(Task.Run(() => (int?)OpenPrintServer(target.Host)));
        Task<AddressProbe[]> addresses = ProbeAddressesAsync(target.Host);
        Task<(NetworkHealth? Health, int Error)> health = ReadHealthAsync();
        (NetworkHealth? Health, int Error) network = await health;
        SmbSessionCheck? smb = await session ?? new SmbSessionCheck(SessionTimedOut, null, null);
        int? rpc = await printServer ?? SessionTimedOut;
        return new(smb, rpc, await addresses, network.Health, network.Error, ReadSmbClientEvents());
    }

    private static async Task<T?> WithTimeout<T>(Task<T> work) =>
        await Task.WhenAny(work, Task.Delay(CheckTimeout)) == work && work.IsCompletedSuccessfully ? work.Result : default;

    // Equivalent of "net view \\HOST": opens an SMB session with the current user's credentials and lists shares.
    internal static SmbSessionCheck CheckSession(Target target)
    {
        int resume = 0;
        int code = NetShareEnum($@"\\{target.Host}", 1, out IntPtr buffer, -1, out int read, out _, ref resume);
        try
        {
            if (code is not (0 or 234) || buffer == IntPtr.Zero)
                return new(code, null, null);
            int printers = 0;
            bool published = false;
            int stride = Marshal.SizeOf<ShareInfo1>();
            for (int i = 0; i < read; i++)
            {
                var share = Marshal.PtrToStructure<ShareInfo1>(IntPtr.Add(buffer, i * stride));
                if ((share.Type & 0xFF) != 1)
                    continue;
                printers++;
                published |= Diagnose.Same(share.Name, target.Share);
            }
            return new(0, printers, published);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                NetApiBufferFree(buffer);
        }
    }

    // Equivalent of "Get-Printer -ComputerName HOST": opens the host's print server over print RPC.
    internal static int OpenPrintServer(string host)
    {
        var defaults = new PrinterDefaults { DesiredAccess = 0x2 };
        if (!OpenPrinterW($@"\\{host}", out IntPtr handle, in defaults))
            return Marshal.GetLastPInvokeError();
        ClosePrinter(handle);
        return 0;
    }

    // Probes IPv4 and IPv6 separately, so an answer on one family does not hide a blocked path on the other.
    private static async Task<AddressProbe[]> ProbeAddressesAsync(string host)
    {
        IPAddress[] addresses;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            addresses = IPAddress.TryParse(host, out IPAddress? literal) ? [literal] : await Dns.GetHostAddressesAsync(host, timeout.Token);
        }
        catch (Exception error) when (error is SocketException or ArgumentException or OperationCanceledException) { return []; }

        var probes = new List<AddressProbe>();
        foreach (AddressFamily family in new[] { AddressFamily.InterNetwork, AddressFamily.InterNetworkV6 })
        {
            IPAddress[] matching = addresses.Where(address => address.AddressFamily == family).ToArray();
            if (matching.Length == 0)
                continue;
            Task<bool> smb = ProbeAsync(matching, 445);
            Task<bool> rpc = ProbeAsync(matching, 135);
            probes.Add(new(family == AddressFamily.InterNetwork ? "IPv4" : "IPv6", await smb, await rpc));
        }
        return probes.ToArray();
    }

    private static async Task<bool> ProbeAsync(IPAddress[] addresses, int port)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            using var client = new TcpClient(addresses[0].AddressFamily);
            await client.ConnectAsync(addresses, port, timeout.Token);
            return true;
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException) { return false; }
    }

    internal static async Task<(NetworkHealth?, int)> ReadHealthAsync()
    {
        (string? json, int error) = await PowerShellJson.RunAsync(HealthScript, TimeSpan.FromSeconds(8));
        if (json is null)
            return (null, error);
        try
        {
            NetworkHealth? health = JsonSerializer.Deserialize<NetworkHealth>(json);
            return health is null ? (null, 13) : (health with
            {
                Adapters = health.Adapters ?? [], Categories = health.Categories ?? [],
                FirewallProducts = health.FirewallProducts ?? [], AntivirusProducts = health.AntivirusProducts ?? []
            }, 0);
        }
        catch (JsonException) { return (null, 13); }
    }

    // Event IDs only: the SMB client messages name the server, which reports must not contain.
    private static int[] ReadSmbClientEvents()
    {
        var ids = new List<int>();
        foreach (string channel in new[] { "Microsoft-Windows-SMBClient/Connectivity", "Microsoft-Windows-SMBClient/Security" })
        {
            try
            {
                var query = new EventLogQuery(channel, PathType.LogName, "*[System[TimeCreated[timediff(@SystemTime) <= 1800000]]]")
                    { ReverseDirection = true };
                using var reader = new EventLogReader(query);
                for (int i = 0; i < 20; i++)
                {
                    using EventRecord? entry = reader.ReadEvent(TimeSpan.FromMilliseconds(150));
                    if (entry is null)
                        break;
                    ids.Add(entry.Id);
                }
            }
            catch (Exception error) when (error is EventLogException or UnauthorizedAccessException or SecurityException) { }
        }
        return ids.Distinct().ToArray();
    }

    // Elevated network repairs. Each one changes only what is needed and reads the result back.
    public static int ApplyNetworkRepair(Target target, Repair action)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return 5;
        string? script = action switch
        {
            Repair.EnableSmbClient => BindingScript("ms_msclient"),
            Repair.EnableSmbServer when SharingSettings.IsLocalHost(target) => BindingScript("ms_server"),
            Repair.SecureHostSharing when SharingSettings.IsLocalHost(target) && SharingSettings.CheckLocalShare(target).Present == true
                => SecureSharingScript,
            _ => null
        };
        if (script is null)
            return 5;
        (string? output, int error) = PowerShellJson.RunAsync(script, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        return error != 0 ? error : output?.Trim() == "\"ok\"" ? 0 : 13;
    }

    private static string BindingScript(string component) => $$"""
        $ErrorActionPreference = 'Stop'
        $up = @(Get-NetAdapter | Where-Object Status -eq 'Up' | ForEach-Object Name)
        Get-NetAdapterBinding -ComponentID '{{component}}' | Where-Object { $_.Name -in $up -and -not $_.Enabled } |
            ForEach-Object { Enable-NetAdapterBinding -Name $_.Name -ComponentID '{{component}}' }
        $off = @(Get-NetAdapterBinding -ComponentID '{{component}}' | Where-Object { $_.Name -in $up -and -not $_.Enabled })
        if ($off.Count -eq 0) { 'ok' | ConvertTo-Json } else { exit 13 }
        """;

    // Rules first, then the Private category, then the firewall: clients keep access at every step.
    private const string SecureSharingScript = """
        $ErrorActionPreference = 'Stop'
        Get-NetFirewallRule -Group '@FirewallAPI.dll,-28502' | Where-Object {
            $_.Direction -eq 'Inbound' -and ([int]$_.Profile -eq 0 -or ([int]$_.Profile -band 2) -ne 0) } | Enable-NetFirewallRule
        Get-NetConnectionProfile | Where-Object NetworkCategory -eq 'Public' | Set-NetConnectionProfile -NetworkCategory Private
        Set-NetFirewallProfile -Profile Private -Enabled True
        $public = @(Get-NetConnectionProfile | Where-Object NetworkCategory -eq 'Public')
        $private = Get-NetFirewallProfile -Profile Private
        if ($public.Count -eq 0 -and $private.Enabled) { 'ok' | ConvertTo-Json } else { exit 13 }
        """;

    private const string HealthScript = """
        $ErrorActionPreference = 'Stop'
        $result = @{ Adapters = @(); Categories = @(); FirewallProducts = @(); AntivirusProducts = @() }
        foreach ($adapter in @(Get-NetAdapter | Where-Object Status -eq 'Up')) {
            $bindings = @(Get-NetAdapterBinding -Name $adapter.Name -ComponentID ms_msclient,ms_server -ErrorAction SilentlyContinue)
            $client = $bindings | Where-Object ComponentID -eq 'ms_msclient'
            $server = $bindings | Where-Object ComponentID -eq 'ms_server'
            $result.Adapters += @{ Adapter = $adapter.Name
                Client = if ($client) { [bool]$client.Enabled } else { $null }
                Server = if ($server) { [bool]$server.Enabled } else { $null } }
        }
        $result.Categories = @(Get-NetConnectionProfile | ForEach-Object { [string]$_.NetworkCategory })
        try { $result.FirewallProducts = @(Get-CimInstance -Namespace root/SecurityCenter2 -ClassName FirewallProduct | ForEach-Object { [string]$_.displayName }) } catch { }
        try { $result.AntivirusProducts = @(Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct | ForEach-Object { [string]$_.displayName }) } catch { }
        $result | ConvertTo-Json -Compress -Depth 4
        """;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShareInfo1
    {
        public string Name;
        public uint Type;
        public string Remark;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterDefaults { public IntPtr DataType, DevMode; public uint DesiredAccess; }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int NetShareEnum(string server, int level, out IntPtr buffer, int preferredMaximum,
        out int entriesRead, out int totalEntries, ref int resume);
    [DllImport("netapi32.dll", ExactSpelling = true)]
    private static extern int NetApiBufferFree(IntPtr buffer);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool OpenPrinterW(string name, out IntPtr handle, in PrinterDefaults defaults);
    [DllImport("winspool.drv", ExactSpelling = true)]
    private static extern bool ClosePrinter(IntPtr handle);
}

internal static class PowerShellJson
{
    // Runs a script in Windows PowerShell without a profile and returns its standard output.
    public static async Task<(string? Output, int Error)> RunAsync(string script, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            info.ArgumentList.Add(arg);
        try
        {
            using Process? process = Process.Start(info);
            if (process is null)
                return (null, 13);
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            using var limit = new CancellationTokenSource(timeout);
            try { await process.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                return (null, 1460);
            }
            _ = await errors;
            string text = await output;
            return process.ExitCode == 0 ? (text, 0) : (null, process.ExitCode);
        }
        catch (Win32Exception error) { return (null, error.NativeErrorCode); }
        catch (Exception error) when (error is InvalidOperationException or IOException) { return (null, 13); }
    }
}
