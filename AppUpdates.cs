using System.Diagnostics;
using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace Kumpuni;

internal static class AppUpdates
{
    public static void Attach(MainForm form)
    {
        // Keep the standalone executable usable and run updates as the regular Windows user.
        if (!WebWorkspace.CanHost() || ReadFeed() is not IUpdateSource source)
            return;

        var manager = new UpdateManager(source);
        if (!manager.IsInstalled)
            return;

        using var running = Process.GetCurrentProcess();
        string processName = running.ProcessName;
        int processId = running.Id;
        string? offeredVersion = null;
        bool checking = false;
        var timer = new System.Windows.Forms.Timer { Interval = (int)TimeSpan.FromHours(4).TotalMilliseconds };
        timer.Tick += async (_, _) => await CheckAsync();
        form.Shown += async (_, _) =>
        {
            timer.Start();
            await CheckAsync();
        };
        form.FormClosed += (_, _) => timer.Dispose();

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
                    MessageBox.Show(form, "Close other Kumpuni windows and reopen Kumpuni to update.", "Kumpuni update");
                    return;
                }

                try
                {
                    await manager.DownloadUpdatesAsync(update);
                    if (form.IsDisposed)
                        return;
                    if (form.HasActiveWork || HasOtherInstance())
                    {
                        offeredVersion = null;
                        MessageBox.Show(form, "The update is downloaded. Finish active checks and close other Kumpuni windows, then reopen Kumpuni.",
                            "Kumpuni update");
                        return;
                    }

                    manager.ApplyUpdatesAndRestart(update);
                }
                catch (Exception error)
                {
                    Trace.TraceWarning("Kumpuni update download or apply failed ({0}).", error.GetType().Name);
                    if (!form.IsDisposed)
                    {
                        MessageBox.Show(form, "Kumpuni could not install the update. Reopen Kumpuni to try again.",
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
            try { return processes.Any(process => process.Id != processId); }
            finally
            {
                foreach (Process process in processes)
                    process.Dispose();
            }
        }
    }

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