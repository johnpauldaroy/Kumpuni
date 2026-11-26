using System.Text.Json;
using Velopack;

namespace Kumpuni;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        if (args.Length == 0)
        {
            ApplicationConfiguration.Initialize();
            using var form = new MainForm();
            AppUpdates.Attach(form);
            Application.Run(form);
            return 0;
        }

        try
        {
            if (args is ["--start-spooler"])
                return NativePrinting.StartSpooler();
            if (args is ["--host-repair", string hostPath, string hostVerb] && Target.Parse(hostPath) is Target host
                && Enum.TryParse(hostVerb, out Repair hostAction))
                return NativePrinting.ApplyHostRepair(host, hostAction);
            if (args is ["--stage-driver", string inf])
                return DriverDeployment.Stage(inf);
            if (args is ["--technician-action", string selectedAction, ..] && Enum.TryParse(selectedAction, out TechnicianAction action))
                return Worker.ApplyTechnicianAction(action, args[2..]);
            if (args is ["--technician-jobs", string localQueue] && IsQueueArgument(localQueue))
                return Write(new WorkerReply(Jobs: NativePrinting.QueueJobsForTechnician(localQueue)));
            if (args is ["--quick-local", string localPrinter] && IsQueueArgument(localPrinter))
                return Write(new WorkerReply(QuickLocal: NativePrinting.ReadQuickLocal(localPrinter)));
            if (args is ["--quick-default", string defaultPrinter, "SetDefault"] && IsQueueArgument(defaultPrinter))
                return Write(new WorkerReply(Action: Worker.WithPrintLock(() => NativePrinting.SetProfileDefault(defaultPrinter))));
            if (args is ["--inventory"])
                return Write(new WorkerReply(Inventory: NativePrinting.ReadInventory()));
            if (args is ["--diagnose" or "--quick-shared", string path] && Target.Parse(path) is Target target)
                return Write(new WorkerReply(Report: Worker.CollectAsync(target, compareLocalCache: args[0] == "--quick-shared").GetAwaiter().GetResult()));
            if (args is ["--repair", string repairPath, string verb] && Target.Parse(repairPath) is Target repairTarget
                && Enum.TryParse(verb, out Repair repair))
                return Write(new WorkerReply(Action: Worker.Apply(repairTarget, repair)));
            if (args is ["--console-scan"])
                return Write(new WorkerReply(Console: Worker.ScanConsoleAsync().GetAwaiter().GetResult()));
            if (args is ["--console-probe", string queue] && IsQueueArgument(queue))
                return Write(new WorkerReply(Check: Worker.CheckConsoleQueueAsync(queue).GetAwaiter().GetResult()));
            if (args is ["--console-test", string testQueue] && IsQueueArgument(testQueue))
                return Write(new WorkerReply(Action: Worker.WithPrintLock(() => NativePrinting.TestInstalledQueue(testQueue))));
            if (args is ["--check-console"])
                return CheckConsole();
            if (args is ["--check-printer-help"])
                return Checks.CheckPrinterHelpAsync().GetAwaiter().GetResult();
            if (args is ["--check-quick-repair"])
                return Checks.CheckQuickRepair();
            if (args is ["--self-test"])
                return Checks.Run();
            if (args is ["--check-sharing"])
                return Checks.CheckSharing();
            if (args is ["--check-sharing-details"])
                return Checks.CheckSharingDetails();
            if (args is ["--check-technician-actions"])
                return Checks.CheckTechnicianActionsAsync().GetAwaiter().GetResult();
            if (args is ["--check-local"])
            {
                Inventory inventory = NativePrinting.ReadInventory();
                Observation probe = NativePrinting.Inspect(@"\\localhost\KumpuniCheckNoSuchQueue-24D6A160");
                return Write(new
                {
                    Machine = Machine.Read(),
                    inventory.InventoryError,
                    inventory.DefaultError,
                    inventory.SpoolerState,
                    inventory.SpoolerError,
                    LocalMissingQueueCode = probe.Code
                }, inventory.InventoryError == 0 && inventory.DefaultError == 0 && inventory.SpoolerError == 0 && probe.Code != 0 ? 0 : 1);
            }
            if (args is ["--check-worker"])
            {
                WorkerReply? reply = Worker.RunAsync("--diagnose", @"\\localhost\KumpuniCheckNoSuchQueue-24D6A160").GetAwaiter().GetResult();
                bool succeeded = reply?.Report is { Inventory: not null, Queue: not null };
                return Write(new { WorkerReturnedReport = succeeded, QueueError = reply?.Report?.Queue?.Code }, succeeded ? 0 : 1);
            }
            if (args is ["--check-web-ui"] or ["--check-web-ui", _])
            {
                ApplicationConfiguration.Initialize();
                return WebWorkspace.CheckBinding(args.Length == 2 ? args[1] : null);
            }
            if (args is ["--check-ui"])
            {
                ApplicationConfiguration.Initialize();
                using var form = new MainForm();
                bool formCreated = form.Handle != IntPtr.Zero;
                bool inventoryBound = TechnicianPanel.CheckInventoryBinding();
                bool printerHelpBound = PrinterHelpPanel.CheckBinding();
                bool sharingBound = form.CheckSharedRepairBinding();
                bool actionsBound = form.CheckTechnicianActionsBinding();
                bool homeNavigation = form.CheckHomeNavigation();
                bool quickRepairBound = QuickRepairPanel.CheckBindingAsync().GetAwaiter().GetResult();
                bool brandingBound = form.CheckBrandingAssets();
                return Write(new { FormCreated = formCreated, InventoryBoundAndFiltered = inventoryBound,
                    PrinterHelpBound = printerHelpBound, SharedRepairBound = sharingBound, TechnicianActionsBound = actionsBound,
                    HomeNavigation = homeNavigation, OneClickWorkflowBound = quickRepairBound, BrandingBound = brandingBound },
                    formCreated && inventoryBound && printerHelpBound && sharingBound && actionsBound && homeNavigation && quickRepairBound && brandingBound ? 0 : 1);
            }
            return 2;
        }
        catch (Exception exception)
        {
            if (args[0] == "--technician-action")
                return exception.HResult;
            if (args[0] is "--start-spooler" or "--host-repair" or "--stage-driver")
                return 1;
            if (args[0] is "--repair" or "--console-test" or "--quick-default")
                return Write(new WorkerReply(Action: new ActionResult(
                    $"Windows did not confirm the repair outcome ({exception.GetType().Name}). Check the queue before retrying.")));
            return Write(new { Error = exception.GetType().Name }, 1);
        }
    }

    private static int CheckConsole()
    {
        ConsoleSnapshot? snapshot = Worker.RunAsync("--console-scan").GetAwaiter().GetResult()?.Console;
        if (snapshot is null)
            return Write(new { CatalogWorker = false }, 1);

        QueueEntry? local = snapshot.Local.Items.FirstOrDefault();
        ConsoleCheck? check = local is null ? null
            : Worker.RunAsync("--console-probe", local.Name).GetAwaiter().GetResult()?.Check;
        ActionResult blocked = Worker.WithPrintLock(() => NativePrinting.TestInstalledQueue("KumpuniMissingQueue-24D6A160"));
        bool succeeded = snapshot.Local.Error == 0 && snapshot.Shared.Error == 0
            && snapshot.Drivers.Error == 0 && snapshot.Ports.Error == 0 && blocked is { Verified: false, Code: 1801 }
            && (local is null || check?.Probe is { OpenError: 0, DetailsError: 0 });

        return Write(new
        {
            CatalogWorker = true,
            LocalQueues = snapshot.Local.Items.Length,
            LocalError = snapshot.Local.Error,
            SharedQueues = snapshot.Shared.Items.Length,
            SharedError = snapshot.Shared.Error,
            Drivers = snapshot.Drivers.Items.Length,
            DriverError = snapshot.Drivers.Error,
            Ports = snapshot.Ports.Items.Length,
            PortError = snapshot.Ports.Error,
            LocalQueueOpen = check?.Probe.OpenError,
            LocalQueueRead = check?.Probe.DetailsError,
            UninstalledTestBlocked = !blocked.Verified && blocked.Code == 1801
        }, succeeded ? 0 : 1);
    }

    private static bool IsQueueArgument(string queue) => queue is { Length: > 0 and <= 1024 } && !queue.Any(char.IsControl);

    internal static int Write<T>(T value, int exitCode = 0)
    {
        using Stream output = Console.OpenStandardOutput();
        JsonSerializer.Serialize(output, value);
        output.Flush();
        return exitCode;
    }
}
