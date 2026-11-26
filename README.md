# Kumpuni

Kumpuni is a standalone Windows x64 printer diagnostics utility. Run **Kumpuni.exe** on the workstation or Windows print host you want to inspect. Use the affected Windows user's session when troubleshooting that user's printer connections.

The main workspace uses the bundled Kumpuni mockup styles in Microsoft Edge WebView2. If the WebView2 Runtime is unavailable or Kumpuni is launched elevated, the native workspace opens instead. Start Kumpuni normally for the web-style layout. Eligible administrator actions continue to request elevation through their existing Windows workers.

## My printer

1. Select an installed USB or network printer, or enter the exact Windows shared printer path, such as `\\PRINT-PC\Office`. Optionally allow Kumpuni to set it as your Windows default.
2. Select **Save printer** once. Kumpuni saves this choice for the current Windows account. Use **Change printer** to edit it.
3. Select **Check and fix** to diagnose the saved queue and apply eligible repairs. Kumpuni can connect a verified share, reconnect an idle connection when the live queue is healthy and the local cache reports offline, start eligible print services, or set the opted-in default. Windows may request elevation.
4. Follow **Diagnose**, **Apply repair**, and **Verify** in **Last check**. Read the result and use **Export report** if you need to share the evidence. Kumpuni checks Windows queue readiness. Physical printing remains a separate confirmation.

Use **Technician console** in the sidebar to inspect live inventory. Open **Advanced tools** for All-Round Printer Help, Shared printer repair, technician actions and physical test pages.

## Technician console

1. Open **Technician console**. It inventories local queues, queues shared by this PC, and connections visible to the current user. Select **Refresh inventory** to rescan.
2. Use **Queues**, **Drivers**, and **Ports** to inspect Windows-reported status, queued job counts, driver models and versions, and port monitors. The filter searches the inventory on the selected tab.
3. Select a queue and choose **Check queue** to inspect Windows access, get fresh queue properties, and look up matching PrintService/Admin event IDs. For a connected share, the app probes the remote queue without using the client cache.
4. Select **Send test page** for a suitable output queue. After Windows accepts submission, use **Confirm page printed** once you see the page.
5. Select **Export redacted report** to save an inventory and test summary for escalation. Queue, driver, and port names become report-local IDs. Host addresses, user names, document names, and full event text are omitted.

The console scans the Windows print subsystem already configured on the machine. Run it on the print host for that host's installed drivers and ports. A partial inventory shows the section and Windows error. Queue status reflects what Windows reports, and a physical page remains a separate confirmation.

## All-Round Printer Help

Open **All-Round Printer Help** and choose how the printer connects. Use **View installed queues** to open the matching Technician Console filter. After adding a printer in Windows, refresh the inventory.

| Connection | What to do | What Kumpuni checks |
|---|---|---|
| USB | Connect the printer, select **Check USB detection**, then inspect the queue or open Windows printer settings to add it. | Whether Windows exposes an active USB printer interface on this PC. A vendor-specific device can use a different interface. |
| Network | Enter the hostname or IP address and the printer's configured TCP port. Use the RAW, LPR, or IPP/IPPS preset when it matches the device. For WSD discovery, use Windows printer settings. | Name resolution and whether the TCP port accepts a connection. Verify printing with the installed queue's test page. |
| Shared | Enter the exact UNC path, for example `\\OFFICE-PC\Accounting`, and open **Shared printer repair**. | The existing shared repair workflow checks host reachability, Windows queue access, and available policy and event evidence. |

The redacted Technician Console report includes the latest completed USB and TCP checks for the session when available, including the TCP port and outcome. Network hostnames, IP addresses, and USB identifiers are omitted.

## Shared printer repair

1. Open **Shared printer repair**. Enter the exact UNC path, such as `\\OFFICE-PC\Accounting`, and choose what failed. Include the Windows error code if you have one.
2. Select **Check and repair** and confirm the scope. Kumpuni checks the share, applies eligible actions, and reads Windows state again after each action. Use **Diagnose only** to collect evidence without making changes.
3. Run the same check on the print host using its Windows computer name or `localhost` in the UNC path to audit the host's registry, services, firewall profiles, and published share. An administrator can opt in to **Restore RPC privacy** for an explicit disabled override on a verified local share. The app records the change from `0` to `1`. Restart the Spooler or host during maintenance, then retest from the affected client. If Group Policy manages this setting, correct the source policy.
4. Use **Stage approved driver** after diagnosis if IT supplied a local printer driver INF. Choose one printer-class INF and confirm. Windows PnPUtil stages it in the Driver Store. Retry the connection or register the matching print driver in Print Management if Windows still reports the driver missing.
5. Select **Export report** to save the redacted JSON. It includes before and after snapshots, dated repairs and driver-staging attempts, Windows result codes, and rechecks. Physical printing is confirmed separately.

