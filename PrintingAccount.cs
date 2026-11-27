using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kumpuni;

internal static class PrintingAccount
{
    internal const string Name = "printshare";
    private const int UserNotFound = 2221;
    private const int AccountDisabled = 1331;
    private const uint AccountDisabledFlag = 0x0002;

    internal static bool CanCreate(Report? report) => report is
    {
        Machine.Workstation: true,
        Sharing: { LocalHost: true, SharedQueuePresent: true, LocalAccountModel: { Error: 0, Value: null or 0 },
            RemoteSpooler: { Error: 0, Value: null or 1 } }
    };

    internal static async Task<ActionResult> CreateAsync(Target target)
    {
        if (!SharingSettings.IsLocalHost(target) || !Machine.Read().Workstation)
            return new("Use a Windows workstation that publishes this printer and accepts authenticated local network sign-ins.", Code: 87);

        int code = await Worker.RunTechnicianActionAsync(TechnicianAction.PreparePrintingAccount, [target.Path]);
        return code switch
        {
            0 => new("Windows verified the local printing account and its Print permission entry on the selected queue.", Verified: true),
            1223 => new("Administrator approval was cancelled.", Code: code),
            AccountDisabled => new("The existing printshare account is disabled. Ask the host administrator to review the account.", Code: code),
            _ => new("Windows could not verify account setup and Print permission. A retry reuses any account Windows already created.", Code: code)
        };
    }

    // Called by the elevated technician worker after it acquires the local repair lock.
    internal static int Prepare(Target target)
    {
        if (!SharingSettings.IsLocalHost(target) || !Machine.Read().Workstation
            || SharingSettings.ReadLocalAccountModel() is not { Error: 0, Value: null or 0 }
            || !SharingSettings.RemoteSpoolerMayAcceptClients())
            return 87;

        Catalog<QueueEntry> queues = NativePrinting.LocalQueues();
        if (queues.Error != 0)
            return queues.Error;
        QueueEntry[] matches = queues.Items.Where(queue => queue.Scope == QueueScope.SharedHere
            && Diagnose.Same(queue.Details?.Share, target.Share)).ToArray();
        if (matches is not [QueueEntry queue])
            return 1801;

        int state = AccountState();
        if (state == UserNotFound)
        {
            int created = CreateLocalUser();
            state = AccountState();
            if (state == UserNotFound && created != 0)
                return created;
        }
        return state != 0 ? state : NativePrinting.GrantQueuePrint(queue.Name, $@"{Environment.MachineName}\{Name}", target.Share);
    }

    private static int CreateLocalUser()
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "net.exe"))
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        };
        // net.exe displays the hidden password prompt in its own console. /add leaves an existing account alone.
        foreach (string argument in new[] { "user", Name, "*", "/add", "/passwordreq:yes" })
            info.ArgumentList.Add(argument);
        using Process? process = Process.Start(info);
        if (process is null)
            return 13;
        process.WaitForExit();
        return process.ExitCode;
    }

    private static int AccountState()
    {
        int code = NetUserGetInfo(null, Name, 20, out IntPtr buffer);
        try
        {
            if (code != 0)
                return code;
            if (buffer == IntPtr.Zero)
                return 13;
            return (Marshal.PtrToStructure<UserInfo20>(buffer).Flags & AccountDisabledFlag) != 0 ? AccountDisabled : 0;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                NetApiBufferFree(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UserInfo20
    {
        public IntPtr Name, FullName, Comment;
        public uint Flags, Id;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int NetUserGetInfo(string? server, string user, int level, out IntPtr buffer);

    [DllImport("netapi32.dll", ExactSpelling = true)]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
