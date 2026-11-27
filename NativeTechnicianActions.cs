using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Kumpuni;

internal enum PrintJobAction { Resume, Restart, Cancel }
internal sealed record JobSelection(uint Id, uint Status, string Stamp, uint Pages, uint Printed, string? Document, string? Owner);

internal static partial class NativePrinting
{
    private const uint AdministerQueue = 4;
    private const uint ReadControl = 0x20000;
    private const uint WriteDac = 0x40000;
    private const int PrintPermission = (int)(ReadControl | PrintAccess);
    private const int QueueHasChanged = 1237;
    private const int QueueJobLimit = 128;

    public static bool ValidShareName(string name) =>
        name.Length <= 80 && !name.EndsWith('$') && Target.Parse(@"\\localhost\" + name)?.Share == name;

    public static int PublishQueue(string name, string share)
    {
        if (!ValidShareName(share) || !SharingSettings.RemoteSpoolerMayAcceptClients())
            return 5;
        Catalog<QueueEntry> queues = LocalQueues();
        if (queues.Error != 0)
            return queues.Error;
        QueueEntry? queue = queues.Items.FirstOrDefault(item => Diagnose.Same(item.Name, name));
        if (queue?.Details is not QueueDetails details)
            return 1801;
        if (queue.Scope == QueueScope.SharedHere)
            return Diagnose.Same(details.Share, share) ? 0 : QueueHasChanged;
        Catalog<PortEntry> ports = InstalledPorts();
        if (ports.Error != 0)
            return ports.Error;
        if (ConsoleHealth.Kind(details, ports.Items) is PortKind.Virtual or PortKind.Unknown)
            return 50;
        if (queues.Items.Any(item => item.Scope == QueueScope.SharedHere && Diagnose.Same(item.Details?.Share, share)))
            return 183;

        var defaults = new PrinterDefaults { DesiredAccess = AdministerQueue };
        if (!OpenPrinterW(queue.Name, out IntPtr handle, in defaults))
            return LastError();
        try
        {
            int result = WithPrinterInformation(handle, 2, Marshal.SizeOf<PrinterInfo2>(), buffer =>
            {
                PrinterInfo2 info = Marshal.PtrToStructure<PrinterInfo2>(buffer);
                if ((info.Attributes & SharedAttribute) != 0)
                    return Diagnose.Same(Text(info.ShareName), share) ? 0 : QueueHasChanged;
                IntPtr shareText = Marshal.StringToHGlobalUni(share);
                try
                {
                    info.ShareName = shareText;
                    info.Attributes |= SharedAttribute;
                    info.SecurityDescriptor = IntPtr.Zero;
                    Marshal.StructureToPtr(info, buffer, false);
                    return SetPrinterW(handle, 2, buffer, 0) ? 0 : LastError();
                }
                finally { Marshal.FreeHGlobal(shareText); }
            });
            return result == 0 ? CheckPublished(name, share) : result;
        }
        finally { ClosePrinter(handle); }
    }

    private static int CheckPublished(string name, string share)
    {
        Catalog<QueueEntry> after = LocalQueues();
        if (after.Error != 0)
            return after.Error;
        return after.Items.Any(entry => entry.Scope == QueueScope.SharedHere && Diagnose.Same(entry.Name, name)
            && Diagnose.Same(entry.Details?.Share, share)) ? 0 : QueueHasChanged;
    }

    public static int GrantQueuePrint(string name, string account, string? expectedShare = null)
    {
        if (account.Split('\\') is not [string domain, string principal] || string.IsNullOrWhiteSpace(domain)
            || string.IsNullOrWhiteSpace(principal) || account.Length > 256 || account.Any(char.IsControl))
            return 87;

        Catalog<QueueEntry> queues = LocalQueues();
        if (queues.Error != 0)
            return queues.Error;
        QueueEntry? queue = queues.Items.FirstOrDefault(item => item.Scope == QueueScope.SharedHere && Diagnose.Same(item.Name, name)
            && (expectedShare is null || Diagnose.Same(item.Details?.Share, expectedShare)));
        if (queue is null)
            return 1801;

        SecurityIdentifier sid;
        try { sid = (SecurityIdentifier)new NTAccount(domain, principal).Translate(typeof(SecurityIdentifier)); }
        catch (IdentityNotMappedException) { return 1332; }
        if (!IsSpecificPrincipal(sid))
            return 5;

        var defaults = new PrinterDefaults { DesiredAccess = ReadControl | WriteDac };
        if (!OpenPrinterW(queue.Name, out IntPtr handle, in defaults))
            return LastError();

        try
        {
            int result = WithPrinterInformation(handle, 3, IntPtr.Size, buffer =>
            {
                RawSecurityDescriptor before = ReadDescriptor(buffer);
                RawSecurityDescriptor? changed = AddExplicitPrint(before, sid);
                if (changed is null)
                    return 1336;
                if (ReferenceEquals(before, changed))
                    return 0;

                var update = new byte[changed.BinaryLength];
                changed.GetBinaryForm(update, 0);
                var pinned = GCHandle.Alloc(update, GCHandleType.Pinned);
                try
                {
                    Marshal.WriteIntPtr(buffer, pinned.AddrOfPinnedObject());
                    return SetPrinterW(handle, 3, buffer, 0) ? 0 : LastError();
                }
                finally { pinned.Free(); }
            });
            return result != 0 ? result : WithPrinterInformation(handle, 3, IntPtr.Size,
                buffer => HasExplicitPrint(ReadDescriptor(buffer).DiscretionaryAcl, sid) ? 0 : QueueHasChanged);
        }
        catch (ArgumentException) { return 1338; }
        finally { ClosePrinter(handle); }
    }

    internal static bool IsSpecificPrincipal(SecurityIdentifier sid) => !new[]
    {
        WellKnownSidType.WorldSid, WellKnownSidType.AuthenticatedUserSid, WellKnownSidType.AnonymousSid,
        WellKnownSidType.BuiltinUsersSid, WellKnownSidType.BuiltinGuestsSid, WellKnownSidType.NetworkSid,
        WellKnownSidType.InteractiveSid, WellKnownSidType.LocalSid
    }.Any(sid.IsWellKnown);

    internal static RawSecurityDescriptor? AddExplicitPrint(RawSecurityDescriptor source, SecurityIdentifier sid)
    {
        if (!IsSpecificPrincipal(sid) || source.DiscretionaryAcl is not RawAcl existing
            || !new DiscretionaryAcl(true, false, existing).IsCanonical)
            return null;
        if (HasExplicitPrint(existing, sid))
            return source;

        var bytes = new byte[existing.BinaryLength];
        existing.GetBinaryForm(bytes, 0);
        var updated = new RawAcl(bytes, 0);
        int index = 0;
        while (index < updated.Count && !updated[index].IsInherited)
            index++;
        updated.InsertAce(index, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, PrintPermission, sid, false, null));
        const ControlFlags preserved = ControlFlags.DiscretionaryAclProtected | ControlFlags.DiscretionaryAclAutoInherited
            | ControlFlags.DiscretionaryAclAutoInheritRequired;
        return new RawSecurityDescriptor((source.ControlFlags & preserved) | ControlFlags.DiscretionaryAclPresent,
            null, null, null, updated);
    }

