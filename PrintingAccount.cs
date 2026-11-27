using System.ComponentModel;
using System.Diagnostics;

namespace Kumpuni;

internal static class PrintingAccount
{
    internal const string Name = "printshare";

    internal static bool CanCreate(Report? report) => report is
    {
        Machine.Workstation: true,
        Sharing: { LocalHost: true, SharedQueuePresent: true, LocalAccountModel: { Error: 0, Value: null or 0 } }
    };

    internal static async Task<ActionResult> CreateAsync(Target target)
    {
        if (!SharingSettings.IsLocalHost(target) || !Machine.Read().Workstation
            || SharingSettings.ReadLocalAccountModel() is not { Error: 0, Value: null or 0 }
            || (await Task.Run(() => SharingSettings.CheckLocalShare(target))).Present != true)
            return new("Use a Windows workstation that publishes this printer and accepts authenticated local network sign-ins.", Code: 87);

        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "net.exe"))
        {
            UseShellExecute = true,
            Verb = "runas"
        };
        // net.exe displays the hidden password prompt in its own console. /add leaves an existing account alone.
        foreach (string argument in new[] { "user", Name, "*", "/add", "/passwordreq:yes" })
            info.ArgumentList.Add(argument);
        try
        {
            using Process? process = Process.Start(info);
            if (process is null)
                return new("Windows could not open the account setup console.");
            await process.WaitForExitAsync();
            return process.ExitCode == 0
                ? new("Windows created the standard local printing account.", Verified: true)
                : new("Windows did not create the account. Check whether printshare already exists or the password failed Windows policy.", Code: process.ExitCode);
        }
        catch (Win32Exception error)
        {
            return error.NativeErrorCode == 1223
                ? new("Administrator approval was cancelled.", Code: error.NativeErrorCode)
                : new("Windows could not open the account setup console.", Code: error.NativeErrorCode);
        }
    }
}
