using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Kumpuni;

internal sealed record DriverDeploymentResult(DateTimeOffset AttemptedAt, string Operation, bool Verified, int Code, string Message);

internal static class DriverDeployment
{
    private static readonly Guid PrinterClass = new("4D36E979-E325-11CE-BFC1-08002BE10318");

    public static int ValidateInf(string path)
    {
        if (path.Length >= 260 || !SharingDiagnostics.IsLocalFilePath(path)
            || !Path.GetExtension(path).Equals(".inf", StringComparison.OrdinalIgnoreCase))
            return 123;
        if (!SetupDiGetINFClassW(path, out Guid deviceClass, new StringBuilder(256), 256, out _))
            return Marshal.GetLastPInvokeError();
        return deviceClass == PrinterClass ? 0 : 13;
    }

    public static async Task<DriverDeploymentResult> RequestAsync(string path)
    {
        DateTimeOffset attempted = DateTimeOffset.UtcNow;
        int error = ValidateInf(path);
        if (error != 0)
            return new(attempted, "ValidatePrinterInf", false, error, "Windows could not verify a local printer-class INF.");

        try
        {
            using Process? worker = Process.Start(Worker.ElevatedDriverStageInfo(path));
            if (worker is null)
                return new(attempted, "StagePrinterInf", false, 13, "Windows did not start the driver helper.");
            await worker.WaitForExitAsync();
            int exit = worker.ExitCode;
            return exit == 0
                ? new(attempted, "StagePrinterInf", true, exit, "PnPUtil reports that the package is in the Windows Driver Store. Check the queue again.")
                : new(attempted, "StagePrinterInf", false, exit, exit == 3010
                    ? "PnPUtil requested a restart. Recheck driver readiness after maintenance."
                    : "Windows did not confirm driver staging. Use the exit code to review the approved package and print policy.");
        }
        catch (Win32Exception exception)
        {
            return new(attempted, "StagePrinterInf", false, exception.NativeErrorCode, exception.NativeErrorCode == 1223
                ? "Elevation was cancelled."
                : "Windows could not start the elevated driver helper.");
        }
        catch (InvalidOperationException)
        {
            return new(attempted, "StagePrinterInf", false, 13, "The driver helper did not report an outcome.");
        }
    }

    internal static int Stage(string path)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return 5;
        int error = ValidateInf(path);
        if (error != 0)
            return error;

        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true
        };
        info.ArgumentList.Add("/add-driver");
        info.ArgumentList.Add(path);
        using Process? process = Process.Start(info);
        if (process is null)
            return 13;
        process.WaitForExit();
        return process.ExitCode;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool SetupDiGetINFClassW(string inf, out Guid deviceClass, StringBuilder className, uint size, out uint required);
}