    private static bool HasExplicitPrint(RawAcl? acl, SecurityIdentifier sid) => acl?.Cast<GenericAce>().Any(ace =>
        ace is CommonAce { AceQualifier: AceQualifier.AccessAllowed, IsInherited: false, IsCallback: false } allow
        && (allow.AceFlags & AceFlags.InheritOnly) == 0 && allow.SecurityIdentifier == sid
        && (allow.AccessMask & PrintPermission) == PrintPermission) == true;

    private static RawSecurityDescriptor ReadDescriptor(IntPtr buffer)
    {
        IntPtr descriptor = Marshal.ReadIntPtr(buffer);
        uint length = descriptor == IntPtr.Zero ? 0 : GetSecurityDescriptorLength(descriptor);
        if (length is < 20 or > ushort.MaxValue)
            throw new ArgumentException("Invalid printer security descriptor.");
        var bytes = new byte[length];
        Marshal.Copy(descriptor, bytes, 0, bytes.Length);
        return new RawSecurityDescriptor(bytes, 0);
    }

    public static Catalog<JobSelection> QueueJobsForTechnician(string name)
    {
        Catalog<QueueEntry> catalog = LocalQueues();
        if (catalog.Error != 0)
            return new([], catalog.Error);
        QueueEntry? queue = catalog.Items.FirstOrDefault(entry => Diagnose.Same(entry.Name, name));
        if (queue is null)
            return new([], 1801);
        var defaults = new PrinterDefaults { DesiredAccess = PrintAccess };
        if (!OpenPrinterW(queue.Name, out IntPtr handle, in defaults))
            return new([], LastError());
        try
        {
            return ReadCatalog<JobInfo1, JobSelection>((IntPtr buffer, int size, out int needed, out int count) =>
                EnumJobsW(handle, 0, QueueJobLimit, 1, buffer, size, out needed, out count), job =>
                new(job.Id, job.Status, JobStamp(job), job.TotalPages, job.PagesPrinted, Text(job.Document), Text(job.User)));
        }
        finally { ClosePrinter(handle); }
    }

