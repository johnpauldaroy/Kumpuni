using System.Globalization;
using System.Text.Json;

namespace Kumpuni;

internal sealed record ReportDifference(string Field, string Client, string Host);

internal static class SharingReportComparison
{
    private const long Limit = 2 * 1024 * 1024;

    public static ReportDifference[] CompareFiles(string clientFile, string hostFile)
    {
        using JsonDocument client = Read(clientFile);
        using JsonDocument host = Read(hostFile);
        return Compare(client.RootElement, host.RootElement);
    }

    private static JsonDocument Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > Limit)
            throw new InvalidDataException("The sharing report exceeds 2 MiB.");
        return JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 20 });
    }

    internal static ReportDifference[] Compare(JsonElement client, JsonElement host)
    {
        client = Evidence(client, false);
        host = Evidence(host, true);
        return
        [
            Row("Windows build", Build(client), Build(host)),
            Row("Print RPC protocol", Text(client, "Policy.RpcTransport", "TCP", "Named pipes"),
                Option(host, "Sharing.RpcProtocols.Value", (3, "Named pipes"), (5, "TCP"), (7, "TCP and named pipes"))),
            Row("Outgoing RPC authentication", Number(client, "Sharing.RpcAuthentication.Value"), "Not applicable"),
            Row("Incoming RPC privacy", "Not applicable", Number(host, "Sharing.Privacy.Value")),
            Row("Fixed print RPC port", "Not available from the client", Number(host, "Sharing.RpcTcpPort.Value")),
            Row("TCP 135 from this PC", Reachability(client, "Network.RpcMapper"), Reachability(host, "Network.RpcMapper")),
            Row("TCP 445 from this PC", Reachability(client, "Network.Smb"), Reachability(host, "Network.Smb")),
            Row("Live queue access", QueueAccess(client), QueueAccess(host)),
            Row("Share published on host", "Not applicable", Boolean(host, "Sharing.SharedQueuePresent")),
            Row("Local driver matches", Number(client, "Advanced.Driver.MatchingDriverCount"), Number(host, "Advanced.Driver.MatchingDriverCount")),
            Row("Registered driver version", VersionText(client, "Advanced.Driver.Version"), VersionText(host, "Advanced.Driver.Version")),
            Row("Queue ACL entries", "Not applicable", Number(host, "Advanced.Queue.Permissions.AceCount")),
            Row("File-sharing session", SessionText(client), SessionText(host)),
            Row("Host print server (print RPC)", ErrorOrOk(client, "Connectivity.PrintServerError"), ErrorOrOk(host, "Connectivity.PrintServerError")),
            Row("Network type", Joined(client, "Connectivity.Health.Categories"), Joined(host, "Connectivity.Health.Categories")),
            Row("Windows Firewall on active network", FirewallOn(client), FirewallOn(host)),
            Row("Security products", Products(client), Products(host)),
            Row("Client for Microsoft Networks", Binding(client, "Client"), "Not applicable"),
            Row("File and Printer Sharing binding", "Not applicable", Binding(host, "Server")),
            Row("Type 3 print framework files", FrameworkMatch(client, host), FrameworkMatch(client, host)),
            .. SharingDiagnostics.FrameworkFiles.Select(name =>
                Row($"Print framework {name}", FrameworkVersion(client, name), FrameworkVersion(host, name)))
        ];
    }

    private static string SessionText(JsonElement item) => Integer(item, "Connectivity.Session.Error") switch
    {
        null => "Not in report",
        0 => Element(item, "Connectivity.Session.SharePublished").ValueKind switch
        {
            JsonValueKind.True => "Opened, share listed", JsonValueKind.False => "Opened, share not listed", _ => "Opened"
        },
        int error => $"Error {error}"
    };

    private static string ErrorOrOk(JsonElement item, string path) => Integer(item, path) switch
    {
        null => "Not in report", 0 => "Opened", int error => $"Error {error}"
    };

    private static string[] Strings(JsonElement item, string path) => Element(item, path) is { ValueKind: JsonValueKind.Array } values
        ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).ToArray() : [];

    private static string Joined(JsonElement item, string path) =>
        Element(item, path).ValueKind == JsonValueKind.Array ? Strings(item, path) is { Length: > 0 } values ? string.Join(", ", values) : "None" : "Not in report";

    private static string Products(JsonElement item) =>
        Element(item, "Connectivity.Health").ValueKind != JsonValueKind.Object ? "Not in report"
        : Strings(item, "Connectivity.Health.FirewallProducts").Concat(Strings(item, "Connectivity.Health.AntivirusProducts")).Distinct().ToArray()
            is { Length: > 0 } products ? string.Join(", ", products) : "None reported";

    private static string FirewallOn(JsonElement item) =>
        Integer(item, "Sharing.Firewall.ActiveProfiles") is int active && Integer(item, "Sharing.Firewall.EnabledLocally") is int enabled
            ? (enabled & active) == active ? "On" : "Off" : "Unknown";

    private static string Binding(JsonElement item, string field)
    {
        if (Element(item, "Connectivity.Health.Adapters") is not { ValueKind: JsonValueKind.Array } adapters)
            return "Not in report";
        JsonValueKind[] states = adapters.EnumerateArray().Select(adapter => Element(adapter, field).ValueKind).ToArray();
        return states.Any(state => state == JsonValueKind.False) ? "Off on an active adapter"
            : states.Any(state => state == JsonValueKind.True) ? "On" : "Unknown";
    }

    private static string FrameworkVersion(JsonElement item, string name)
    {
        if (Element(item, "Advanced.Framework") is not { ValueKind: JsonValueKind.Array } files)
            return "Not in report";
        foreach (JsonElement file in files.EnumerateArray())
        {
            if (Element(file, "Name") is { ValueKind: JsonValueKind.String } fileName && fileName.GetString() == name)
                return Element(file, "Version") is { ValueKind: JsonValueKind.String } value
                    && Version.TryParse(value.GetString(), out Version? version) ? version.ToString()
                    : Integer(file, "Error") == 2 ? "Not installed" : "Unreadable";
        }
        return "Not in report";
    }

    private static string FrameworkMatch(JsonElement client, JsonElement host)
    {
        string[] left = SharingDiagnostics.FrameworkFiles.Select(name => FrameworkVersion(client, name)).ToArray();
        string[] right = SharingDiagnostics.FrameworkFiles.Select(name => FrameworkVersion(host, name)).ToArray();
        if (left.Concat(right).Any(value => !Version.TryParse(value, out _)))
            return "Unknown";
        return left.SequenceEqual(right) ? "Same versions" : "Versions differ";
    }

    private static JsonElement Evidence(JsonElement doc, bool host)
    {
        JsonElement role = Element(doc, "Evidence.Sharing.LocalHost");
        if (Element(doc, "SchemaVersion") is not { ValueKind: JsonValueKind.Number } schema
            || !schema.TryGetInt32(out int version) || version != 2
            || role.ValueKind != (host ? JsonValueKind.True : JsonValueKind.False))
            throw new InvalidDataException(host
                ? "Select a schema 2 Shared printer repair report collected on the print host."
                : "Select a schema 2 Shared printer repair report collected on the affected client.");
        return Element(doc, "Evidence");
    }

    private static ReportDifference Row(string field, string client, string host) => new(field, client, host);

    private static JsonElement Element(JsonElement value, string path)
    {
        foreach (string part in path.Split('.'))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value))
                return default;
        }
        return value;
    }

    private static int? Integer(JsonElement item, string path) =>
        Element(item, path) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out int number) ? number : null;

    private static string Number(JsonElement item, string path) => Integer(item, path)?.ToString(CultureInfo.InvariantCulture) ?? "Unknown";
    private static string Build(JsonElement item) => Integer(item, "Machine.Revision") is int revision
        ? $"{Number(item, "Machine.Build")}.{revision}" : Number(item, "Machine.Build");
    private static string Boolean(JsonElement item, string path) => Element(item, path).ValueKind switch
    {
        JsonValueKind.True => "Yes", JsonValueKind.False => "No", _ => "Unknown"
    };
    private static string Reachability(JsonElement item, string path) => Element(item, path).ValueKind switch
    {
        JsonValueKind.True => "Reached", JsonValueKind.False => "No response", _ => "Unknown"
    };
    private static string Text(JsonElement item, string path, params string[] allowed) =>
        Element(item, path) is { ValueKind: JsonValueKind.String } value && value.GetString() is string known && allowed.Contains(known)
        ? known : "Unknown";
    private static string Option(JsonElement item, string path, params (int Value, string Label)[] allowed) =>
        allowed.FirstOrDefault(option => option.Value == Integer(item, path)).Label ?? Number(item, path);
    private static string VersionText(JsonElement item, string path) =>
        Element(item, path) is { ValueKind: JsonValueKind.String } value && Version.TryParse(value.GetString(), out Version? version)
        ? version.ToString() : "Unknown";
    private static string QueueAccess(JsonElement item)
    {
        int? open = Integer(item, "Queue.Code");
        int detail = Integer(item, "Queue.DetailsError") ?? 0;
        return open switch { null => "Unknown", 0 when detail == 0 => "Opened", 0 => $"Properties error {detail}", _ => $"Open error {open}" };
    }
}
