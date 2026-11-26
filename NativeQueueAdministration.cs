using System.Runtime.InteropServices;
using System.Text;

namespace Kumpuni;

internal static partial class NativePrinting
{
    public static QueueAdministration ReadQueueAdministration(string name, int jobLimit)
    {
        const uint readControl = 0x00020000;
        const uint administer = 0x4;
        var management = new PrinterDefaults { DesiredAccess = administer };
        int manageError;
        if (OpenPrinterW(name, out IntPtr handle, in management))
        {
            ClosePrinter(handle);
            manageError = 0;
        }
        else { manageError = LastError(); }

        var security = new PrinterDefaults { DesiredAccess = readControl };
        QueuePermissions permissions;
        if (OpenPrinterW(name, out handle, in security))
        {
            try { permissions = ReadQueuePermissions(handle, manageError); }
            finally { ClosePrinter(handle); }
        }
        else { permissions = new(LastError(), null, null, manageError); }

        var print = new PrinterDefaults { DesiredAccess = PrintAccess };
        QueueJobs jobs;
        if (OpenPrinterW(name, out handle, in print))
        {
            try
            {
                Catalog<QueueJobFlags> catalog = ReadCatalog<JobInfo1, QueueJobFlags>(
                    (IntPtr buffer, int size, out int needed, out int count) =>
                        EnumJobsW(handle, 0, (uint)jobLimit, 1, buffer, size, out needed, out count),
                    job => new QueueJobFlags(job.Status));
                jobs = SummarizeJobs(catalog.Items.Select(job => job.Status), catalog.Error, jobLimit);
            }
            finally { ClosePrinter(handle); }
        }
        else { jobs = SummarizeJobs([], LastError(), jobLimit); }

        return new(permissions, jobs);
    }

    internal static QueueJobs SummarizeJobs(IEnumerable<uint> statuses, int error, int limit)
    {
        uint[] sample = statuses.ToArray();
        const uint needsAttention = 0x2 | 0x20 | 0x40 | 0x200 | 0x400;
        return new(error, sample.Length, sample.Count(flags => (flags & needsAttention) != 0),
            sample.Count(flags => (flags & 0x1) != 0), sample.Count(flags => (flags & (0x8 | 0x10)) != 0),
            sample.Length >= limit);
    }

    private static QueuePermissions ReadQueuePermissions(IntPtr handle, int manageError)
    {
        if (GetPrinterW(handle, 3, IntPtr.Zero, 0, out int needed))
            return new(13, null, null, manageError);
        int error = LastError();
        if (error != InsufficientBuffer || !CanReadBuffer(needed) || needed < IntPtr.Size)
            return new(error == InsufficientBuffer ? 13 : error, null, null, manageError);

        IntPtr buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!GetPrinterW(handle, 3, buffer, needed, out _))
                return new(LastError(), null, null, manageError);
            IntPtr descriptor = Marshal.ReadIntPtr(buffer);
            if (descriptor == IntPtr.Zero)
                return new(13, null, null, manageError);
            if (!GetSecurityDescriptorDacl(descriptor, out bool present, out IntPtr dacl, out _))
                return new(LastError(), null, null, manageError);
            if (!present || dacl == IntPtr.Zero)
                return new(0, "Unrestricted", null, manageError);
            if (!GetAclInformation(dacl, out AclSize size, (uint)Marshal.SizeOf<AclSize>(), 2))
                return new(LastError(), null, null, manageError);
            return new(0, size.AceCount == 0 ? "Empty" : "Configured", size.AceCount, manageError);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private sealed record QueueJobFlags(uint Status);

    [StructLayout(LayoutKind.Sequential)]
    private struct AclSize { public uint AceCount, BytesInUse, BytesFree; }

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool GetSecurityDescriptorDacl(IntPtr descriptor, out bool present, out IntPtr dacl, out bool defaulted);
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool GetAclInformation(IntPtr acl, out AclSize info, uint length, uint informationClass);
}
