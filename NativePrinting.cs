using System.ComponentModel;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Kumpuni;

internal static partial class NativePrinting
{
    private const int InsufficientBuffer = 122;
    private const int BufferLimit = 16 * 1024 * 1024;
    private const uint PrintAccess = 8;
    private const uint QueryService = 4;
    private const uint StartServiceAccess = 16;
    private const uint Stopped = 1;
    private const uint Running = 4;

    public static Inventory ReadInventory()
    {
        (string[] connections, int connectionError) = Connections();
        (string? defaultPrinter, int defaultError) = DefaultPrinter();
        (uint? spoolerState, int spoolerError) = SpoolerStatus();
        return new(connections, defaultPrinter, defaultError, connectionError,
            spoolerState, spoolerError, ConfiguredAutomatic());
    }

    public static Observation Inspect(string path, bool live = true) => ReadQueue(path, live).Observation();

    public static ActionResult Connect(Target target)
    {
        Observation probe = Inspect(target.Path);
        if (probe.Code != 0 || probe.DetailsError != 0)
            return new("The live queue could not be verified. No connection was changed.",
                Code: probe.Code != 0 ? probe.Code : probe.DetailsError);

        (string[] current, int error) = Connections();
        if (error != 0)
            return new("Could not check existing connections. No connection was changed.", Code: error);

        if (!HasConnection(current, target) && !AddPrinterConnectionW(target.Path))
        {
            int code = LastError();
            return new(code == 5
                ? "Windows denied the install. Ask IT to check Point and Print and stage the approved driver, then retry here."
                : "Windows could not add the connection. Diagnose again to collect the failure evidence.", Code: code);
        }

        return VerifyConnection(target);
    }

    public static ActionResult Reconnect(Target target)
    {
        Inventory before = ReadInventory();
        if (before.InventoryError != 0 || before.DefaultError != 0)
            return new("Could not snapshot the current connection and default. No connection was changed.",
                Code: before.InventoryError != 0 ? before.InventoryError : before.DefaultError);
        if (!HasConnection(before.Connections, target))
            return new("The existing connection changed. Diagnose again.");

        Observation live = Inspect(target.Path);
        Observation local = Inspect(target.Path, live: false);
        if (live.Code != 0 || local.Code != 0 || live.Jobs != 0 || local.Jobs != 0)
            return new("Both queue checks must show zero jobs before reconnecting. No connection was changed.");

        if (!DeletePrinterConnectionW(target.Path))
            return new("Windows could not remove the selected connection.", Code: LastError());

        ActionResult connected = Connect(target);
        if (!connected.Verified)
        {
            (string[] current, int error) = Connections();
            if (error == 0 && !HasConnection(current, target))
            {
                if (!AddPrinterConnectionW(target.Path))
                    return new("The original connection was removed. Windows could not restore it. Use the report to contact IT.",
                        Code: LastError());
                if (VerifyConnection(target).Verified && RestoreDefault(before.Default, target))
                    return new("The reconnect failed on its first attempt. The original connection and any selected default were verified after recovery.",
                        Verified: true, Code: connected.Code);
            }

            return new("The original connection was removed, but its final state or default could not be verified. Diagnose again.",
                Code: connected.Code);
        }

        if (!RestoreDefault(before.Default, target))
            return new("The connection was restored. Windows did not confirm the previous default printer.");

        return new("The selected connection is present and the live queue responds. Any previous default on this queue was restored.",
            Verified: true);
    }

    public static ActionResult SetDefault(Target target)
    {
        (string[] current, int error) = Connections();
        if (error != 0 || !HasConnection(current, target))
            return new("The selected connection is unavailable for this user.", Code: error == 0 ? null : error);

        Observation printer = Inspect(target.Path, live: false);
        if (printer.Code != 0 || !printer.DriverKnown || !printer.PortKnown)
            return new("Windows could not verify a driver and port for this connection. Default printer was not changed.",
                Code: printer.Code == 0 ? null : printer.Code);

        if (Diagnose.Same(DefaultPrinter().Name, target.Path))
            return new("Windows confirms the selected queue is already this user's default printer.", Verified: true);

        if (!SetDefaultPrinterW(target.Path))
            return new("Windows could not set the default printer.", Code: LastError());

        (string? actual, error) = DefaultPrinter();
        return Diagnose.Same(actual, target.Path)
            ? new("Windows confirms the selected queue is this user's default printer.", Verified: true)
            : new("Windows did not confirm the default. Check Let Windows manage my default printer and try again.",
                Code: error == 0 ? null : error);
    }

    public static ActionResult PrintTestPage(Target target)
    {
        (string[] current, int error) = Connections();
        if (error != 0 || !HasConnection(current, target))
            return new("Connect the printer as this user before sending a test page.", Code: error == 0 ? null : error);

        QueueProbe queue = ReadQueue(target.Path, live: true);
        if (!ConsoleHealth.MaySendTest(queue.Details, [], queue))
            return new("Windows could not verify a usable output queue. No test page was sent.",
                Code: queue.OpenError != 0 ? queue.OpenError : queue.DetailsError == 0 ? null : queue.DetailsError);

        return SubmitTestPage(target.Path);
    }

