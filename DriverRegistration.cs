using System.Runtime.InteropServices;
using System.Text;

namespace Kumpuni;

internal static class DriverRegistration
{
    public static int Register(string inf, string model)
    {
        if (model.Length is 0 or > 256 || model != model.Trim() || model.Any(char.IsControl))
            return 87;

        int validate = DriverDeployment.ValidateInf(inf);
        if (validate != 0)
            return validate;
        PrintPolicy? policy = Worker.ReadPolicy();
        if (policy?.ProtectedPrintRequired == true)
            return 1260;

        string? environment = SharingDiagnostics.NativeEnvironment();
        if (environment is null)
            return 50;
        Catalog<DriverEntry> before = NativePrinting.InstalledDrivers();
        if (before.Error != 0)
            return before.Error;
        if (Contains(before, model, environment))
            return 183;

        var driverStore = new StringBuilder(32768);
        uint length = (uint)driverStore.Capacity;
        int uploaded = UploadPrinterDriverPackageW(null, inf, environment, 0, IntPtr.Zero, driverStore, ref length);
        if (uploaded != 0 || driverStore.Length == 0)
            return uploaded == 0 ? 13 : uploaded;
        int installed = InstallPrinterDriverFromPackageW(null, driverStore.ToString(), model, environment, 0);
        if (installed != 0)
            return installed;

        Catalog<DriverEntry> after = NativePrinting.InstalledDrivers();
        if (after.Error != 0)
            return after.Error;
        return Contains(after, model, environment) ? 0 : 1237;
    }

    private static bool Contains(Catalog<DriverEntry> drivers, string model, string environment) =>
        drivers.Items.Any(driver => Diagnose.Same(driver.Name, model) && Diagnose.Same(driver.Environment, environment));

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int UploadPrinterDriverPackageW(string? server, string inf, string environment,
        uint flags, IntPtr owner, StringBuilder destination, ref uint length);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int InstallPrinterDriverFromPackageW(string? server, string inf, string driver,
        string environment, uint flags);
}