    public static bool CanApplyJobAction(PrintJobAction action, uint flags) => (flags & (0x4 | 0x100)) == 0 && action switch
    {
        PrintJobAction.Resume => (flags & 0x1) != 0,
        PrintJobAction.Restart => (flags & 0x10) != 0,
        PrintJobAction.Cancel => (flags & (0x80 | 0x1000)) == 0,
        _ => false
    };

    public static int ChangeQueueJob(string name, uint id, string stamp, PrintJobAction action)
    {
        Catalog<JobSelection> selected = QueueJobsForTechnician(name);
        if (selected.Error != 0)
            return selected.Error;
        if (!selected.Items.Any(job => job.Id == id && job.Stamp == stamp && CanApplyJobAction(action, job.Status)))
            return QueueHasChanged;

        var defaults = new PrinterDefaults { DesiredAccess = AdministerQueue };
        if (!OpenPrinterW(name, out IntPtr handle, in defaults))
            return LastError();
        try
        {
            int result = WithJobInformation(handle, id, job =>
            {
                if (JobStamp(job) != stamp || !CanApplyJobAction(action, job.Status))
                    return QueueHasChanged;
                uint command = action switch { PrintJobAction.Resume => 2, PrintJobAction.Restart => 4, PrintJobAction.Cancel => 5, _ => 0 };
                return command == 0 ? 87 : SetJobW(handle, id, 0, IntPtr.Zero, command) ? 0 : LastError();
            });
            if (result != 0)
                return result;
            for (int i = 0; i < 8; i++)
            {
                int read = WithJobInformation(handle, id, job => JobStamp(job) != stamp || action switch
                {
                    PrintJobAction.Resume => (job.Status & 0x1) == 0,
                    PrintJobAction.Cancel => (job.Status & (0x4 | 0x100)) != 0,
                    PrintJobAction.Restart => (job.Status & 0x800) != 0 || (job.Status & 0x10) == 0,
                    _ => false
                } ? 0 : QueueHasChanged);
                if (read is 0 or 87 or 1168)
                    return 0;
                if (read != QueueHasChanged)
                    return read;
                Thread.Sleep(200);
            }
            return QueueHasChanged;
        }
        finally { ClosePrinter(handle); }
    }

    private static string JobStamp(JobInfo1 job) =>
        FormattableString.Invariant($"{job.Year:0000}{job.Month:00}{job.Day:00}{job.Hour:00}{job.Minute:00}{job.Second:00}{job.Milliseconds:000}");

    private static int WithPrinterInformation(IntPtr handle, uint level, int minimum, Func<IntPtr, int> project)
    {
        if (GetPrinterW(handle, level, IntPtr.Zero, 0, out int needed))
            return 13;
        int error = LastError();
        if (error != InsufficientBuffer)
            return error;
        if (!CanReadBuffer(needed) || needed < minimum)
            return 13;
        IntPtr buffer = Marshal.AllocHGlobal(needed);
        try { return GetPrinterW(handle, level, buffer, needed, out _) ? project(buffer) : LastError(); }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static int WithJobInformation(IntPtr handle, uint id, Func<JobInfo1, int> project)
    {
        if (GetJobW(handle, id, 1, IntPtr.Zero, 0, out int needed))
            return 13;
        int error = LastError();
        if (error != InsufficientBuffer)
            return error;
        if (!CanReadBuffer(needed) || needed < Marshal.SizeOf<JobInfo1>())
            return 13;
        IntPtr buffer = Marshal.AllocHGlobal(needed);
        try { return GetJobW(handle, id, 1, buffer, needed, out _) ? project(Marshal.PtrToStructure<JobInfo1>(buffer)) : LastError(); }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool SetPrinterW(IntPtr handle, uint level, IntPtr buffer, uint command);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool GetJobW(IntPtr handle, uint id, uint level, IntPtr buffer, int size, out int needed);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool SetJobW(IntPtr handle, uint id, uint level, IntPtr buffer, uint command);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
}