| Failure or setting | What Kumpuni checks or changes | Next check when unresolved | Microsoft reference |
|---|---|---|---|
| Missing share, wrong path, or stale connection (`0x00000709`, `67`, `53`) | Checks the published share on the host, DNS, the live UNC queue and the user's connection. Can connect a verified queue or reconnect an idle matching connection after a relevant reported error. | Verify the host has published the intended share and the client can resolve its hostname. | [Shared printer connection problems](https://support.microsoft.com/en-us/windows/hardware/printer/fix-shared-printer-connection-problems-in-windows) |
| Print RPC, firewall, or stopped Spooler (`1722`, `1753`) | Checks TCP 135, live queue access, local Spooler, firewall service and active profiles, and configured RPC transport and authentication, incoming protocols, TCP port, Kerberos policy and the allowed client connections policy. Can start a stopped automatic Spooler on the identified print host or an eligible workstation. | Verify the configured listener and effective firewall rules from the affected client. TCP 135 alone is only the endpoint mapper. | [Print RPC settings](https://learn.microsoft.com/en-us/troubleshoot/windows-client/printing/windows-11-rpc-connection-updates-for-print) |
| Windows 11 24H2 or later, `0x00000709` with SMB reachable | When the host name resolves and TCP 445 responds, reports blocked guest or unsigned SMB access instead of a wrong share name. Offers **Sign in to print host**, which opens the Windows credential prompt for `\\HOST\IPC$`. Windows keeps the password; select Remember my credentials to save it in Credential Manager. Kumpuni then checks the queue again. The same action is offered for `5`, `86`, `1326`, `1244` and `1272`. | Confirm the account has access to the host and the queue's Print permission. | [Shared printer connection problems](https://support.microsoft.com/en-us/windows/hardware/printer/fix-shared-printer-connection-problems-in-windows) |
| Type 3 print framework mismatch (`0x00000006`) | Reports the error and reads the versions of `UNIDRV.DLL`, `UNIDRVUI.DLL`, `UNIRES.DLL`, `PSCRIPT5.DLL` and `PS5UI.DLL` in `spool\drivers\<arch>\3`. **Compare reports** shows them side by side. Kumpuni never replaces system files. | Bring the client and host to the same update level. | [Windows release health](https://learn.microsoft.com/en-us/windows/release-health/) |
| RPC authentication (`0x0000011B`) | Reads the local `RpcAuthnLevelPrivacyEnabled` DWORD. An administrator can opt in to re-enable privacy on the verified print host. | Compare Windows updates and RPC policy on both PCs. Restart and retest after a privacy change. | [KB4599464](https://support.microsoft.com/en-us/servicing/os/windows-server/2020/12/managing-deployment-of-printer-rpc-binding-changes-for-cve-2021-1678-kb4599464) |
| SMB, `LanmanServer`, credentials, or queue permissions | Checks TCP 445, selected Windows errors, the local Server service, and explicit SMB protocol, signing, and guest overrides. Can start the stopped automatic Server service on the verified host when incoming print RPC over named pipes is configured. | Check authenticated access, the queue's Print permission, the SMB policy on both PCs and applicable inbound rules. Registry overrides may differ from effective defaults. | [SMB TCP troubleshooting](https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/tcp-three-way-handshake-fails), [SMB signing](https://learn.microsoft.com/en-us/windows-server/storage/file-server/smb-signing) |
| Windows update or RPC compatibility (`0x000006E4`, `0x0000007C`) | Records the actual error and Windows build. | Compare host and client update history against the guidance for that version. | [Printing issue troubleshooting](https://learn.microsoft.com/en-us/troubleshoot/windows-server/printing/troubleshoot-printer-guidance), [Windows release health](https://learn.microsoft.com/en-us/windows/release-health/) |
| Driver installation or Point and Print | Checks driver-related Win32 errors, PrintService/Admin event IDs, Point and Print, Windows protected print policy, and locally registered drivers matching the queue and Windows architecture. Can stage an approved local printer INF. | If required, register the approved driver in Print Management or use a compatible queue. | [KB5005652](https://support.microsoft.com/en-us/servicing/os/windows/2021/08/kb5005652-manage-new-point-and-print-default-driver-installation-behavior-cve-2021-34481) |
| Default printer, stuck jobs, or physical output | Can set a verified connection as the user's default or submit one test page when the Print task is selected and the queue is eligible. Reconnection requires zero jobs in both live and cached checks. A host audit samples up to 128 jobs by status. | Inspect the device or use Windows queue administration for individual jobs. | [Windows printing scenarios](https://learn.microsoft.com/en-us/troubleshoot/windows-server/printing/troubleshoot-printing-scenarios) |
| Active firewall policy and Network Discovery | Audits built-in SMB, RPC and Network Discovery rules on active profiles, plus custom inbound rules with explicit print ports when the effective store is readable. Checks `fdPHost` and `FDResPub`. A standard account gets labeled local-rule evidence. | Run on the intended host as administrator for `ActiveStore`. Compare custom rule scopes and client reachability. A stopped trigger-start Discovery service alone does not establish a fault. | [Get-NetFirewallRule](https://learn.microsoft.com/en-us/powershell/module/netsecurity/get-netfirewallrule), [Discovery services](https://learn.microsoft.com/en-us/windows-server/security/windows-services/security-guidelines-for-disabling-system-services-in-windows-server) |
| Queue permissions and print jobs | Uses a live Print access check in the affected user session. The host audit inspects the DACL and probes Manage Printers for the current account. The export omits account and document names and ACL principals. | Review the queue Security tab and grant Print to the intended groups through your access process. | [Printer security descriptor](https://learn.microsoft.com/en-us/windows/win32/printdocs/printer-info-3) |
| Spool folder and disk capacity | Checks the selected hosted queue's configured spool location where present, otherwise the host default. Reports folder accessibility and free space on the Windows volume, including mounted volumes. The report omits paths. | Use storage maintenance for a full volume and verify the spool folder and spool service permissions. A standard user's folder-access result is separate from the Spooler's service identity. | [Spool folder guidance](https://learn.microsoft.com/en-us/troubleshoot/windows-server/printing/troubleshoot-printing-scenarios) |

## Technician actions

| Tab | Use | What Windows verifies or reports |
|---|---|---|
| Compare reports | Export a redacted Shared printer repair report on the affected client and on the print host for the same UNC share. Open both schema 2 files and review the highlighted differences. | Client and host roles, Windows builds, RPC configuration, port reachability, driver inventory, and queue access. |
| Register driver | Enter the exact model and choose the approved local printer INF. Enter an optional trusted UNC path to retry as the current user after verification. | Adds the selected package to the Driver Store as needed, registers the missing model, and checks the local spooler driver inventory. An existing matching driver is left in place. |
| Client firewall | On the host, enter the affected client IP, select the host profile, and enter the TCP port. Use Windows administrator approval to inspect ActiveStore. | Shows enabled inbound rule candidates by address and port. Rules can have additional constraints such as application, service, IPsec, and interfaces. Symbolic scopes such as LocalSubnet and dynamic RPC require review. |
| Local queues and jobs | Refresh local queues. Share a selected local queue by UNC name, grant Print to a specific DOMAIN user or group on a shared queue, or load and manage one selected job. | Re-reads the resulting share or ACL, or checks the selected job status. Windows can enable its File and Printer Sharing exception when an administrator shares a queue. Existing explicit denies stay in place. Retest access from the affected user session. |

Use **Export action report** to save timestamps, result codes, verification outcomes and firewall candidate counts. Queue names, identities, rule names, source paths and job details stay out of this report.

## Build

Use .NET SDK 10 from the directory containing `Kumpuni.csproj`.

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=false -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None
```

The standalone executable bundles the x64 .NET 10.0.15 Desktop Runtime. The interactive console requires a Windows desktop session.

## Windows installer and updates

Build a local Windows x64 installer from the project folder:

```powershell
.\Installer\Build-Installer.ps1 -Version 1.0.0
```

The setup and update files are written to `Installer\releases`. Setup installs per Windows user and obtains the Microsoft Edge WebView2 Runtime if needed. The app and its updater are installed under `%LOCALAPPDATA%\Kumpuni.Desktop`. Existing printer setup stays in `%LOCALAPPDATA%\Kumpuni\printer-setup.json`.

To enable update notifications, use one public GitHub repository or HTTPS directory that installed users can reach. Pass the same URL for the first hosted installer and later builds. Use the GitHub repository URL, without `/releases`.

```powershell
.\Installer\Build-Installer.ps1 -Version 1.0.1 -UpdateFeed 'https://github.com/OWNER/REPOSITORY'
```

Increase the version for every release. For GitHub Releases, attach `releases.win-x64.json` and the packages it references to the new release. Include Setup for new users. For a static HTTPS directory, upload the packages before replacing `releases.win-x64.json`. Keep previous packages available if the new feed references them. Anyone who installed a build without `-UpdateFeed` needs to run the first installer that includes it.

Installed copies check on launch and every four hours while open. When a newer version is available, Kumpuni asks before downloading it and restarting. If a repair is active or another Kumpuni process is running, the restart is deferred. Publish installers and packages signed with the same publisher when distributing to users. For a Windows signing certificate already available to SignTool, the build script accepts `-SigningCertificateThumbprint`.

See the [Velopack update sources](https://docs.velopack.io/integrating/update-sources) and [code signing](https://docs.velopack.io/packaging/signing) documentation.

## Verification

| Switch | Check |
|---|---|
| `--self-test` | Error classification, queue health, test eligibility, and report redaction fixtures |
| `--check-web-ui` | Bundled browser layout, first-run setup, live inventory and fixture repair flow |
| `--check-ui` | Form creation, inventory and connection filters, and guided printer controls using fixtures |
| `--check-console` | Live local queue, driver, and port enumeration, and a read-only check of an installed queue |
| `--check-local` | Local spooler and an unavailable loopback queue |
| `--check-worker` | Shared-printer diagnostic worker startup and response |
| `--check-sharing` | Local RPC, SMB, firewall and share audit, plus guards against mutating a missing share or remote host |
| `--check-sharing-details` | Read-only firewall, spool volume, local queue and printer INF validation checks |
| `--check-technician-actions` | Read-only client-specific firewall candidate query, local job enumeration and Windows Spooler export checks |
| `--check-printer-help` | Windows USB-interface enumeration, plus accepted and refused TCP connections on loopback |
| `--check-quick-repair` | Read-only worker verification for an installed local printer and a missing queue |

For acceptance, test on Windows 10 and 11 and a Windows Server print host with a dedicated share. Include a local physical printer, a shared queue, a virtual queue, an offline or paused printer, a blocked print RPC endpoint, and physical test page confirmation. In a dedicated test host, verify service recovery and RPC privacy read-back plus client connectivity after a maintenance restart. Include an elevated ActiveStore comparison and an approved printer INF staging and registration test on a disposable Windows installation. Use a dedicated queue and test account for sharing, access grants, and job recovery.

## Microsoft references

- [USB printer device detection](https://learn.microsoft.com/en-us/windows-hardware/drivers/print/printer-connected-to-a-usb-port)
- [Windows device-interface enumeration](https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_device_interface_listw)
- [Default RAW and LPR ports](https://learn.microsoft.com/en-us/troubleshoot/windows-server/printing/standard-port-monitor-for-tcpip)
- [Printers & scanners settings URI](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings#devices)
- [Queue enumeration and caching](https://learn.microsoft.com/en-us/windows/win32/printdocs/enumprinters)
- [Driver enumeration](https://learn.microsoft.com/en-us/windows/win32/printdocs/enumprinterdrivers)
- [Port enumeration](https://learn.microsoft.com/en-us/windows/win32/printdocs/enumports)
- [Windows queue state and attributes](https://learn.microsoft.com/en-us/windows/win32/printdocs/printer-info-2)
- [Windows print job status](https://learn.microsoft.com/en-us/windows/win32/printdocs/job-info-1)
- [Shared-printer connection troubleshooting](https://support.microsoft.com/en-us/windows/hardware/printer/fix-shared-printer-connection-problems-in-windows)
- [PnPUtil driver package staging](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax)
- [Print RPC defaults and settings](https://learn.microsoft.com/en-us/troubleshoot/windows-client/printing/windows-11-rpc-connection-updates-for-print)
- [Point and Print administrator requirements](https://support.microsoft.com/en-us/servicing/os/windows/2021/08/kb5005652-manage-new-point-and-print-default-driver-installation-behavior-cve-2021-34481)

## Known Windows issues

Kumpuni shows known Windows printing issues that match the PC's build and the Windows error, after a check. The list ships in `KnownIssues/known-issues.json` and is refreshed at most once per run from the update feed: for GitHub, the `known-issues.json` asset on the `known-issues` release; for an HTTPS directory, `known-issues.json` next to the feed. If the download fails or the file is invalid, the bundled list is used. Downloaded text is shown as plain text and cannot trigger actions.

To publish a new list without an app release, keep the `known-issues` GitHub release marked as a pre-release so Velopack and GitHub's latest release ignore it:

```powershell
gh release create known-issues --prerelease --title "Kumpuni known issues" --notes "Known Windows printing issues list." KnownIssues\known-issues.json
gh release upload known-issues KnownIssues\known-issues.json --clobber   # later updates
```

Fields: `id`, `title`, `detail`, `status` (`confirmed`, `reported` or `by-design`), optional `url` (HTTPS), `builds`, `minBuild`, `maxBuild`, `minRevision`, `maxRevision` and `codes` (Win32 error numbers, for example `1801` for `0x00000709`).
