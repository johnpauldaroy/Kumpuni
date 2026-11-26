using System.Runtime.InteropServices;

namespace Kumpuni;

internal static partial class NativePrinting
{
    private const uint EnumerateLocal = 2;
    private const uint EnumerateConnections = 4;
    private const uint SharedAttribute = 8;

    public static Catalog<QueueEntry> LocalQueues() => ReadCatalog<PrinterInfo2, QueueEntry>(
        (IntPtr buffer, int size, out int needed, out int count) =>
            EnumPrintersW(EnumerateLocal, null, 2, buffer, size, out needed, out count),
        row => Text(row.PrinterName) is string name
            ? new QueueEntry(name, (row.Attributes & SharedAttribute) != 0 ? QueueScope.SharedHere : QueueScope.Local, ReadDetails(row))
            : null);

    public static Catalog<QueueEntry> SharedConnections()
    {
        var (connections, error) = Connections();
        return new(connections.Select(name => new QueueEntry(name, QueueScope.SharedConnection)).ToArray(), error);
    }

    public static Catalog<DriverEntry> InstalledDrivers() => ReadCatalog<DriverInfo6, DriverEntry>(
        (IntPtr buffer, int size, out int needed, out int count) =>
            EnumPrinterDriversW(null, "all", 6, buffer, size, out needed, out count),
        row => Text(row.Name) is string name
            ? new DriverEntry(name, Text(row.Environment) ?? "Unknown", row.Model, DriverVersion(row.Version))
            : null);

    public static Catalog<PortEntry> InstalledPorts() => ReadCatalog<PortInfo2, PortEntry>(
        (IntPtr buffer, int size, out int needed, out int count) =>
            EnumPortsW(null, 2, buffer, size, out needed, out count),
        row => Text(row.Name) is string name ? new PortEntry(name, Text(row.Monitor), Text(row.Description), row.Flags) : null);

    public static HostSpooler ReadHostSpooler()
    {
        var (state, statusError) = SpoolerStatus();
        var (defaultQueue, defaultError) = DefaultPrinter();
        return new(state, statusError, defaultQueue, defaultError);
    }

    public static QuickLocalEvidence ReadQuickLocal(string name)
    {
        ServiceReading service = ReadService("Spooler");
        bool workstation = Machine.Read().Workstation;
        if (service.State != 4)
            return new(workstation, service, null, PortKind.Unknown, null, 0);
        QueueProbe probe = name.StartsWith(@"\\", StringComparison.Ordinal) ? new(1801, 0, null) : CheckInstalledQueue(name);
        Catalog<PortEntry> ports = probe.Details is null ? new([], 0) : InstalledPorts();
        var (current, error) = DefaultPrinter();
        return new(workstation, service, probe, ConsoleHealth.Kind(probe.Details, ports.Items),
            error == 0 ? Diagnose.Same(current, name) : null, error);
    }

    public static ActionResult SetProfileDefault(string name)
    {
        QuickLocalEvidence state = ReadQuickLocal(name);
        if (state is not { Spooler.State: 4, Probe: { OpenError: 0, DetailsError: 0, Details: QueueDetails details } }
            || string.IsNullOrWhiteSpace(details.Driver) || string.IsNullOrWhiteSpace(details.Port)
            || ConsoleHealth.Health(details) is QueueHealth.Attention or QueueHealth.Unknown)
            return new("Windows could not verify the selected local queue before setting the default.");
        if (state.IsDefault == true)
            return new("This printer is already the Windows default.", Verified: true);
        if (state.DefaultError != 0)
            return new("Windows could not read the current default printer.", Code: state.DefaultError);
        if (!SetDefaultPrinterW(name))
            return new("Windows could not set the default printer.", Code: LastError());

        var (actual, error) = DefaultPrinter();
        return error == 0 && Diagnose.Same(actual, name)
            ? new("Windows confirmed the selected printer as the default.", Verified: true)
            : new("Windows could not verify the new default printer.", Code: error == 0 ? null : error);
    }

    public static QueueProbe CheckInstalledQueue(string name)
    {
        if (!IsInstalledQueue(name, out bool shared, out int error))
            return new(error, 0, null);
        return ReadQueue(name, live: shared);
    }

