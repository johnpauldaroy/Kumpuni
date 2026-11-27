using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace Kumpuni;

internal static class AppUpdates
{
    private const string UpdateLog = @"%LOCALAPPDATA%\velopack\velopack_Kumpuni.Desktop.log";

    public static void Attach(MainForm form)
    {
        // Keep the standalone executable usable and run updates as the regular Windows user.
        if (!WebWorkspace.CanHost() || ReadFeed() is not IUpdateSource source || Environment.ProcessPath is not string executable)
            return;

        var manager = new UpdateManager(source);
        if (!manager.IsInstalled)
            return;

        using var running = Process.GetCurrentProcess();
        string processName = running.ProcessName;
        int processId = running.Id;
        int sessionId = running.SessionId;
        string? offeredVersion = null;
        bool checking = false;
        CancellationTokenSource? download = null;
        var timer = new System.Windows.Forms.Timer { Interval = (int)TimeSpan.FromHours(4).TotalMilliseconds };
        timer.Tick += async (_, _) => await CheckAsync();
        form.Shown += async (_, _) =>
        {
            timer.Start();
            await CheckAsync();
        };
        form.FormClosed += (_, _) =>
        {
            timer.Dispose();
            download?.Cancel();
        };

        async Task CheckAsync()
        {
            if (checking || form.IsDisposed || form.HasActiveWork)
                return;
            checking = true;
            try
            {
                UpdateInfo? update = await manager.CheckForUpdatesAsync();
                if (update is null || form.IsDisposed || form.HasActiveWork)
                    return;

                string version = update.TargetFullRelease.Version.ToString();
                if (version == offeredVersion)
                    return;
                offeredVersion = version;

                var consent = MessageBox.Show(form,
                    $"Kumpuni {version} is available. Download it and restart Kumpuni to install it?",
                    "Kumpuni update available", MessageBoxButtons.YesNo, MessageBoxIcon.Information,
                    MessageBoxDefaultButton.Button2);
                if (consent != DialogResult.Yes)
                    return;

                if (HasOtherInstance())
                {
                    offeredVersion = null;
                    MessageBox.Show(form, "Wait for checks from this installation to finish and close its other windows. Then reopen Kumpuni.", "Kumpuni update");
                    return;
                }

                try
                {
                    string title = form.Text;
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    download = timeout;
                    try
                    {
                        form.Text = $"Kumpuni | Downloading {version}";
                        IProgress<int> progress = new Progress<int>(percent =>
                        {
                            if (!form.IsDisposed && download == timeout)
                                form.Text = $"Kumpuni | Downloading {version} ({percent}%)";
                        });
                        await manager.DownloadUpdatesAsync(update, progress.Report, timeout.Token);
                        timeout.Token.ThrowIfCancellationRequested();
                    }
                    finally
                    {
                        download = null;
                        if (!form.IsDisposed)
                            form.Text = title;
                    }
                    if (form.IsDisposed)
                        return;
                    if (form.HasActiveWork || HasOtherInstance())
                    {
                        offeredVersion = null;
                        MessageBox.Show(form, "The update is downloaded. Finish active checks and close other Kumpuni windows, then reopen Kumpuni.",
                            "Kumpuni update");
                        return;
                    }

                    manager.WaitExitThenApplyUpdates(update.TargetFullRelease);
                    form.Close(); // Release the WinForms and WebView2 resources before Update.exe swaps the files.
                }
                catch (Exception error)
                {
                    Trace.TraceWarning("Kumpuni update download or apply failed ({0}).", error.GetType().Name);
                    offeredVersion = null;
                    if (!form.IsDisposed)
                    {
                        string problem = error is OperationCanceledException
                            ? "The update download did not finish within five minutes."
                            : "Kumpuni could not prepare the update.";
                        MessageBox.Show(form, $"{problem} Reopen Kumpuni to retry.\n\nUpdate log\n{UpdateLog}",
                            "Kumpuni update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
            catch (Exception error)
            {
                Trace.TraceWarning("Kumpuni update check failed ({0}).", error.GetType().Name);
            }
            finally { checking = false; }
        }

        bool HasOtherInstance()
        {
            Process[] processes = Process.GetProcessesByName(processName);
            try { return processes.Any(process => process.Id != processId && BlocksUpdate(process)); }
            finally
            {
                foreach (Process process in processes)
                    process.Dispose();
            }
        }

        bool BlocksUpdate(Process process)
        {
            try
            {
                return IsInstallPeer(executable, process.MainModule?.FileName, process.SessionId == sessionId);
            }
            catch (InvalidOperationException) { return false; }
            catch (Win32Exception)
            {
                try { return IsInstallPeer(executable, null, process.SessionId == sessionId); }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception) { return false; }
            }
        }
    }

    internal static bool IsInstallPeer(string executable, string? candidate, bool sameSession) =>
        candidate is null ? sameSession : string.Equals(executable, candidate, StringComparison.OrdinalIgnoreCase);

    private static IUpdateSource? ReadFeed()
    {
        if (ReadFeedUri() is not Uri uri)
            return null;
        if (uri.IdnHost == "github.com")
            return new GithubSource(uri.AbsoluteUri, accessToken: null, prerelease: false);
        return new SimpleWebSource(uri.AbsoluteUri);
    }

    // The validated update feed baked in at build time, or null when the build has none.
    internal static Uri? ReadFeedUri()
    {
        string? value = typeof(AppUpdates).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(metadata => metadata.Key == "KumpuniUpdateFeed")?.Value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.Host.Length == 0 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return null;
        if (uri.IdnHost == "github.com"
            && (uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length != 2 || !uri.IsDefaultPort))
            return null;
        return uri;
    }
}