    private static ActionResult SubmitTestPage(string name)
    {
        string documentName = $"Kumpuni test {Guid.NewGuid():N}";
        using var document = new PrintDocument { DocumentName = documentName, PrintController = new StandardPrintController() };
        document.PrinterSettings.PrinterName = name;
        if (!document.PrinterSettings.IsValid)
            return new("Windows does not recognize the installed connection as a valid print target.");

        document.PrintPage += (_, args) =>
        {
            using var heading = new Font("Segoe UI", 20, FontStyle.Bold);
            using var body = new Font("Segoe UI", 11);
            args.Graphics!.DrawString("Kumpuni printer test", heading, Brushes.Black, 35, 35);
            args.Graphics.DrawString(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm zzz"), body, Brushes.Black, 35, 80);
        };

        try { document.Print(); }
        catch (Win32Exception exception)
        {
            return new("Windows reported an error during submission. Check the queue before resending.", Code: exception.NativeErrorCode,
                JobState: "Submission uncertain");
        }
        catch (InvalidPrinterException)
        {
            return new("Windows reported that the printer was invalid during submission. Check the queue before resending.",
                JobState: "Submission uncertain");
        }

        string? state = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            state = ReadJob(name, documentName) ?? state;
            if (state is "Reported printed by Windows" or "Job needs attention")
                break;
            Thread.Sleep(300);
        }

        return new("The test page was submitted. Check the physical printer.", Verified: true, JobState: state ?? "Submitted");
    }

    public static int StartSpooler() => Machine.Read().Workstation ? StartAutomaticService("Spooler") : 5;

    public static int ApplyHostRepair(Target target, Repair action)
    {
        if (!SharingSettings.IsLocalHost(target) || !SharingSettings.RemoteSpoolerMayAcceptClients())
            return 5;
        if (action == Repair.StartHostSpooler)
            return StartAutomaticService("Spooler");
        if (action == Repair.RestoreRpcPrivacy)
            return SharingSettings.RestorePrivacy(target);
        if (action == Repair.StartLanmanServer)
        {
            if (SharingSettings.CheckLocalShare(target).Present == true && SharingSettings.ReadNamedPipesConfiguration())
                return StartAutomaticService("LanmanServer");
        }
        return 5;
    }

