using System.Reflection;
using System.Text.Json;

namespace Kumpuni;

internal sealed record KnownIssue(string Id, string Title, string Detail, string Status, string? Url,
    int[]? Builds = null, int? MinBuild = null, int? MaxBuild = null, int? MinRevision = null, int? MaxRevision = null,
    int[]? Codes = null)
{
    public string StatusText => Status switch
    {
        "confirmed" => "Confirmed by Microsoft",
        "by-design" => "Windows behavior by design",
        _ => "Reported, not confirmed by Microsoft"
    };
}

internal sealed record KnownIssueList(int Schema, string? Updated, KnownIssue[] Issues);

// Known Windows printing issues shown next to a diagnosis. The list ships inside the app and is refreshed
// from the update feed so a new Windows issue does not need an app release. Downloaded text is display-only.
internal static class KnownIssues
{
    private const string FileName = "known-issues.json";
    private const string GitHubTag = "known-issues";
    private const int MaxBytes = 64 * 1024;
    private const int MaxIssues = 50;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static Task<KnownIssue[]>? _loaded;

    public static Task<KnownIssue[]> LoadAsync() => _loaded ??= LoadOnceAsync();

    public static KnownIssue[] Match(IEnumerable<KnownIssue> issues, Machine machine, int? code) => issues.Where(issue =>
        (issue.Builds is not { Length: > 0 } builds || builds.Contains(machine.Build))
        && (issue.MinBuild is not int min || machine.Build >= min)
        && (issue.MaxBuild is not int max || machine.Build <= max)
        && (machine.Revision is not int revision
            || ((issue.MinRevision is not int low || revision >= low) && (issue.MaxRevision is not int high || revision <= high)))
        && (issue.Codes is not { Length: > 0 } codes || code is int value && codes.Contains(value))).ToArray();

    // The live Windows error wins over a code the user typed.
    public static int? RelevantCode(Report? report, uint? reported) =>
        report?.Queue is { Code: not 0 } queue ? queue.Code : reported is uint typed && typed <= int.MaxValue ? (int)typed : null;

    internal static Uri? RemoteUri(Uri? feed)
    {
        if (feed is null)
            return null;
        if (feed.IdnHost == "github.com")
            return new Uri($"{feed.AbsoluteUri.TrimEnd('/')}/releases/download/{GitHubTag}/{FileName}");
        return new Uri(new Uri(feed.AbsoluteUri.EndsWith('/') ? feed.AbsoluteUri : feed.AbsoluteUri + "/"), FileName);
    }

    internal static KnownIssue[]? Parse(string json)
    {
        try
        {
            KnownIssueList? list = JsonSerializer.Deserialize<KnownIssueList>(json, Json);
            if (list is not { Schema: 1, Issues: not null } || list.Issues.Length > MaxIssues)
                return null;
            return list.Issues.All(Valid) ? list.Issues : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool Valid(KnownIssue? issue) => issue is not null
        && Text(issue.Id, 80) && Text(issue.Title, 200) && Text(issue.Detail, 1200) && Text(issue.Status, 20)
        && (issue.Url is null || Uri.TryCreate(issue.Url, UriKind.Absolute, out Uri? url) && url.Scheme == Uri.UriSchemeHttps
            && url.UserInfo.Length == 0 && issue.Url.Length <= 300);

    private static bool Text(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(c => char.IsControl(c) && c != '\n');

    private static async Task<KnownIssue[]> LoadOnceAsync()
    {
        KnownIssue[] bundled = Bundled();
        if (RemoteUri(AppUpdates.ReadFeedUri()) is not Uri remote)
            return bundled;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4), MaxResponseContentBufferSize = MaxBytes };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Kumpuni");
            string json = await client.GetStringAsync(remote).ConfigureAwait(false);
            return Parse(json) ?? bundled;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return bundled;
        }
    }

    internal static KnownIssue[] Bundled()
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Kumpuni.KnownIssues.json");
        if (stream is null)
            return [];
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd()) ?? [];
    }
}
