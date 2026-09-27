using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using System.Management;
using System.Collections.Generic;
using System.Linq;
using NxeDashboard.Shared;

namespace NXEUsbSetupTool
{
    public partial class Form1 : Form
    {
        private const int WM_DEVICECHANGE = 0x0219;
        private readonly BackgroundWorker worker = new BackgroundWorker();
        private readonly object logSync = new object();
        private readonly string logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NXE USB Setup Tool", "Logs", "setup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");

        // Dedicated standalone Xbox emulators in NXE (NO PPSSPP - PSP uses RetroArch)
        public static readonly string[] DedicatedEmulators = new[]
        {
            "Dolphin",
            "Flycast",
            "XBSX2",
            "Xenia"
        };

        // RetroArch system folders underneath Games\RetroArch\<System>\
        public static readonly string[] RetroArchSystems = RetroArchPlatformManifest.FolderNames;

        // Emulators that require/use BIOS directories
        public static readonly string[] BiosEmulators = new[]
        {
            "Dolphin",
            "Flycast",
            "RetroArch",
            "XBSX2"
        };

        public class DriveItem
        {
            public string DriveLetter { get; set; } // e.g. "G:"
            public string RootPath => DriveLetter.EndsWith("\\") ? DriveLetter : DriveLetter + "\\";
            public string VolumeLabel { get; set; } // e.g. "Xbox"
            public string FileSystem { get; set; }  // e.g. "NTFS"
            public long TotalSizeBytes { get; set; }
            public string FormattedSize { get; set; }

            public override string ToString()
            {
                if (!string.IsNullOrWhiteSpace(VolumeLabel))
                    return $"{DriveLetter} - {VolumeLabel.Trim()} - {FileSystem} - {FormattedSize}";
                else
                    return $"{DriveLetter} - {FileSystem} - {FormattedSize}";
            }
        }

        public Form1()
        {
            InitializeComponent();

            // Dark theme styling
            this.BackColor = Color.FromArgb(30, 30, 30);
            this.ForeColor = Color.White;
            foreach (Control c in this.Controls)
            {
                if (c is Button || c is ComboBox || c is Label || c is ProgressBar)
                    c.BackColor = Color.FromArgb(45, 45, 45);
                if (c is Button || c is Label)
                    c.ForeColor = Color.White;
            }

            // ComboBox custom draw for clean dark theme
            driveBox.ForeColor = Color.White;
            driveBox.DrawMode = DrawMode.OwnerDrawFixed;
            driveBox.DrawItem += DriveBox_DrawItem;

            // BackgroundWorker setup
            worker.WorkerReportsProgress = true;
            worker.WorkerSupportsCancellation = true;
            worker.DoWork += Worker_DoWork;
            worker.ProgressChanged += Worker_ProgressChanged;
            worker.RunWorkerCompleted += Worker_Completed;

            // Initial load of connected drives
            LoadDrives();
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_DEVICECHANGE && (worker == null || !worker.IsBusy))
            {
                LoadDrives();
            }
        }