    private static int StartAutomaticService(string name)
    {
        if (ReadStartType(name) != 2)
            return 5;

        IntPtr manager = OpenSCManagerW(null, null, 1);
        if (manager == IntPtr.Zero)
            return LastError();

        try
        {
            IntPtr service = OpenServiceW(manager, name, QueryService | StartServiceAccess);
            if (service == IntPtr.Zero)
                return LastError();

            try
            {
                if (!QueryServiceStatus(service, out ServiceStatus status))
                    return LastError();
                if (status.CurrentState == Running)
                    return 0;
                if (status.CurrentState != Stopped)
                    return 1061;
                if (!StartServiceW(service, 0, IntPtr.Zero))
                {
                    int error = LastError();
                    return error == 1056 && QueryServiceStatus(service, out status) && status.CurrentState == Running ? 0 : error;
                }

                for (int i = 0; i < 20; i++)
                {
                    if (!QueryServiceStatus(service, out status))
                        return LastError();
                    if (status.CurrentState == Running)
                        return 0;
                    if (status.CurrentState == Stopped)
                        return unchecked((int)(status.Win32ExitCode == 0 ? 1067 : status.Win32ExitCode));
                    Thread.Sleep(500);
                }
                return 1460;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static (string[] Values, int Error) Connections()
    {
        Catalog<string> result = ReadCatalog<PrinterInfo4, string>(
            (IntPtr buffer, int size, out int needed, out int count) =>
                EnumPrintersW(EnumerateConnections, null, 4, buffer, size, out needed, out count),
            row => Text(row.Name));
        return (result.Items, result.Error);
    }


    private static (string? Name, int Error) DefaultPrinter()
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            int chars = 0;
            if (GetDefaultPrinterW(null, ref chars))
                return (null, 0);
            int code = LastError();
            if (code == 2)
                return (null, 0);
            if (code != InsufficientBuffer || chars is <= 0 or > 65536)
                return (null, code);

            var name = new StringBuilder(chars);
            if (GetDefaultPrinterW(name, ref chars))
                return (name.ToString(), 0);
            code = LastError();
            if (code != InsufficientBuffer || attempt == 1)
                return (null, code == 2 ? 0 : code);
        }
        return (null, InsufficientBuffer);
    }

    private static ActionResult VerifyConnection(Target target)
    {
        (string[] current, int error) = Connections();
        if (error != 0 || !HasConnection(current, target))
            return new("Windows did not confirm the connection in the current user's printer list.",
                Code: error == 0 ? null : error);

        Observation live = Inspect(target.Path);
        return live.Code == 0
            ? new("The connection is present and the live queue responds.", Verified: true)
            : new("The connection is present. The live queue could not be verified.", Code: live.Code);
    }

    private static bool RestoreDefault(string? previous, Target target)
    {
        // Reconnecting can unset a default that pointed at the selected connection.
        return !Diagnose.Same(previous, target.Path) || SetDefault(target).Verified;
    }

    public static ServiceReading ReadService(string name)
    {
        var (state, error) = SpoolerStatus(name);
        return new(state, ReadStartType(name), error);
    }

    private static (uint? State, int Error) SpoolerStatus(string name = "Spooler")
    {
        IntPtr manager = OpenSCManagerW(null, null, 1);
        if (manager == IntPtr.Zero)
            return (null, LastError());

        try
        {
            IntPtr service = OpenServiceW(manager, name, QueryService);
            if (service == IntPtr.Zero)
                return (null, LastError());
            try
            {
                return QueryServiceStatus(service, out ServiceStatus status)
                    ? (status.CurrentState, 0) : (null, LastError());
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static bool ConfiguredAutomatic() => ReadStartType("Spooler") == 2;

    private static int? ReadStartType(string name)
    {
        try
        {
            using var service = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
            return service?.GetValue("Start") as int?;
        }
        catch (UnauthorizedAccessException) { return null; }
        catch (System.Security.SecurityException) { return null; }
        catch (IOException) { return null; }
    }

    private static string? ReadJob(string path, string documentName)
    {
        var defaults = new PrinterDefaults { DesiredAccess = PrintAccess };
        if (!OpenPrinterW(path, out IntPtr handle, in defaults))
            return null;

        try
        {
            if (EnumJobsW(handle, 0, 128, 1, IntPtr.Zero, 0, out int needed, out _)
                || !CanReadBuffer(needed))
                return null;

            IntPtr buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!EnumJobsW(handle, 0, 128, 1, buffer, needed, out _, out int count))
                    return null;

                int stride = Marshal.SizeOf<JobInfo1>();
                if (count < 0 || count > needed / stride)
                    return null;

                for (int i = 0; i < count; i++)
                {
                    var job = Marshal.PtrToStructure<JobInfo1>(IntPtr.Add(buffer, i * stride));
                    if (!string.Equals(Marshal.PtrToStringUni(job.Document), documentName, StringComparison.Ordinal))
                        continue;
                    if ((job.Status & (0x2 | 0x20 | 0x40 | 0x200 | 0x400)) != 0)
                        return "Job needs attention";
                    if ((job.Status & 0x80) != 0)
                        return "Reported printed by Windows";
                    return "Visible in Windows queue";
                }
                return null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { ClosePrinter(handle); }
    }

    private static bool HasConnection(string[] values, Target target) => values.Any(value => Diagnose.Same(value, target.Path));
    private static bool CanReadBuffer(int size) => size is > 0 and <= BufferLimit;
    private static int LastError() => Marshal.GetLastPInvokeError();

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterDefaults { public IntPtr DataType, DevMode; public uint DesiredAccess; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterOptions { public uint Size, Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterInfo4 { public IntPtr Name, Server; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterInfo2
    {
        public IntPtr ServerName, PrinterName, ShareName, PortName, DriverName, Comment, Location, DevMode,
            SeparatorFile, PrintProcessor, DataType, Parameters, SecurityDescriptor;
        public uint Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, Jobs, AveragePpm;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobInfo1
    {
        public uint Id;
        public IntPtr Printer, Machine, User, Document, DataType, StatusText;
        public uint Status, Priority, Position, TotalPages, PagesPrinted;
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool OpenPrinter2W(string name, out IntPtr handle, in PrinterDefaults defaults, in PrinterOptions options);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool OpenPrinterW(string name, out IntPtr handle, in PrinterDefaults defaults);
    [DllImport("winspool.drv", ExactSpelling = true)]
    private static extern bool ClosePrinter(IntPtr handle);
    [DllImport("winspool.drv", ExactSpelling = true, SetLastError = true)]
    private static extern bool GetPrinterW(IntPtr handle, uint level, IntPtr buffer, int size, out int needed);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool EnumPrintersW(uint flags, string? name, uint level, IntPtr buffer, int size, out int needed, out int count);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool GetDefaultPrinterW(StringBuilder? name, ref int chars);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool SetDefaultPrinterW(string name);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool AddPrinterConnectionW(string name);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool DeletePrinterConnectionW(string name);
    [DllImport("winspool.drv", ExactSpelling = true, SetLastError = true)]
    private static extern bool EnumJobsW(IntPtr handle, uint first, uint count, uint level, IntPtr buffer, int size, out int needed, out int returned);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool StartServiceW(IntPtr service, uint count, IntPtr args);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