    public static ActionResult TestInstalledQueue(string name)
    {
        QueueProbe probe = CheckInstalledQueue(name);
        if (probe.OpenError != 0 || probe.DetailsError != 0)
            return new("Windows could not verify the selected queue. Run Check queue and review the reported error.",
                Code: probe.OpenError != 0 ? probe.OpenError : probe.DetailsError);

        // Require the fresh queue and port properties to identify a suitable output destination.
        Catalog<PortEntry> ports = InstalledPorts();
        if (!ConsoleHealth.MaySendTest(probe.Details, ports.Items, probe))
            return new("The selected queue is virtual, incomplete, or reports a fault. Check its status before sending a test page.");

        return SubmitTestPage(name);
    }

    private static bool IsInstalledQueue(string name, out bool shared, out int error)
    {
        shared = name.StartsWith(@"\\", StringComparison.Ordinal);
        Catalog<QueueEntry> catalog = shared ? SharedConnections() : LocalQueues();
        if (catalog.Items.Any(queue => Diagnose.Same(queue.Name, name)))
        {
            error = 0;
            return true;
        }
        error = catalog.Error == 0 ? 1801 : catalog.Error;
        return false;
    }

    internal static QueueProbe ReadQueue(string name, bool live)
    {
        var defaults = new PrinterDefaults { DesiredAccess = PrintAccess };
        var options = new PrinterOptions { Size = (uint)Marshal.SizeOf<PrinterOptions>(), Flags = 1 };
        bool opened = live
            ? OpenPrinter2W(name, out IntPtr handle, in defaults, in options)
            : OpenPrinterW(name, out handle, in defaults);
        if (!opened)
            return new(LastError(), 0, null);

        try
        {
            if (GetPrinterW(handle, 2, IntPtr.Zero, 0, out int needed))
                return new(0, 13, null);
            int error = LastError();
            if (error != InsufficientBuffer || !CanReadBuffer(needed) || needed < Marshal.SizeOf<PrinterInfo2>())
                return new(0, error, null);

            IntPtr buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetPrinterW(handle, 2, buffer, needed, out _))
                    return new(0, LastError(), null);

                return new(0, 0, ReadDetails(Marshal.PtrToStructure<PrinterInfo2>(buffer)));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { ClosePrinter(handle); }
    }

    private static QueueDetails ReadDetails(PrinterInfo2 row) =>
        new(Text(row.DriverName), Text(row.PortName), Text(row.ShareName), row.Status, row.Jobs, row.Attributes);

    private delegate bool CatalogReader(IntPtr buffer, int size, out int needed, out int count);

    private static Catalog<T> ReadCatalog<TRow, T>(CatalogReader read, Func<TRow, T?> project)
        where TRow : struct where T : class
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (read(IntPtr.Zero, 0, out int needed, out int emptyCount))
                return new([], emptyCount == 0 ? 0 : 13);
            int error = LastError();
            if (error != InsufficientBuffer || !CanReadBuffer(needed))
                return new([], error);

            IntPtr buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!read(buffer, needed, out _, out int count))
                {
                    error = LastError();
                    if (error == InsufficientBuffer && attempt == 0)
                        continue;
                    return new([], error);
                }

                int stride = Marshal.SizeOf<TRow>();
                if (count < 0 || count > needed / stride)
                    return new([], 13);

                var rows = new List<T>(count);
                for (int i = 0; i < count; i++)
                {
                    if (project(Marshal.PtrToStructure<TRow>(IntPtr.Add(buffer, i * stride))) is T item)
                        rows.Add(item);
                }
                return new(rows.ToArray(), 0);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return new([], InsufficientBuffer);
    }

    private static string? Text(IntPtr address)
    {
        if (address == IntPtr.Zero)
            return null;
        string? text = Marshal.PtrToStringUni(address);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? DriverVersion(ulong value) => value == 0 ? null :
        $"{value >> 48}.{(value >> 32) & 0xFFFF}.{(value >> 16) & 0xFFFF}.{value & 0xFFFF}";

    [StructLayout(LayoutKind.Sequential)]
    private struct DriverInfo6
    {
        public uint Model;
        public IntPtr Name, Environment, DriverPath, DataFile, ConfigFile, HelpFile, DependentFiles, Monitor,
            DefaultDataType, PreviousNames;
        public System.Runtime.InteropServices.ComTypes.FILETIME DriverDate;
        public ulong Version;
        public IntPtr Manufacturer, OemUrl, HardwareId, Provider;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PortInfo2
    {
        public IntPtr Name, Monitor, Description;
        public uint Flags, Reserved;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool EnumPrinterDriversW(string? server, string? environment, uint level,
        IntPtr buffer, int size, out int needed, out int count);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool EnumPortsW(string? server, uint level, IntPtr buffer, int size, out int needed, out int count);
}
