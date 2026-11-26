using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Kumpuni;

internal sealed record InboundFirewallRule(string Name, string Action, string[] LocalPorts, string[] RemoteAddresses);
internal sealed record InboundFirewallRules(string Store, int PolicyError, int Profile, InboundFirewallRule[] Rules);
internal sealed record FirewallMatch(string Name, string Action, string PortMatch, string RemoteMatch);
internal sealed record FirewallCheck(string Store, int PolicyError, int Profile, int Port, FirewallMatch[] Matches);

internal static class ClientFirewall
{
    public static async Task<FirewallCheck> CheckAsync(IPAddress client, int profile, int port)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)) })
            info.ArgumentList.Add(argument);
        info.Environment["KUMPUNI_RULE_PROFILE"] = profile.ToString(System.Globalization.CultureInfo.InvariantCulture);

        try
        {
            using Process? process = Process.Start(info);
            if (process is null)
                return new("Unavailable", 13, profile, port, []);
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                return new("Unavailable", 1460, profile, port, []);
            }
            _ = await errors;
            if (process.ExitCode != 0)
                return new("Unavailable", process.ExitCode, profile, port, []);
            InboundFirewallRules? rows = JsonSerializer.Deserialize<InboundFirewallRules>(await output);
            if (rows is null)
                return new("Unavailable", 13, profile, port, []);

            FirewallMatch[] matches = rows.Rules.Select(rule => new FirewallMatch(rule.Name, rule.Action,
                MatchPort(rule.LocalPorts, port), MatchAddress(rule.RemoteAddresses, client)))
                .Where(rule => rule.PortMatch != "Outside scope" && rule.RemoteMatch != "Outside scope")
                .OrderBy(rule => rule.Action).ThenBy(rule => rule.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            return new(rows.Store, rows.PolicyError, rows.Profile, port, matches);
        }
        catch (Win32Exception error) { return new("Unavailable", error.NativeErrorCode, profile, port, []); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or IOException)
        {
            return new("Unavailable", 13, profile, port, []);
        }
    }

    internal static string MatchPort(IEnumerable<string> specs, int port) => Match(specs, item =>
    {
        if (item.Equals("RPC-EPMap", StringComparison.OrdinalIgnoreCase))
            return port == 135;
        if (int.TryParse(item, out int exact))
            return port == exact;
        string[] range = item.Split('-');
        if (range is [string start, string end] && int.TryParse(start, out int low) && int.TryParse(end, out int high))
            return port >= low && port <= high;
        return null;
    });

    internal static string MatchAddress(IEnumerable<string> specs, IPAddress ip) => Match(specs, item =>
    {
        byte[] address = Bytes(ip);
        string[] range = item.Split('-');
        if (range is [string start, string end] && IPAddress.TryParse(start, out IPAddress? low)
            && IPAddress.TryParse(end, out IPAddress? high))
        {
            byte[] first = Bytes(low);
            byte[] last = Bytes(high);
            return address.Length == first.Length && address.Length == last.Length
                && address.AsSpan().SequenceCompareTo(first) >= 0 && address.AsSpan().SequenceCompareTo(last) <= 0;
        }

        string[] subnet = item.Split('/');
        if (subnet.Length == 2 && IPAddress.TryParse(subnet[0], out IPAddress? network))
        {
            byte[] bytes = Bytes(network);
            if (bytes.Length != address.Length)
                return false;
            if (!int.TryParse(subnet[1], out int prefix))
            {
                if (bytes.Length != 4 || !IPAddress.TryParse(subnet[1], out IPAddress? mask))
                    return null;
                byte[] maskBytes = Bytes(mask);
                if (maskBytes.Length != 4)
                    return null;
                uint inverted = ~System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(maskBytes);
                if ((inverted & unchecked(inverted + 1)) != 0)
                    return null;
                prefix = 32 - System.Numerics.BitOperations.PopCount(inverted);
            }
            if (prefix < 0 || prefix > bytes.Length * 8)
                return null;
            int whole = prefix / 8;
            if (!address.AsSpan(0, whole).SequenceEqual(bytes.AsSpan(0, whole)))
                return false;
            int bits = prefix % 8;
            return bits == 0 || ((address[whole] ^ bytes[whole]) & (0xFF << (8 - bits))) == 0;
        }

        return IPAddress.TryParse(item, out IPAddress? single) ? address.AsSpan().SequenceEqual(Bytes(single)) : null;
    });

    private static byte[] Bytes(IPAddress address) =>
        (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).GetAddressBytes();

    private static string Match(IEnumerable<string> specs, Func<string, bool?> one)
    {
        string[] items = specs.SelectMany(spec => spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray();
        bool unknown = items.Length == 0;
        foreach (string item in items)
        {
            if (item.Equals("Any", StringComparison.OrdinalIgnoreCase) || item == "*")
                return "Matches";
            bool? matches = one(item);
            if (matches == true)
                return "Matches";
            unknown |= matches is null;
        }
        return unknown ? "Needs review" : "Outside scope";
    }

    private const string Script = """
        $ErrorActionPreference = 'Stop'
        try {
            $fw = New-Object -ComObject HNetCfg.FwPolicy2
            $profile = [int]$env:KUMPUNI_RULE_PROFILE
            if ($profile -eq 0) { $profile = $fw.CurrentProfileTypes -band 7 }
            $store = 'LocalRules'
            $policyError = 5
            $rows = @()
            $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
            if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
                try {
                    $byPort = @{}
                    Get-NetFirewallPortFilter -PolicyStore ActiveStore | ForEach-Object { $byPort[$_.InstanceID] = $_ }
                    $byAddress = @{}
                    Get-NetFirewallAddressFilter -PolicyStore ActiveStore | ForEach-Object { $byAddress[$_.InstanceID] = $_ }
                    $rules = @(Get-NetFirewallRule -PolicyStore ActiveStore -Enabled True -Direction Inbound)
                    if ($rules.Count -gt 0 -and $byPort.Count -gt 0 -and
                        @($rules | Where-Object { $byPort.ContainsKey($_.InstanceID) }).Count -eq 0) { throw 'Rule filter identifiers unavailable' }
                    $rows = @(
                        foreach ($rule in $rules) {
                            if ([int]$rule.Profile -ne 0 -and ([int]$rule.Profile -band $profile) -eq 0) { continue }
                            $port = $byPort[$rule.InstanceID]
                            if ($null -ne $port -and $port.Protocol -notin 'TCP','6','Any','256') { continue }
                            $address = $byAddress[$rule.InstanceID]
                            [pscustomobject]@{
                                Name = [string]$rule.DisplayName; Action = [string]$rule.Action
                                LocalPorts = [string[]]@($port.LocalPort | Where-Object { $_ })
                                RemoteAddresses = [string[]]@($address.RemoteAddress | Where-Object { $_ })
                            }
                        }
                    )
                    $store = 'ActiveStore'
                    $policyError = 0
                } catch { $policyError = if ($_.CategoryInfo.Category -eq 'PermissionDenied') { 5 } else { 13 } }
            }
            if ($store -eq 'LocalRules') {
                $rows = @(
                    foreach ($rule in $fw.Rules) {
                        if (!$rule.Enabled -or $rule.Direction -ne 1 -or
                            ($rule.Profiles -band $profile) -eq 0 -or $rule.Protocol -notin 6,256) { continue }
                        [pscustomobject]@{
                            Name = [string]$rule.Name; Action = if ($rule.Action -eq 1) { 'Allow' } else { 'Block' }
                            LocalPorts = [string[]]@($rule.LocalPorts | Where-Object { $_ })
                            RemoteAddresses = [string[]]@($rule.RemoteAddresses | Where-Object { $_ })
                        }
                    }
                )
            }
            @{ Store=$store; PolicyError=$policyError; Profile=$profile; Rules=$rows } | ConvertTo-Json -Compress -Depth 5
        } catch { exit 13 }
        """;
}