        private void DriveBox_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= driveBox.Items.Count) return;
            e.DrawBackground();

            object item = driveBox.Items[e.Index];
            bool isPlaceholder = !(item is DriveItem);
            Color textColor = isPlaceholder ? Color.FromArgb(170, 170, 170) : Color.White;

            using (Brush b = new SolidBrush(textColor))
            {
                e.Graphics.DrawString(item.ToString(), e.Font, b, e.Bounds);
            }
            e.DrawFocusRectangle();
        }

        private void RefreshButton_Click(object sender, EventArgs e)
        {
            if (worker.IsBusy) return;
            LoadDrives();
        }

        public void LoadDrives()
        {
            driveBox.Items.Clear();
            var validDrives = new List<DriveItem>();
            var processedLetters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string systemRoot = null;
            try
            {
                systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
            }
            catch { }

            try
            {
                // Step 1: Query USB & Removable / External physical drives via WMI
                using (var diskSearcher = new ManagementObjectSearcher(
                    "SELECT DeviceID, Model, Size, MediaType, InterfaceType FROM Win32_DiskDrive WHERE InterfaceType='USB' OR MediaType LIKE '%External%' OR MediaType LIKE '%Removable%'"))
                {
                    foreach (ManagementObject disk in diskSearcher.Get().Cast<ManagementObject>())
                    {
                        string diskId = disk["DeviceID"]?.ToString();
                        if (string.IsNullOrEmpty(diskId)) continue;

                        var partitionQuery = "ASSOCIATORS OF {Win32_DiskDrive.DeviceID='" + diskId +
                                             "'} WHERE AssocClass = Win32_DiskDriveToDiskPartition";

                        using (var partitionSearcher = new ManagementObjectSearcher(partitionQuery))
                        {
                            foreach (ManagementObject partition in partitionSearcher.Get().Cast<ManagementObject>())
                            {
                                string partId = partition["DeviceID"]?.ToString();
                                if (string.IsNullOrEmpty(partId)) continue;

                                var logicalQuery = "ASSOCIATORS OF {Win32_DiskPartition.DeviceID='" + partId +
                                                   "'} WHERE AssocClass = Win32_LogicalDiskToPartition";

                                using (var logicalSearcher = new ManagementObjectSearcher(logicalQuery))
                                {
                                    foreach (ManagementObject logical in logicalSearcher.Get().Cast<ManagementObject>())
                                    {
                                        string letter = logical["DeviceID"]?.ToString();
                                        if (string.IsNullOrEmpty(letter)) continue;

                                        // Skip system drive
                                        if (!string.IsNullOrEmpty(systemRoot) && letter.StartsWith(systemRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                                            continue;

                                        // Strict NTFS check
                                        DriveItem driveItem = TryCreateNtfsDriveItem(letter, logical["VolumeName"]?.ToString());
                                        if (driveItem != null && processedLetters.Add(driveItem.DriveLetter))
                                        {
                                            validDrives.Add(driveItem);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("WMI search error: " + ex.Message);
            }

            // Step 2: Supplemental check using DriveInfo for any removable drives
            try
            {
                foreach (DriveInfo di in DriveInfo.GetDrives())
                {
                    if (di.DriveType == DriveType.Removable)
                    {
                        string letter = di.Name.TrimEnd('\\');
                        if (!string.IsNullOrEmpty(systemRoot) && letter.StartsWith(systemRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (processedLetters.Contains(letter))
                            continue;

                        DriveItem driveItem = TryCreateNtfsDriveItem(letter, null);
                        if (driveItem != null && processedLetters.Add(driveItem.DriveLetter))
                        {
                            validDrives.Add(driveItem);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("DriveInfo check error: " + ex.Message);
            }

            // Populate UI
            if (validDrives.Count > 0)
            {
                foreach (var d in validDrives)
                {
                    driveBox.Items.Add(d);
                }
                driveBox.SelectedIndex = 0;
                driveBox.Enabled = true;
                prepareButton.Enabled = true;
                statusLabel.Text = "Ready to prepare drive for NXE.";
            }
            else
            {
                driveBox.Items.Add("No compatible NTFS drives detected");
                driveBox.SelectedIndex = 0;
                driveBox.Enabled = false;
                prepareButton.Enabled = false;
                statusLabel.Text = "No compatible NTFS drives detected. Connect an NTFS-formatted USB drive.";
            }
        }

        private static DriveItem TryCreateNtfsDriveItem(string letter, string fallbackLabel)
        {
            try
            {
                string root = letter.EndsWith("\\") ? letter : letter + "\\";
                DriveInfo di = new DriveInfo(root);

                if (!di.IsReady)
                    return null;

                if (di.DriveType == DriveType.CDRom || di.DriveType == DriveType.Network || di.DriveType == DriveType.NoRootDirectory)
                    return null;

                // STRICT NTFS FILTER
                if (!string.Equals(di.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                    return null;

                string volumeLabel = !string.IsNullOrWhiteSpace(di.VolumeLabel)
                    ? di.VolumeLabel.Trim()
                    : (!string.IsNullOrWhiteSpace(fallbackLabel) ? fallbackLabel.Trim() : "");

                long totalSize = di.TotalSize;
                string formattedSize = FormatBytes(totalSize);

                return new DriveItem
                {
                    DriveLetter = letter.TrimEnd('\\'),
                    VolumeLabel = volumeLabel,
                    FileSystem = "NTFS",
                    TotalSizeBytes = totalSize,
                    FormattedSize = formattedSize
                };
            }
            catch
            {
                return null;
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < suffixes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.#} {suffixes[order]}";
        }

        private void PrepareButton_Click(object sender, EventArgs e)
        {
            if (worker.IsBusy)
            {
                worker.CancelAsync();
                prepareButton.Enabled = false;
                statusLabel.Text = "Cancelling emulator package search...";
                return;
            }

            if (driveBox.SelectedItem == null || !(driveBox.SelectedItem is DriveItem selectedItem))
            {
                MessageBox.Show("Please select a valid NTFS drive.", "No Drive Selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string root = selectedItem.RootPath;

            // Pre-flight check
            try
            {
                DriveInfo drive = new DriveInfo(root);
                if (!drive.IsReady)
                {
                    MessageBox.Show($"The selected drive ({selectedItem.DriveLetter}) is not ready or disconnected.", "Drive Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    LoadDrives();
                    return;
                }

                if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show($"The selected drive ({selectedItem.DriveLetter}) is formatted as {drive.DriveFormat}.\n\nOnly NTFS volumes are supported for Xbox/NXE preparation.", "Unsupported File System", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    LoadDrives();
                    return;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to access drive ({selectedItem.DriveLetter}): {ex.Message}", "Drive Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                LoadDrives();
                return;
            }

            progressBar1.Value = 0;
            statusLabel.Text = "Starting NXE drive preparation...";
            prepareButton.Enabled = false;
            prepareButton.Text = "Cancel";
            prepareButton.Enabled = true;
            refreshButton.Enabled = false;

            worker.RunWorkerAsync(root);
        }

        void Worker_DoWork(object sender, DoWorkEventArgs e)
        {
            try
            {
                string root = (string)e.Argument;

            // Stage 1: Validation
            worker.ReportProgress(10, "Validating drive accessibility...");
            DriveInfo drive = new DriveInfo(root);
            if (!drive.IsReady)
                throw new InvalidOperationException("Drive is not ready or has been disconnected.");
            if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Drive filesystem is {drive.DriveFormat}. It must be formatted as NTFS for NXE.");

            // Stage 2: Create NXE folder structure
            worker.ReportProgress(25, "Creating NXE folder structure...");
            CreateNxeFolderStructure(root);

            // Stage 3: Discover, validate, and stage supported emulator packages.
            using (var packageManager = new EmulatorPackageManager(LogPackageEvent,
                (percentage, status) => worker.ReportProgress(percentage, status)))
            {
                packageManager.Prepare(root,
                    () => worker.CancellationPending,
                    AskToDownloadMissingPackages,
                    AskToRetryPackageDownloads);
            }

            // Stage 4: Verify created folders
            worker.ReportProgress(58, "Verifying folder structure...");
            VerifyNxeFolderStructure(root);

            // Stage 5: Apply Xbox UWP Permissions (S-1-15-2-1 ALL APPLICATION PACKAGES)
            worker.ReportProgress(68, "Applying Xbox UWP permissions...");
            var sid = new SecurityIdentifier("S-1-15-2-1");
            var rule = new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow);

            ApplyPermission(root, rule);
            ProcessDirectory(root, rule);

            // Stage 6: Verify permissions
            worker.ReportProgress(90, "Verifying permissions...");
            VerifyPermissions(root, sid);

            worker.ReportProgress(100, "NXE drive setup completed successfully.");
            }
            catch (OperationCanceledException)
            {
                e.Cancel = true;
            }
        }

        private bool AskToDownloadMissingPackages(int foundCount)
        {
            bool answer = false;
            Invoke((MethodInvoker)delegate
            {
                string message = foundCount == 0
                    ? "NXE couldn't find the supported emulator packages on this PC.\n\nWould you like NXE USB Setup Tool to download them for you?"
                    : "NXE couldn't find all supported emulator packages on this PC.\n\nWould you like NXE USB Setup Tool to download the missing packages for you?";
                answer = MessageBox.Show(this, message, "Supported Emulator Packages",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
            });
            return answer;
        }

        private bool AskToRetryPackageDownloads(IReadOnlyList<EmulatorPackageFailure> failures)
        {
            bool retry = false;
            Invoke((MethodInvoker)delegate
            {
                using (var dialog = new DownloadFailureDialog(failures,
                    NxeDashboard.Shared.EmulatorPackageManifest.Definitions
                        .Where(definition => !failures.Any(failure => string.Equals(
                            failure.Definition.Id, definition.Id, StringComparison.OrdinalIgnoreCase)))
                        .Select(definition => definition.Id).ToArray()))
                {
                    dialog.ShowDialog(this);
                    retry = dialog.RetryRequested;
                }
            });
            return retry;
        }

        private void LogPackageEvent(string message)
        {
            try
            {
                lock (logSync)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(logPath));
                    File.AppendAllText(logPath, DateTime.Now.ToString("O") + " " + message + Environment.NewLine);
                }
            }
            catch { }
        }

        public static void CreateNxeFolderStructure(string root)
        {
            // Root-level folders for direct access & dashboard navigation
            CreateFolderTree(root);

            // Also create identical structure inside NXE/ for UWP virtual root
            string nxeRoot = Path.Combine(root, "NXE");
            CreateFolderTree(nxeRoot);
        }

        private static void CreateFolderTree(string basePath)
        {
            // 1. Root and top-level categories
            EnsureDir(basePath);
            EnsureDir(Path.Combine(basePath, "Games"));
            EnsureDir(Path.Combine(basePath, "BIOS"));
            EnsureDir(Path.Combine(basePath, "Saves"));
            EnsureDir(Path.Combine(basePath, "NXE Themes"));
            EnsureDir(Path.Combine(basePath, "Covers"));
            EnsureDir(Path.Combine(basePath, "EmulatorData"));
            EnsureDir(Path.Combine(basePath, "Metadata"));
            EnsureDir(Path.Combine(basePath, "Config"));
            EnsureDir(Path.Combine(basePath, "Cache"));
            EnsureDir(Path.Combine(basePath, "Transfers"));

            // 2. Dedicated emulator folders (under Games, Saves, Covers, EmulatorData)
            foreach (var emu in DedicatedEmulators)
            {
                EnsureDir(Path.Combine(basePath, "Games", emu));
                EnsureDir(Path.Combine(basePath, "Saves", emu));
                EnsureDir(Path.Combine(basePath, "Covers", emu));
                EnsureDir(Path.Combine(basePath, "EmulatorData", emu));
            }

            // 3. RetroArch folder and system subfolders
            EnsureDir(Path.Combine(basePath, "Games", "RetroArch"));
            EnsureDir(Path.Combine(basePath, "Saves", "RetroArch"));
            EnsureDir(Path.Combine(basePath, "Covers", "RetroArch"));
            EnsureDir(Path.Combine(basePath, "EmulatorData", "RetroArch"));
            foreach (var sys in RetroArchSystems)
            {
                EnsureDir(Path.Combine(basePath, "Games", "RetroArch", sys));
            }

            // 4. BIOS subfolders
            foreach (var emu in BiosEmulators)
            {
                EnsureDir(Path.Combine(basePath, "BIOS", emu));
            }
            EnsureDir(Path.Combine(basePath, "BIOS", "RetroArch", "PPSSPP"));
        }

        public static void VerifyNxeFolderStructure(string root)
        {
            var requiredFolders = new List<string>
            {
                Path.Combine(root, "Games"),
                Path.Combine(root, "BIOS"),
                Path.Combine(root, "Saves"),
                Path.Combine(root, "NXE Themes"),
                Path.Combine(root, "Covers"),
                Path.Combine(root, "EmulatorData"),
                Path.Combine(root, "Metadata"),
                Path.Combine(root, "Config"),
                Path.Combine(root, "Cache"),
                Path.Combine(root, "Transfers"),
                Path.Combine(root, "NXE")
            };

            foreach (var emu in DedicatedEmulators)
            {
                requiredFolders.Add(Path.Combine(root, "Games", emu));
                requiredFolders.Add(Path.Combine(root, "Saves", emu));
                requiredFolders.Add(Path.Combine(root, "Covers", emu));
                requiredFolders.Add(Path.Combine(root, "EmulatorData", emu));
            }

            requiredFolders.Add(Path.Combine(root, "Games", "RetroArch"));
            requiredFolders.Add(Path.Combine(root, "Saves", "RetroArch"));
            requiredFolders.Add(Path.Combine(root, "Covers", "RetroArch"));
            requiredFolders.Add(Path.Combine(root, "EmulatorData", "RetroArch"));
            foreach (var sys in RetroArchSystems)
            {
                requiredFolders.Add(Path.Combine(root, "Games", "RetroArch", sys));
            }

            foreach (var emu in BiosEmulators)
            {
                requiredFolders.Add(Path.Combine(root, "BIOS", emu));
            }

            foreach (var folder in requiredFolders)
            {
                if (!Directory.Exists(folder))
                    throw new DirectoryNotFoundException($"Verification failed: Required folder '{folder}' was not created.");
            }
        }

        private static void EnsureDir(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }

        private static void ProcessDirectory(string path, FileSystemAccessRule rule)
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(path))
                {
                    ApplyPermission(dir, rule);
                    ProcessDirectory(dir, rule);
                }
            }
            catch { }
        }

        private static void ApplyPermission(string path, FileSystemAccessRule rule)
        {
            try
            {
                DirectoryInfo d = new DirectoryInfo(path);
                var sec = d.GetAccessControl();
                sec.AddAccessRule(rule);
                d.SetAccessControl(sec);
            }
            catch { }
        }

        public static void VerifyPermissions(string root, SecurityIdentifier sid)
        {
            try
            {
                DirectoryInfo d = new DirectoryInfo(root);
                var sec = d.GetAccessControl();
                var rules = sec.GetAccessRules(true, true, typeof(SecurityIdentifier));
                bool hasRule = false;
                foreach (FileSystemAccessRule r in rules)
                {
                    if (r.IdentityReference.Equals(sid) && (r.FileSystemRights & FileSystemRights.FullControl) != 0)
                    {
                        hasRule = true;
                        break;
                    }
                }
                if (!hasRule)
                    throw new UnauthorizedAccessException("Verification failed: S-1-15-2-1 permission rule was not found on the root drive.");
            }
            catch (Exception ex) when (!(ex is UnauthorizedAccessException))
            {
                // Non-critical ACL inspection failure if environment restricts readback
                System.Diagnostics.Debug.WriteLine("Permission verification check: " + ex.Message);
            }
        }

        void Worker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            progressBar1.Value = Math.Min(100, Math.Max(0, e.ProgressPercentage));
            if (e.UserState is string status)
            {
                statusLabel.Text = status;
            }
        }

        void Worker_Completed(object sender, RunWorkerCompletedEventArgs e)
        {
            prepareButton.Text = "Prepare Drive for NXE";
            prepareButton.Enabled = driveBox.SelectedItem is DriveItem;
            refreshButton.Enabled = true;

            if (e.Cancelled)
            {
                statusLabel.Text = "Setup cancelled. Existing drive contents were preserved.";
                return;
            }

            if (e.Error != null)
            {
                MessageBox.Show(e.Error.Message, "Setup Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                statusLabel.Text = "Failed: " + e.Error.Message;
                return;
            }

            progressBar1.Value = 100;
            statusLabel.Text = "NXE drive setup completed successfully.";

            MessageBox.Show(
@"NXE USB Drive Setup Completed Successfully!

The drive structure has been created and verified:
• Games (Xenia, XBSX2, Dolphin, Flycast, and RetroArch system folders)
• BIOS (RetroArch, Dolphin, XBSX2, Flycast)
• Saves (Separated per emulator)
• NXE Themes (Custom themes folder)
• Covers, EmulatorData, Metadata, Config, Cache, Transfers
• Xbox UWP permissions (ALL APPLICATION PACKAGES) applied

When plugging the drive into your Xbox console,
select 'Use for Media' (DO NOT select 'Games & Apps').

HAPPY GAMING!",
                "NXE Setup Complete",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }
}
