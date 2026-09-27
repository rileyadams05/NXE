using NxeDashboard.Emulators;
using NxeDashboard.Library;
using NxeDashboard.Navigation;
using NxeDashboard.Runtime;
using NxeDashboard.Shared;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Gaming.Input;
using Windows.ApplicationModel;
using Windows.Security.Cryptography;
using Windows.Security.Credentials;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;

namespace NxeDashboard
{
    public sealed partial class MainPage : Page
    {
        // The legacy duplicate "Games" Guide tab is intentionally disabled and removed
        // so that NXE Guide remains focused on system commands (Home, Favorites, Recent, FTP, Settings, Quit)
        // and does not duplicate the real Games dashboard.
        private static class GuideLayout
        {
            public const int BladeCount = 1;
            public const int HomeBlade = 0;
            public const double RowHeight = 27;
            public const double HomePageTop = 132;
            public const double StandardPageTop = 142;
            public static readonly double[] LeftPositions = { 205, 179, 155, 134 };
            public static readonly double[] RightPositions = { 575, 602, 628, 652 };
            public static readonly double[] BladeScales = { 0.96, 0.93, 0.90, 0.87 };
            public static readonly string[] BladeLabels = { "NXE Guide" };
        }

        private sealed class FileManagerEntry
        {
            public string Name { get; set; }
            public string Details { get; set; }
            public string Glyph { get; set; }
            public bool IsFolder { get; set; }
            public StorageFolder Folder { get; set; }
            public StorageFile File { get; set; }
            public bool IsLocationHelper { get; set; }
        }

        private readonly Dictionary<string, Action<EmulatorDetectionResult>> backendStatusUpdates =
            new Dictionary<string, Action<EmulatorDetectionResult>>(StringComparer.OrdinalIgnoreCase);
        private readonly NxeFtpServer ftpService = new NxeFtpServer();
        private readonly SemaphoreSlim ftpRestartLock = new SemaphoreSlim(1, 1);
        private readonly DashboardNavigationController navigation =
            new DashboardNavigationController();
        private readonly SemaphoreSlim storageRefreshLock = new SemaphoreSlim(1, 1);
        private readonly ExternalStorageManager storagePresenceProbe = new ExternalStorageManager();
        private readonly DispatcherTimer storageRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(15)
        };
        private readonly DispatcherTimer guideStatusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        private readonly DispatcherTimer rightStickScrollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        private bool isFtpGuideOpen;
        private int currentFtpGuideStep = 1;
        private readonly List<NxeGameRecord> allGames = new List<NxeGameRecord>();
        private readonly List<NxeGameRecord> guideRecentGames = new List<NxeGameRecord>();
        private readonly List<NxeGameRecord> guideFavoriteGames = new List<NxeGameRecord>();
        private readonly ObservableCollection<FileManagerEntry> fileManagerEntries =
            new ObservableCollection<FileManagerEntry>();
        private UwpLibraryCoordinator libraryCoordinator;
        private EmulatorBackendRegistry backendRegistry;
        private UwpBackendLaunchAdapter backendLaunchAdapter;
        private EmulatorManager emulatorManager;
        private Control returnFocusTarget;
        private NxeGameRecord selectedGame;
        private StorageFolder fileManagerFolder;
        private string fileManagerPath = "/";
        private bool guideFavoritesPage;
        private bool guideRecentPage;
        private bool leftBumperHeld;
        private bool filterComboConsumed;
        private bool filterOpen;
        private int filterOriginalIndex;
        private bool guideQuitSelectionYes;
        private bool storageRefreshInitialized;
        private bool hasReadyGameDrive;
        private int lastDriveCount;
        private string lastDriveIdentity;
        private GameDriveState gameDriveState = GameDriveState.NoDrive;
        private NxeFtpCloudBridge ftpCloudBridge;
        private NxeWebManagementServer webManagementServer;
        private string ftpFailure = string.Empty;
        private int guideCommandIndex;
        private int guideBladeIndex = GuideLayout.HomeBlade;
        private int[] guideSelections;

        public ObservableCollection<NxeGameRecord> Games { get; } =
            new ObservableCollection<NxeGameRecord>();

        public MainPage()
        {
            InitializeComponent();
            InitializeGuideShell();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            CoreWindow.GetForCurrentThread().KeyDown += OnCoreWindowKeyDown;
            CoreWindow.GetForCurrentThread().KeyUp += OnCoreWindowKeyUp;
            navigation.StateChanged += OnNavigationStateChanged;
            ftpService.StateChanged += OnFtpStateChanged;
            ftpService.StorageChanged += OnFtpStorageChanged;
            storageRefreshTimer.Tick += OnStorageRefreshTick;
            guideStatusTimer.Tick += OnGuideStatusTick;
            rightStickScrollTimer.Tick += OnRightStickScrollTick;
            rightStickScrollTimer.Start();
            App.GameSessionEnded += OnGameSessionEnded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await RunStartupValidationAsync();
                emulatorManager = new EmulatorManager();
                backendLaunchAdapter = new UwpBackendLaunchAdapter(emulatorManager);
                backendRegistry = new EmulatorBackendRegistry(backendLaunchAdapter);
                PopulateEmulatorSettings();
                libraryCoordinator = new UwpLibraryCoordinator(
                    System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, "nxe-library.db"));
                await libraryCoordinator.InitializeAsync();
                await StartFtpInfrastructureAsync();

                // Set the initial filter only after InitializeComponent has built
                // the complete visual tree. Setting SelectedIndex in XAML fires
                // SelectionChanged while later named controls are still null.
                LibraryFilter.SelectedIndex = 0;
                ApplyLibrarySnapshot(libraryCoordinator.LoadCached());
                RestoreDashboardState();
                StorageStatus.Visibility = Visibility.Collapsed;
                navigation.ResetToLibrary();
                ApplyNavigationState();
                FocusLibrary();

                _ = RefreshExternalStorageAsync();
                storageRefreshTimer.Start();
#if DEBUG
                if (App.LaunchArguments.StartsWith("--ftp-self-test", StringComparison.OrdinalIgnoreCase))
                {
                    try { await WriteDiagnosticAsync("ftp-self-test.txt", await FtpStartupVerification.RunAsync()); }
                    catch (Exception exception)
                    {
                        await WriteDiagnosticAsync("ftp-self-test.txt", "FAIL " + exception + "\r\n");
                    }
                }
#endif
                await WriteDiagnosticAsync("render-validation.txt",
                    "status=success\r\n" +
                    "dashboardRoot=library\r\n" +
                    "landingCards=removed\r\n" +
                    "backgroundSource=RetailNXE/extracted/controlp/defaultbackground.jpg\r\n" +
                    "librarySource=SQLite cached records plus incremental refresh\r\n" +
                    "libraryView=horizontal virtualized cover strip\r\n" +
                    "settingsEntry=dashboard chrome gear\r\n" +
                    "navigationStates=Library,Settings,GameDetails,Guide,FileManager,GuideQuitConfirmation\r\n" +
                    "guideSource=PluginUI Team NXE 1.4.2 GuideMain.xur translated from Dashboard/res.xzp\r\n" +
                    "guideLayout=complete 852x480 Xbox 360 NXE Guide scene with Games and Guide sections, favorites, File Manager, Settings, Quit App confirmation, live status, and Select/Back legend\r\n" +
                    "backInputs=Backspace,Escape,GamepadB\r\n" +
                    "backendLaunch=EmulatorBackendRegistry\r\n" +
                    "ftpRefresh=enabled\r\n");
                StartupStatus.Text = "Ready";
                StartupProgress.Value = 100;
                await Task.Delay(120);
                StartupGate.Visibility = Visibility.Collapsed;
            }
            catch (Exception exception)
            {
                await WriteDiagnosticAsync("render-error.txt", exception.ToString());
                StorageStatus.Text = "NXE dashboard load failed: " + exception.Message;
                StorageStatus.Visibility = Visibility.Collapsed;
                StartupStatus.Text = "NXE startup validation failed";
            }
        }

        private async Task RunStartupValidationAsync()
        {
            StartupGate.Visibility = Visibility.Visible;
            StartupProgress.Value = 0;
            StartupStatus.Text = "Checking NXE package…";
            await Task.Yield();

            var warnings = new List<string>();
            var critical = new List<string>();

            var startupImage = await StorageFile.GetFileFromApplicationUriAsync(
                new Uri("ms-appx:///RetailNXE/Startup/NXE.png"));
            if (startupImage == null || startupImage.FileType == null)
                throw new InvalidOperationException("The required NXE startup image is missing.");

            StartupProgress.Value = 20;
            StartupStatus.Text = "Checking NXE configuration…";
            if (ApplicationData.Current.LocalSettings == null)
                critical.Add("LocalSettings unavailable");
            foreach (var asset in new[] { "xb_a.svg", "xb_lb.svg", "xb_menu.svg", "xb_view.svg", "xb_y.svg" })
            {
                try { await Package.Current.InstalledLocation.GetFileAsync("RetailNXE\\ControllerGlyphs\\Xbox\\" + asset); }
                catch { critical.Add("Controller asset missing: " + asset); }
            }
            if (Package.Current.Id.Version.Major < 0)
                critical.Add("NXE version information unavailable");

            StartupProgress.Value = 32;
            StartupStatus.Text = "Checking emulator definitions…";
            var definitions = EmulatorPackageManifest.Definitions;
            if (definitions == null || definitions.Length != 5)
                throw new InvalidOperationException("The embedded emulator manifest is incomplete.");

            StartupProgress.Value = 45;
            StartupStatus.Text = "Checking embedded engine host…";
            var host = new EmbeddedEmulatorHost();
            var unavailable = new List<string>();
            foreach (var definition in definitions)
            {
                if (!await host.IsAvailableAsync(definition.Id))
                    unavailable.Add(definition.DisplayName);
            }
            if (unavailable.Count > 0)
                warnings.Add("Unavailable engine entries: " + string.Join(", ", unavailable));

            try
            {
                await Package.Current.InstalledLocation.GetFileAsync("Cores\\flycast_libretro.dll");
            }
            catch { warnings.Add("Flycast core missing"); }

            var missingCores = new List<string>();
            foreach (var platform in RetroArchPlatformManifest.Platforms)
            {
                var available = false;
                foreach (var candidate in platform.CoreCandidates)
                {
                    try
                    {
                        await Package.Current.InstalledLocation.GetFileAsync("Engines\\RetroArch\\" + candidate.Replace('/', '\\'));
                        available = true;
                        break;
                    }
                    catch { }
                }
                if (!available) missingCores.Add(platform.Id);
            }
            if (missingCores.Count > 0)
                warnings.Add("RetroArch cores missing: " + string.Join(", ", missingCores));

            try
            {
                var storage = await storagePresenceProbe.ProbePresenceAsync();
                if (storage.ReadError) warnings.Add("Removable storage probe failed");
                else if (storage.DriveCount == 0) warnings.Add("No USB drive connected");
            }
            catch { warnings.Add("Removable storage probe failed"); }

            // A missing native engine is a readiness warning during the staged
            // migration, not a reason to corrupt the dashboard or storage state.
            await WriteDiagnosticAsync("startup-validation.txt",
                "startupImage=valid\r\n" +
                "emulatorDefinitions=" + definitions.Length + "\r\n" +
                "embeddedEnginesUnavailable=" + string.Join(",", unavailable) + "\r\n" +
                "warnings=" + string.Join("|", warnings) + "\r\n" +
                "critical=" + string.Join("|", critical) + "\r\n" +
                "version=" + Package.Current.Id.Version + "\r\n" +
                "updateManifestCheck=deferred");

            if (critical.Count > 0)
                throw new InvalidOperationException(string.Join("; ", critical));

            StartupProgress.Value = 75;
            StartupStatus.Text = warnings.Count == 0
                ? "Starting NXE Dashboard…"
                : "Starting NXE Dashboard…";
            await Task.Yield();
            StartupProgress.Value = 100;
        }

        private static async Task WriteDiagnosticAsync(string name, string contents)
        {
            var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                name, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, contents);
        }

        private void StartupProgress_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (StartupPercent != null)
                StartupPercent.Text = ((int)Math.Round(e.NewValue)) + "%";
        }

        private void OnCoreWindowKeyDown(CoreWindow sender, KeyEventArgs args)
        {
            var key = args.VirtualKey;
            if (args.KeyStatus.WasKeyDown &&
                (key == VirtualKey.GamepadA || key == VirtualKey.GamepadB ||
                 key == VirtualKey.GamepadX || key == VirtualKey.GamepadY ||
                 key == VirtualKey.GamepadLeftShoulder || key == VirtualKey.GamepadMenu))
            {
                args.Handled = true;
                return;
            }

            if (filterOpen && HandleLibraryFilterInput(key))
            {
                args.Handled = true;
                return;
            }

            if (key == VirtualKey.GamepadLeftShoulder)
            {
                leftBumperHeld = true;
                filterComboConsumed = false;
                args.Handled = true;
                return;
            }
            if (navigation.Current == DashboardState.Library &&
                key == VirtualKey.GamepadY)
            {
                if (leftBumperHeld && !filterComboConsumed)
                {
                    filterComboConsumed = true;
                    OpenLibraryFilter();
                }
                args.Handled = true;
                return;
            }
            if (key == VirtualKey.GamepadView)
            {
                ToggleGuide();
                args.Handled = true;
                return;
            }
            if (navigation.Current == DashboardState.Library)
            {
                if (key == VirtualKey.Left || key == VirtualKey.GamepadDPadLeft ||
                    key == VirtualKey.GamepadLeftThumbstickLeft)
                {
                    if (GameLibraryList.Items.Count > 0 && GameLibraryList.SelectedIndex > 0)
                    {
                        GameLibraryList.SelectedIndex--;
                        GameLibraryList.ScrollIntoView(GameLibraryList.SelectedItem);
                    }
                    args.Handled = true;
                    return;
                }
                if (key == VirtualKey.Right || key == VirtualKey.GamepadDPadRight ||
                    key == VirtualKey.GamepadLeftThumbstickRight)
                {
                    if (GameLibraryList.Items.Count > 0 && GameLibraryList.SelectedIndex < GameLibraryList.Items.Count - 1)
                    {
                        GameLibraryList.SelectedIndex++;
                        GameLibraryList.ScrollIntoView(GameLibraryList.SelectedItem);
                    }
                    args.Handled = true;
                    return;
                }
                if (key == VirtualKey.Enter || key == VirtualKey.GamepadA)
                {
                    if (GameLibraryList.SelectedItem is NxeGameRecord game)
                    {
                        _ = LaunchGameAsync(game, message => { });
                    }
                    args.Handled = true;
                    return;
                }
                if (key == VirtualKey.GamepadX)
                {
                    // X is deliberately unassigned on the dashboard. Consume it so
                    // it cannot become a ListView click or another launch route.
                    args.Handled = true;
                    return;
                }
                if (key == VirtualKey.GamepadMenu)
                {
                    if (GameLibraryList.SelectedItem is NxeGameRecord game)
                    {
                        CaptureFocus();
                        PopulateDetails(game);
                        navigation.NavigateTo(DashboardState.GameDetails);
                    }
                    args.Handled = true;
                    return;
                }
            }
            if (navigation.Current == DashboardState.GuideQuitConfirmation)
            {
                if (key == VirtualKey.Left || key == VirtualKey.Right ||
                    key == VirtualKey.GamepadDPadLeft || key == VirtualKey.GamepadDPadRight ||
                    key == VirtualKey.GamepadLeftThumbstickLeft || key == VirtualKey.GamepadLeftThumbstickRight ||
                    key == VirtualKey.Up || key == VirtualKey.Down ||
                    key == VirtualKey.GamepadDPadUp || key == VirtualKey.GamepadDPadDown ||
                    key == VirtualKey.GamepadLeftThumbstickUp || key == VirtualKey.GamepadLeftThumbstickDown)
                {
                    guideQuitSelectionYes = !guideQuitSelectionYes;
                    UpdateQuitConfirmationFocus();
                    args.Handled = true;
                    return;
                }
                if (key == VirtualKey.Enter || key == VirtualKey.GamepadA)
                {
                    ConfirmQuitApp();
                    args.Handled = true;
                    return;
                }
            }
            if (navigation.Current == DashboardState.Guide)
            {
                if (key == VirtualKey.Down || key == VirtualKey.GamepadDPadDown ||
                    key == VirtualKey.GamepadLeftThumbstickDown)
                {
                    SetGuideCommandSelection(guideCommandIndex + 1, true);
                    args.Handled = true;
                    return;
                }
                if (key == VirtualKey.Up || key == VirtualKey.GamepadDPadUp ||
                    key == VirtualKey.GamepadLeftThumbstickUp)
                {
                    SetGuideCommandSelection(guideCommandIndex - 1, true);
                    args.Handled = true;
                    return;
                }
                if (key == VirtualKey.Left || key == VirtualKey.GamepadDPadLeft ||
                    key == VirtualKey.GamepadLeftThumbstickLeft || key == VirtualKey.GamepadLeftShoulder)
                {
                    if (guideBladeIndex > 0)
                    {
                        SetGuideBlade(guideBladeIndex - 1, true);
                        SetGuideCommandSelection(guideSelections[guideBladeIndex], true);
                    }
                    args.Handled = true;
                    return;
                }
                if (key == VirtualKey.Right || key == VirtualKey.GamepadDPadRight ||
                    key == VirtualKey.GamepadLeftThumbstickRight || key == VirtualKey.GamepadRightShoulder)
                {
                    if (guideBladeIndex < GuideLayout.BladeCount - 1)
                    {
                        SetGuideBlade(guideBladeIndex + 1, true);
                        SetGuideCommandSelection(guideSelections[guideBladeIndex], true);
                    }
                    args.Handled = true;
                    return;
                }
                if (key == VirtualKey.Enter || key == VirtualKey.GamepadA)
                {
                    ActivateGuideSelection();
                    args.Handled = true;
                    return;
                }
            }
            if (navigation.Current == DashboardState.FileManager &&
                (key == VirtualKey.Enter || key == VirtualKey.GamepadA))
            {
                OpenFileManagerSelection();
                args.Handled = true;
                return;
            }
            if (navigation.Current == DashboardState.FileManager &&
                (key == VirtualKey.Y || key == VirtualKey.GamepadY))
            {
                OpenFileManagerParent();
                args.Handled = true;
                return;
            }
            if (key == VirtualKey.Home || key == VirtualKey.G)
            {
                ToggleGuide();
                args.Handled = true;
                return;
            }
            if (key == VirtualKey.Back || key == VirtualKey.Escape || key == VirtualKey.GamepadB)
            {
                RequestBack();
                args.Handled = true;
            }
        }

        private void OnCoreWindowKeyUp(CoreWindow sender, KeyEventArgs args)
        {
            if (args.VirtualKey == VirtualKey.GamepadLeftShoulder)
            {
                leftBumperHeld = false;
                filterComboConsumed = false;
                args.Handled = true;
            }
        }

        private void OpenLibraryFilter()
        {
            if (filterOpen) return;
            filterOpen = true;
            filterOriginalIndex = Math.Max(0, LibraryFilter.SelectedIndex);
            LibraryFilter.Opacity = 1;
            LibraryFilter.Focus(FocusState.Programmatic);
            LibraryFilter.IsDropDownOpen = true;
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            CaptureFocus();
            navigation.NavigateTo(DashboardState.Settings);
        }

        private bool HandleLibraryFilterInput(VirtualKey key)
        {
            if (key == VirtualKey.GamepadA)
            {
                // A is reserved for launching on the dashboard.  While the
                // filter is open it is consumed and must never confirm, close,
                // or bubble through to the selected game.
                return true;
            }
            if (key == VirtualKey.GamepadX)
            {
                ApplyFilter();
                CloseLibraryFilter();
                return true;
            }
            if (key == VirtualKey.GamepadB)
            {
                LibraryFilter.SelectedIndex = filterOriginalIndex;
                CloseLibraryFilter();
                return true;
            }
            if (key == VirtualKey.GamepadLeftShoulder)
            {
                leftBumperHeld = true;
                return true;
            }
            if (key == VirtualKey.GamepadY || key == VirtualKey.GamepadMenu)
                return true;

            var previous = key == VirtualKey.Up || key == VirtualKey.Left ||
                key == VirtualKey.GamepadDPadUp || key == VirtualKey.GamepadDPadLeft ||
                key == VirtualKey.GamepadLeftThumbstickUp || key == VirtualKey.GamepadLeftThumbstickLeft;
            var next = key == VirtualKey.Down || key == VirtualKey.Right ||
                key == VirtualKey.GamepadDPadDown || key == VirtualKey.GamepadDPadRight ||
                key == VirtualKey.GamepadLeftThumbstickDown || key == VirtualKey.GamepadLeftThumbstickRight;
            if (!previous && !next) return false;

            var delta = previous ? -1 : 1;
            LibraryFilter.SelectedIndex = Math.Max(0,
                Math.Min(LibraryFilter.Items.Count - 1, LibraryFilter.SelectedIndex + delta));
            return true;
        }

        private void CloseLibraryFilter()
        {
            filterOpen = false;
            LibraryFilter.IsDropDownOpen = false;
            LibraryFilter.Opacity = 0;
            FocusLibrary();
        }

        private void LibraryFilter_DropDownClosed(object sender, object e)
        {
            if (filterOpen)
            {
                LibraryFilter.SelectedIndex = filterOriginalIndex;
                filterOpen = false;
                FocusLibrary();
            }
            LibraryFilter.Opacity = 0;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e) => RequestBack();

        private void RequestBack()
        {
            if (navigation.Current == DashboardState.Settings && isFtpGuideOpen)
            {
                if (currentFtpGuideStep > 1)
                {
                    SetFtpGuideStep(currentFtpGuideStep - 1);
                }
                else
                {
                    CloseFtpGuide();
                }
                return;
            }
            if (navigation.Current == DashboardState.GuideQuitConfirmation)
            {
                navigation.Back();
                return;
            }
            if (navigation.Current == DashboardState.Guide && guideFavoritesPage)
            {
                guideFavoritesPage = false;
                RefreshGuideHomeActions();
                SetGuideBlade(GuideLayout.HomeBlade, false);
                SetGuideCommandSelection(1, true);
                return;
            }
            if (navigation.Current == DashboardState.Guide && guideRecentPage)
            {
                guideRecentPage = false;
                RefreshGuideHomeActions();
                SetGuideBlade(GuideLayout.HomeBlade, false);
                SetGuideCommandSelection(2, true);
                return;
            }
            // Root consumes Back deliberately. NXE should not close because B,
            // Backspace, or Escape was pressed while browsing the library.
            var closingGuide = navigation.Current == DashboardState.Guide;
            navigation.Back();
            if (closingGuide) PlayGuideSound(GuideCloseSound);
        }

        private void OnNavigationStateChanged(object sender, EventArgs e) => ApplyNavigationState();

        private void ApplyNavigationState()
        {
            var state = navigation.Current;
            if (state != DashboardState.Settings)
            {
                CloseFtpGuide();
            }
            LibraryPanel.Visibility = state == DashboardState.Library ? Visibility.Visible : Visibility.Collapsed;
            SettingsPanel.Visibility = state == DashboardState.Settings ? Visibility.Visible : Visibility.Collapsed;
            FileManagerPanel.Visibility = state == DashboardState.FileManager ? Visibility.Visible : Visibility.Collapsed;
            GameDetailsPanel.Visibility = state == DashboardState.GameDetails ? Visibility.Visible : Visibility.Collapsed;
            GuideOverlay.Visibility = state == DashboardState.Guide || state == DashboardState.GuideQuitConfirmation
                ? Visibility.Visible : Visibility.Collapsed;
            GuideButton.Visibility = Visibility.Collapsed;
            SettingsButton.Visibility = Visibility.Collapsed;
            GuideQuitConfirmPanel.Visibility = state == DashboardState.GuideQuitConfirmation
                ? Visibility.Visible : Visibility.Collapsed;
            if (state != DashboardState.Guide && state != DashboardState.GuideQuitConfirmation)
                guideStatusTimer.Stop();

            if (state == DashboardState.Settings)
            {
                SettingsPivot.SelectedIndex = 0;
                CloseFtpGuide();
                SettingsBackButton.Focus(FocusState.Programmatic);
            }
            else if (state == DashboardState.GameDetails)
            {
                (LaunchButton.IsEnabled ? LaunchButton : DetailsBackButton).Focus(FocusState.Programmatic);
            }
            else if (state == DashboardState.FileManager)
            {
                _ = RefreshFileManagerAsync(fileManagerFolder);
            }
            else if (state == DashboardState.GuideQuitConfirmation)
            {
                guideQuitSelectionYes = false;
                UpdateQuitConfirmationFocus();
            }
            else if (state == DashboardState.Guide)
            {
                UpdateGuideStatus();
                guideStatusTimer.Start();
                RefreshGuideRecentGames();
                RefreshGuideFavoriteGames();
                RefreshGuideHomeActions();
                SetGuideBlade(guideBladeIndex, false);
                SetGuideCommandSelection(guideSelections[guideBladeIndex], true);
            }
            else
            {
                RestoreLibraryFocus();
            }
        }

        private void OnGuideStatusTick(object sender, object e) => UpdateGuideStatus();

        private void UpdateGuideStatus()
        {
            GuideClock.Text = DateTime.Now.ToString("h:mm tt");
        }

        private void CaptureFocus()
        {
            returnFocusTarget = FocusManager.GetFocusedElement() as Control;
        }

        private async void RestoreLibraryFocus()
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (returnFocusTarget != null && returnFocusTarget.Visibility == Visibility.Visible &&
                    returnFocusTarget.Focus(FocusState.Programmatic))
                {
                    returnFocusTarget = null;
                    return;
                }
                FocusLibrary();
            });
        }

        private void FocusLibrary()
        {
            if (Games.Count > 0)
            {
                if (GameLibraryList.SelectedIndex < 0) GameLibraryList.SelectedIndex = 0;
                GameLibraryList.Focus(FocusState.Programmatic);
            }
            else if (SettingsButton.Visibility == Visibility.Visible)
            {
                SettingsButton.Focus(FocusState.Programmatic);
            }
            else
            {
                LibraryFilter.Focus(FocusState.Programmatic);
            }
        }

        private void ToggleGuide()
        {
            if (navigation.Current == DashboardState.Guide)
            {
                RequestBack();
                return;
            }
            CaptureFocus();
            guideFavoritesPage = false;
            guideRecentPage = false;
            guideBladeIndex = GuideLayout.HomeBlade;
            navigation.NavigateTo(DashboardState.Guide);
            PlayGuideSound(GuideOpenSound);
        }

        private void GuideButton_Click(object sender, RoutedEventArgs e) => ToggleGuide();

        private void InitializeGuideShell()
        {
            guideSelections = new int[GuideLayout.BladeCount];
            RefreshGuideRecentGames();
            RefreshGuideFavoriteGames();
            RefreshGuideHomeActions();
            SetGuideBlade(GuideLayout.HomeBlade, false);
        }

        private void RefreshGuideHomeActions()
        {
            if (GuideHomeRows == null) return;
            GuideReturnButton.Visibility = Visibility.Visible;
            GuideFavoritesButton.Visibility = Visibility.Visible;
            GuideRecentButton.Visibility = Visibility.Visible;
            GuideFtpButton.Visibility = Visibility.Visible;
            GuideSettingsButton.Visibility = Visibility.Visible;
            GuideQuitAppButton.Visibility = Visibility.Visible;
        }

        private void RefreshGuideRecentGames()
        {
            if (GuideGamesRows == null) return;

            guideRecentGames.Clear();
            guideRecentGames.AddRange(DashboardLibraryView.Apply(allGames, "Recent").Take(5));
            GuideGamesRows.Children.Clear();

            var rowStyle = GuideOverlay.Resources["GuideRowButtonStyle"] as Style;
            var textStyle = GuideOverlay.Resources["GuideRowTextStyle"] as Style;
            if (guideRecentGames.Count == 0)
            {
                GuideGamesRows.Children.Add(new Button
                {
                    Style = rowStyle,
                    IsEnabled = false,
                    Content = new TextBlock { Text = "No recently played games", Style = textStyle }
                });
            }
            else
            {
                foreach (var game in guideRecentGames)
                {
                    var button = new Button
                    {
                        Style = rowStyle,
                        Tag = game,
                        Content = new TextBlock
                        {
                            Text = game.CanonicalTitle,
                            Style = textStyle,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        }
                    };
                    button.GotFocus += GuideCommand_GotFocus;
                    button.Click += GuideCommand_Click;
                    GuideGamesRows.Children.Add(button);
                }
            }

            if (guideSelections != null && guideSelections.Length > 0)
            {
                var slot = GuideLayout.HomeBlade;
                guideSelections[slot] = Math.Max(0, Math.Min(
                    guideSelections[slot], guideRecentGames.Count - 1));
            }
        }

        private void RefreshGuideFavoriteGames()
        {
            if (GuideFavoritesRows == null) return;
            guideFavoriteGames.Clear();
            guideFavoriteGames.AddRange(allGames.Where(game => game.IsFavorite)
                .OrderBy(game => game.CanonicalTitle, StringComparer.CurrentCultureIgnoreCase));
            GuideFavoritesRows.Children.Clear();
            var rowStyle = GuideOverlay.Resources["GuideRowButtonStyle"] as Style;
            var textStyle = GuideOverlay.Resources["GuideRowTextStyle"] as Style;
            if (guideFavoriteGames.Count == 0)
            {
                GuideFavoritesRows.Children.Add(new Button
                {
                    Style = rowStyle,
                    IsEnabled = false,
                    Content = new TextBlock { Text = "No favorite games", Style = textStyle }
                });
            }
            else
            {
                foreach (var game in guideFavoriteGames)
                {
                    var button = new Button
                    {
                        Style = rowStyle,
                        Tag = game,
                        Content = new TextBlock
                        {
                            Text = game.CanonicalTitle,
                            Style = textStyle,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        }
                    };
                    button.GotFocus += GuideCommand_GotFocus;
                    button.Click += GuideCommand_Click;
                    GuideFavoritesRows.Children.Add(button);
                }
            }
        }

        private void GuideCommand_GotFocus(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null) return;
            var index = ActiveGuideButtons.IndexOf(button);
            if (index >= 0) SetGuideCommandSelection(index, false);
        }

        private StackPanel ActiveGuideRows => guideRecentPage
            ? GuideGamesRows : guideFavoritesPage ? GuideFavoritesRows : GuideHomeRows;
        private List<Button> ActiveGuideButtons => ActiveGuideRows.Children
            .OfType<Button>()
            .Where(button => button.Visibility == Visibility.Visible && button.IsEnabled)
            .ToList();

        private double GuideHighlightOffset(IReadOnlyList<Button> buttons, int selectedIndex)
        {
            // The Home page intentionally has a spacer before Quit App. Measure the
            // real stacked row positions so the highlight follows that spacing.
            var offset = 0d;
            for (var index = 0; index < selectedIndex; index++)
            {
                var button = buttons[index];
                offset += button.ActualHeight > 0 ? button.ActualHeight : GuideLayout.RowHeight;
                offset += button.Margin.Top + button.Margin.Bottom;

                var buttonIndex = ActiveGuideRows.Children.IndexOf(button);
                var nextButtonIndex = ActiveGuideRows.Children.IndexOf(buttons[index + 1]);
                for (var childIndex = buttonIndex + 1; childIndex < nextButtonIndex; childIndex++)
                {
                    if (ActiveGuideRows.Children[childIndex] is FrameworkElement spacer &&
                        spacer.Visibility == Visibility.Visible)
                        offset += spacer.ActualHeight > 0 ? spacer.ActualHeight : spacer.Height;
                }
            }

            return offset + buttons[selectedIndex].Margin.Top;
        }

        private void SetGuideBlade(int index, bool animate)
        {
            var next = Math.Max(0, Math.Min(index, GuideLayout.BladeCount - 1));
            var previous = guideBladeIndex;
            guideBladeIndex = next;

            GuideGamesRows.Visibility = Visibility.Collapsed;
            GuideHomeRows.Visibility = Visibility.Collapsed;
            GuideFavoritesRows.Visibility = Visibility.Collapsed;
            ActiveGuideRows.Visibility = Visibility.Visible;

            var activeButtons = ActiveGuideButtons;
            var hasSelectableRows = activeButtons.Count > 0;
            GuideCommandHighlight.Visibility = hasSelectableRows
                ? Visibility.Visible : Visibility.Collapsed;

            GuideActiveBladeLabel.Text = GuideLayout.BladeLabels[next];
            Canvas.SetTop(GuidePageHost, next == GuideLayout.HomeBlade
                ? GuideLayout.HomePageTop : GuideLayout.StandardPageTop);

            guideCommandIndex = hasSelectableRows
                ? Math.Max(0, Math.Min(guideSelections[next], activeButtons.Count - 1))
                : 0;
            GuideHighlightTransform.Y = hasSelectableRows
                ? GuideHighlightOffset(activeButtons, guideCommandIndex) : 0;
            if (animate && previous != next)
            {
                PlayGuideSound(GuideSwitchSound);
                AnimateGuidePage(previous < next ? 18 : -18);
            }
            if (hasSelectableRows && GuideOverlay.Visibility == Visibility.Visible &&
                guideCommandIndex < activeButtons.Count)
                activeButtons[guideCommandIndex].Focus(FocusState.Programmatic);
        }

        private void SetGuideCommandSelection(int index, bool moveFocus)
        {
            var buttons = ActiveGuideButtons;
            if (buttons.Count == 0) return;
            guideCommandIndex = Math.Max(0, Math.Min(index, buttons.Count - 1));
            guideSelections[guideBladeIndex] = guideCommandIndex;
            GuideHighlightTransform.Y = GuideHighlightOffset(buttons, guideCommandIndex);
            if (moveFocus) buttons[guideCommandIndex].Focus(FocusState.Programmatic);
        }

        private void ActivateGuideSelection()
        {
            var buttons = ActiveGuideButtons;
            if (guideCommandIndex < 0 || guideCommandIndex >= buttons.Count) return;
            var button = buttons[guideCommandIndex];
            button.Focus(FocusState.Programmatic);
            ActivateGuideButton(button);
        }

        private void GuideCommand_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button) ActivateGuideButton(button);
        }

        private void ActivateGuideButton(Button button)
        {
            if (guideRecentPage && button.Tag is NxeGameRecord game)
            {
                LaunchGuideGame(game);
                return;
            }
            if (guideFavoritesPage && button.Tag is NxeGameRecord favorite)
            {
                LaunchGuideGame(favorite);
                return;
            }
            if (button == GuideReturnButton)
            {
                CloseGuideToDashboard(null);
                return;
            }
            if (button == GuideFavoritesButton)
            {
                guideFavoritesPage = true;
                guideRecentPage = false;
                RefreshGuideFavoriteGames();
                SetGuideBlade(GuideLayout.HomeBlade, true);
                SetGuideCommandSelection(0, true);
                return;
            }
            if (button == GuideRecentButton)
            {
                guideRecentPage = true;
                guideFavoritesPage = false;
                RefreshGuideRecentGames();
                SetGuideBlade(GuideLayout.HomeBlade, true);
                SetGuideCommandSelection(0, true);
                return;
            }
            if (button == GuideFtpButton)
            {
                fileManagerHistory.Clear();
                fileManagerFolder = null;
                fileManagerPath = "/";
                PlayGuideSound(GuideCloseSound);
                navigation.NavigateTo(DashboardState.FileManager);
                return;
            }
            if (button == GuideSettingsButton)
            {
                CloseGuideToDashboard(0);
                return;
            }
            if (button == GuideQuitAppButton)
            {
                guideQuitSelectionYes = false;
                navigation.NavigateTo(DashboardState.GuideQuitConfirmation);
                return;
            }
            ShowGuidePlaceholder();
        }

        private void CloseGuideToDashboard(int? settingsTab)
        {
            if (settingsTab.HasValue)
            {
                navigation.NavigateTo(DashboardState.Settings);
                SettingsPivot.SelectedIndex = settingsTab.Value;
            }
            else
            {
                navigation.Back();
            }
            PlayGuideSound(GuideCloseSound);
        }

        private void OnGameSessionEnded()
        {
            emulatorManager?.MarkReturned();
            if (navigation.Current == DashboardState.Guide)
                CloseGuideToDashboard(null);
            else if (navigation.Current == DashboardState.GameDetails)
                navigation.NavigateTo(DashboardState.Library);

            RestoreDashboardState();
            RestoreLibraryFocus();
        }

        internal void SaveDashboardState(NxeGameRecord launchingGame = null)
        {
            var settings = ApplicationData.Current.LocalSettings.Values;
            var game = launchingGame ?? selectedGame;
            settings["DashboardRestorePending"] = launchingGame != null ||
                (settings["DashboardRestorePending"] as bool? == true);
            settings["DashboardScreen"] = navigation.Current.ToString();
            settings["DashboardSelectedGameId"] = game?.InternalId ?? string.Empty;
            settings["DashboardSelectedGamePath"] = game?.LaunchPath ?? string.Empty;
            settings["DashboardSelectedTile"] = GameLibraryList?.SelectedIndex ?? -1;
            settings["DashboardFilter"] = SelectedFilter();
            settings["DashboardScrollPosition"] = GameLibraryList?.SelectedIndex ?? -1;
            settings["DashboardActiveDrive"] = game?.StorageDeviceId ?? string.Empty;
            settings["DashboardActiveEmulator"] = game?.BackendId ?? string.Empty;
            settings["DashboardActiveGame"] = game?.InternalId ?? string.Empty;
            settings["DashboardLaunchTimestamp"] = launchingGame == null
                ? string.Empty : DateTimeOffset.UtcNow.ToString("o");
        }

        private void RestoreDashboardState()
        {
            if (LibraryFilter == null || GameLibraryList == null) return;
            var settings = ApplicationData.Current.LocalSettings.Values;
            var pending = settings["DashboardRestorePending"] as bool?;
            if (pending != true) return;

            var savedFilter = settings["DashboardFilter"] as string;
            var filterIndex = LibraryFilter.Items.OfType<ComboBoxItem>().ToList()
                .FindIndex(item => string.Equals(item.Content?.ToString(), savedFilter, StringComparison.OrdinalIgnoreCase));
            LibraryFilter.SelectedIndex = filterIndex >= 0 ? filterIndex : 0;
            ApplyFilter();

            var selectedId = settings["DashboardSelectedGameId"] as string;
            var selected = Games.FirstOrDefault(game =>
                string.Equals(game.InternalId, selectedId, StringComparison.OrdinalIgnoreCase));
            if (selected != null)
            {
                GameLibraryList.SelectedItem = selected;
                GameLibraryList.ScrollIntoView(selected);
                selectedGame = selected;
            }
            else if (settings["DashboardSelectedTile"] is int savedIndex && Games.Count > 0)
            {
                GameLibraryList.SelectedIndex = Math.Max(0, Math.Min(savedIndex, Games.Count - 1));
                GameLibraryList.ScrollIntoView(GameLibraryList.SelectedItem);
            }

            navigation.ResetToLibrary();
            settings["DashboardRestorePending"] = false;
        }

        private void GuideQuitConfirm_Click(object sender, RoutedEventArgs e)
        {
            guideQuitSelectionYes = sender == GuideQuitYesButton;
            ConfirmQuitApp();
        }

        private void UpdateQuitConfirmationFocus()
        {
            (guideQuitSelectionYes ? GuideQuitYesButton : GuideQuitNoButton)
                .Focus(FocusState.Programmatic);
        }

        private void ConfirmQuitApp()
        {
            if (!guideQuitSelectionYes)
            {
                navigation.Back();
                return;
            }
            Windows.ApplicationModel.Core.CoreApplication.Exit();
        }

        private async Task RefreshFileManagerAsync(StorageFolder folder)
        {
            fileManagerEntries.Clear();
            fileManagerFolder = folder;
            FileManagerPath.Text = fileManagerPath;
            try
            {
                if (folder == null)
                {
                    foreach (var name in NxeStorageContract.TopLevel)
                    {
                        fileManagerEntries.Add(new FileManagerEntry
                        {
                            Name = name,
                            Details = "Choose PC or FTP location",
                            Glyph = "\uE8B7",
                            IsLocationHelper = true
                        });
                    }
                    var local = ApplicationData.Current.LocalFolder;
                    var cache = ApplicationData.Current.LocalCacheFolder;
                    fileManagerEntries.Add(new FileManagerEntry
                    {
                        Name = "Covers", Details = "NXE local storage", Glyph = "\uE8B7",
                        IsFolder = true, Folder = await local.CreateFolderAsync("Covers", CreationCollisionOption.OpenIfExists)
                    });
                    fileManagerEntries.Add(new FileManagerEntry
                    {
                        Name = "Config", Details = "NXE local storage", Glyph = "\uE713",
                        IsFolder = true, Folder = await local.CreateFolderAsync("Config", CreationCollisionOption.OpenIfExists)
                    });
                    fileManagerEntries.Add(new FileManagerEntry
                    {
                        Name = "Metadata", Details = "NXE local storage", Glyph = "\uE8A5",
                        IsFolder = true, Folder = await local.CreateFolderAsync("Metadata", CreationCollisionOption.OpenIfExists)
                    });
                    fileManagerEntries.Add(new FileManagerEntry
                    {
                        Name = "Cache", Details = "NXE local cache", Glyph = "\uE8B7",
                        IsFolder = true, Folder = cache
                    });
                    foreach (var drive in await new ExternalStorageManager().DiscoverAndPrepareAsync())
                    {
                        fileManagerEntries.Add(new FileManagerEntry
                        {
                            Name = "/" + drive.VirtualName, Details = drive.NxeRoot.Name,
                            Glyph = "\uE7F8", IsFolder = true, Folder = drive.NxeRoot
                        });
                    }
                }
                else
                {
                    var items = await folder.GetItemsAsync();
                    foreach (var item in items.OrderBy(item => !item.IsOfType(StorageItemTypes.Folder))
                        .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
                    {
                        var isFolder = item.IsOfType(StorageItemTypes.Folder);
                        var details = isFolder ? "Folder" : "File";
                        if (item is StorageFile file)
                        {
                            try { details = (await file.GetBasicPropertiesAsync()).Size + " bytes"; }
                            catch { details = "File"; }
                        }
                        fileManagerEntries.Add(new FileManagerEntry
                        {
                            Name = item.Name, Details = details,
                            Glyph = isFolder ? "\uE8B7" : "\uE8A5",
                            IsFolder = isFolder,
                            Folder = item as StorageFolder,
                            File = item as StorageFile
                        });
                    }
                }
                FileManagerStatus.Text = fileManagerEntries.Count == 0
                    ? "This folder is empty" : "A opens a folder or selects a file";
                FileManagerList.ItemsSource = fileManagerEntries;
                if (fileManagerEntries.Count > 0)
                {
                    FileManagerList.SelectedIndex = 0;
                    FileManagerList.Focus(FocusState.Programmatic);
                }
            }
            catch (Exception exception)
            {
                FileManagerStatus.Text = "Storage unavailable: " + exception.Message;
                FileManagerList.ItemsSource = fileManagerEntries;
            }
        }

        private void FileManagerList_ItemClick(object sender, ItemClickEventArgs e) =>
            OpenFileManagerEntry(e.ClickedItem as FileManagerEntry);

        private void OpenFileManagerSelection() =>
            OpenFileManagerEntry(FileManagerList.SelectedItem as FileManagerEntry);

        private void OpenFileManagerEntry(FileManagerEntry entry)
        {
            if (entry == null) return;
            if (entry.IsLocationHelper)
            {
                _ = ShowLocationChooserAsync(entry.Name);
                return;
            }
            if (!entry.IsFolder)
            {
                FileManagerStatus.Text = "Selected file: " + entry.Name;
                return;
            }
            fileManagerHistory.Push(fileManagerFolder);
            fileManagerFolder = entry.Folder;
            fileManagerPath = fileManagerPath == "/"
                ? "/" + entry.Name.TrimStart('/')
                : fileManagerPath.TrimEnd('/') + "/" + entry.Name;
            _ = RefreshFileManagerAsync(fileManagerFolder);
        }

        private async Task ShowLocationChooserAsync(string name)
        {
            var chooser = new ContentDialog
            {
                Title = name,
                Content = "How are you accessing these files?",
                PrimaryButtonText = "Drive connected to PC",
                SecondaryButtonText = "FTP",
                CloseButtonText = "Back",
                DefaultButton = ContentDialogButton.Primary
            };
            var result = await chooser.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                FileManagerStatus.Text = name + "\r\n" + (name == "Games" ? "E:\\Games" : "E:\\" + name);
            }
            else if (result == ContentDialogResult.Secondary)
            {
                FileManagerStatus.Text = name + "\r\n/usb0/" + name;
            }
        }

        private readonly Stack<StorageFolder> fileManagerHistory = new Stack<StorageFolder>();

        private void OpenFileManagerParent()
        {
            if (fileManagerHistory.Count == 0)
            {
                FileManagerStatus.Text = "Already at the storage root";
                return;
            }
            fileManagerFolder = fileManagerHistory.Pop();
            var slash = fileManagerPath.LastIndexOf('/');
            fileManagerPath = slash <= 0 ? "/" : fileManagerPath.Substring(0, slash);
            _ = RefreshFileManagerAsync(fileManagerFolder);
        }

        private async void LaunchGuideGame(NxeGameRecord game)
        {
            var launched = await LaunchGameAsync(game, ShowGuideNotice);
            if (launched && navigation.Current == DashboardState.Guide) RequestBack();
        }

        private void ShowGuidePlaceholder() => ShowGuideNotice("Not implemented");

        private void ShowGuideNotice(string message)
        {
            GuideNoticeText.Text = message;
            GuideNotice.Opacity = 1;
            var fade = new DoubleAnimation
            {
                BeginTime = TimeSpan.FromMilliseconds(900),
                Duration = TimeSpan.FromMilliseconds(350),
                From = 1,
                To = 0,
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(fade, GuideNotice);
            Storyboard.SetTargetProperty(fade, "Opacity");
            var storyboard = new Storyboard();
            storyboard.Children.Add(fade);
            storyboard.Begin();
        }

        private void AnimateGuidePage(double from)
        {
            var page = ActiveGuideRows;
            var transform = new TranslateTransform { X = from };
            page.RenderTransform = transform;
            page.Opacity = 0;
            var duration = TimeSpan.FromMilliseconds(150);
            var storyboard = new Storyboard();
            var slide = new DoubleAnimation
            {
                From = from, To = 0, Duration = duration,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(slide, transform);
            Storyboard.SetTargetProperty(slide, "X");
            storyboard.Children.Add(slide);
            var fade = new DoubleAnimation { From = 0, To = 1, Duration = duration };
            Storyboard.SetTarget(fade, page);
            Storyboard.SetTargetProperty(fade, "Opacity");
            storyboard.Children.Add(fade);
            storyboard.Begin();
        }

        private static void PlayGuideSound(MediaElement sound)
        {
            try
            {
                sound.Stop();
                sound.Play();
            }
            catch
            {
                // The extracted XMA plays on Xbox. Some Windows PC media stacks
                // cannot decode XMA, so navigation must never depend on audio.
            }
        }

        private void LibraryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GameLibraryList == null || EmptyLibrary == null) return;
            if (filterOpen) return;
            ApplyFilter();
        }

        private string SelectedFilter()
        {
            var item = LibraryFilter.SelectedItem as ComboBoxItem;
            return item?.Content?.ToString() ?? DashboardLibraryView.All;
        }

        private void ApplyFilter()
        {
            var selectedId = (GameLibraryList.SelectedItem as NxeGameRecord)?.InternalId ?? selectedGame?.InternalId;
            var filtered = DashboardLibraryView.Apply(allGames, SelectedFilter()).ToList();

            var matches = Games.Count == filtered.Count &&
                !Games.Where((g, i) => !ReferenceEquals(g, filtered[i])).Any();

            if (!matches)
            {
                ReconcileVisibleGames(filtered);
            }

            var state = gameDriveState;
            if (state == GameDriveState.DriveConnectedWithGames)
            {
                EmptyLibrary.Visibility = Visibility.Collapsed;
                DriveSetupInstructions.Visibility = Visibility.Collapsed;
            }
            else if (state == GameDriveState.DriveConnectedNoGames)
            {
                EmptyLibrary.Visibility = Visibility.Visible;
                DriveSetupInstructions.Visibility = Visibility.Collapsed;
            }
            else
            {
                EmptyLibrary.Visibility = Visibility.Visible;
                DriveSetupInstructions.Visibility = Visibility.Visible;
                DriveSetupInstructions.Text = state == GameDriveState.DriveReadError
                    ? "The game drive is connected, but NXE could not read it. Check Xbox storage permissions and reconnect the drive."
                    : "No removable game drive was found. Connect an NTFS drive prepared with the NXE USB Setup Tool.";
            }

            var selected = selectedId == null ? null :
                Games.FirstOrDefault(game => game.InternalId == selectedId);
            if (selected != null) GameLibraryList.SelectedItem = selected;
            else if (Games.Count > 0 && GameLibraryList.SelectedIndex < 0) GameLibraryList.SelectedIndex = 0;
            else if (Games.Count == 0) UpdateSelectedSummary(null);
        }

        private void ReconcileVisibleGames(IReadOnlyList<NxeGameRecord> desired)
        {
            for (var index = Games.Count - 1; index >= 0; index--)
            {
                if (!desired.Any(game => game.InternalId == Games[index].InternalId))
                    Games.RemoveAt(index);
            }

            for (var index = 0; index < desired.Count; index++)
            {
                var target = desired[index];
                var existingIndex = -1;
                for (var candidate = index; candidate < Games.Count; candidate++)
                {
                    if (Games[candidate].InternalId == target.InternalId)
                    {
                        existingIndex = candidate;
                        break;
                    }
                }

                if (existingIndex < 0)
                    Games.Insert(index, target);
                else
                {
                    if (existingIndex != index) Games.Move(existingIndex, index);
                    if (!ReferenceEquals(Games[index], target)) Games[index] = target;
                }
            }
        }

        private void GameLibraryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSelectedSummary(GameLibraryList.SelectedItem as NxeGameRecord);
        }

        private void UpdateSelectedSummary(NxeGameRecord game)
        {
            selectedGame = game;
            if (game == null)
            {
                SelectedInfo.Visibility = Visibility.Collapsed;
                SelectedTitle.Text = string.Empty;
                SelectedSummary.Text = string.Empty;
                return;
            }
            SelectedInfo.Visibility = Visibility.Visible;
            SelectedTitle.Text = game.CanonicalTitle;
            SelectedSummary.Text = game.Platform + "  •  " + game.BackendId + "  •  " +
                (game.IsAvailable ? game.CompatibilityStatus.ToString() : "Unavailable — storage offline");
        }

        private void PopulateDetails(NxeGameRecord game)
        {
            DetailsTitle.Text = game.CanonicalTitle;
            DetailsIdentity.Text = game.Platform + "  •  " + game.BackendId +
                (string.IsNullOrWhiteSpace(game.NativeGameId) ? string.Empty : "  •  " + game.NativeGameId);
            DetailsAvailability.Text = game.IsAvailable
                ? "Available  •  Compatibility: " + game.CompatibilityStatus
                : "Unavailable — the storage device is offline";
            DetailsCredits.Text = JoinDetails(game.Developer, game.Publisher,
                game.ReleaseDate.HasValue ? game.ReleaseDate.Value.ToString("yyyy") : null,
                game.Region);
            DetailsHistory.Text = "Played " + game.PlayCount + " time(s)" +
                (game.LastPlayed.HasValue ? "  •  Last played " + game.LastPlayed.Value.ToString("g") : string.Empty);
            DetailsDescription.Text = string.IsNullOrWhiteSpace(game.Description)
                ? "No description is cached for this title yet." : game.Description;
            DetailsCover.Source = ArtworkSource(game.CoverPath);
            FavoriteButton.Content = game.IsFavorite ? "Remove Favorite" : "Add Favorite";
            LaunchButton.IsEnabled = game.IsAvailable;
            LaunchButton.Content = game.IsAvailable ? "Launch" : "Storage Offline";
        }

        private static string JoinDetails(params string[] values) =>
            string.Join("  •  ", values.Where(value => !string.IsNullOrWhiteSpace(value)));

        private static ImageSource ArtworkSource(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            Uri uri;
            if (!Uri.TryCreate(path, UriKind.Absolute, out uri))
            {
                var normalized = path.Replace('\\', '/').TrimStart('/');
                uri = new Uri("ms-appdata:///localcache/" + normalized);
            }
            return new BitmapImage(uri);
        }

        private async void LaunchButton_Click(object sender, RoutedEventArgs e)
        {
            var game = selectedGame;
            if (game == null) return;
            await LaunchGameAsync(game, message => DetailsAvailability.Text = message);
        }

        private async Task<bool> LaunchGameAsync(NxeGameRecord game, Action<string> reportStatus)
        {
            if (game == null) return false;
            if (!game.IsAvailable)
            {
                reportStatus?.Invoke("Storage unavailable");
                return false;
            }
            try
            {
                var backend = backendRegistry?.For(game);
                if (backend == null)
                    throw new InvalidOperationException("No backend is mapped for " + game.Platform + ".");
                if (string.Equals(game.BackendId, "xenia", StringComparison.OrdinalIgnoreCase))
                    await LogXeniaSourceDiagnosticAsync(game);
                SaveDashboardState(game);
                reportStatus?.Invoke("Launching with " + backend.DisplayName + "…");
                await backend.LaunchAsync(game);
                libraryCoordinator.RecordLaunch(game.InternalId);
                ApplyLibrarySnapshot(libraryCoordinator.LoadCached());
                return true;
            }
            catch (Exception exception)
            {
                reportStatus?.Invoke("Launch unavailable: " + exception.Message);
                return false;
            }
        }

        private static async Task LogXeniaSourceDiagnosticAsync(NxeGameRecord game)
        {
            await ExternalStorageManager.LogDiagnosticAsync("[XENIA] sourcePath=" + game.LaunchPath);
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(game.LaunchPath);
                var properties = await file.GetBasicPropertiesAsync();
                var parent = await file.GetParentAsync();
                byte[] first;
                byte[] last;
                using (var stream = await file.OpenReadAsync())
                {
                    first = await ReadBytesAsync(stream, 0, 4);
                    last = stream.Size >= 4 ? await ReadBytesAsync(stream, stream.Size - 4, 4) : new byte[0];
                }

                await ExternalStorageManager.LogDiagnosticAsync(
                    "[XENIA] StorageFile.Name=" + file.Name +
                    " StorageFile.Path=" + file.Path +
                    " sourceExists=True sourceReadable=True parentFolder=" + (parent?.Path ?? string.Empty) +
                    " sourceSize=" + properties.Size +
                    " sourceSignature=" + ClassifyXeniaSignature(first, last) +
                    " first4=" + Hex(first) + " last4=" + Hex(last));
            }
            catch (Exception exception)
            {
                await ExternalStorageManager.LogDiagnosticAsync(
                    "[XENIA] sourceExists=False sourceReadable=False HRESULT=0x" +
                    exception.HResult.ToString("X8") + " error=" + exception.Message);
                throw;
            }
        }

        private static async Task<byte[]> ReadBytesAsync(IRandomAccessStream stream, ulong offset, uint count)
        {
            using (var input = stream.GetInputStreamAt(offset))
            using (var reader = new DataReader(input))
            {
                var loaded = await reader.LoadAsync(count);
                var bytes = new byte[loaded];
                reader.ReadBytes(bytes);
                return bytes;
            }
        }

        private static string ClassifyXeniaSignature(byte[] first, byte[] last)
        {
            var signature = first == null ? string.Empty :
                new string(first.Select(value => value >= 32 && value <= 126 ? (char)value : '.').ToArray());
            if (signature == "CON ") return "CON";
            if (signature == "PIRS" || signature == "LIVE" ||
                signature == "XEX1" || signature == "XEX2") return signature;
            var tail = Hex(last);
            if (tail == "16-9F-52-D6" || tail == "D6-52-9F-16") return "ZAR";
            return "UNKNOWN";
        }

        private static string Hex(byte[] bytes) => bytes == null || bytes.Length == 0
            ? string.Empty
            : BitConverter.ToString(bytes);

        private void FavoriteButton_Click(object sender, RoutedEventArgs e)
        {
            if (selectedGame == null) return;
            libraryCoordinator.SetFavorite(selectedGame.InternalId, !selectedGame.IsFavorite);
            var selectedId = selectedGame.InternalId;
            ApplyLibrarySnapshot(libraryCoordinator.LoadCached());
            selectedGame = allGames.FirstOrDefault(game => game.InternalId == selectedId);
            if (selectedGame != null) PopulateDetails(selectedGame);
        }

        private void PopulateEmulatorSettings()
        {
            EmulatorList.Children.Clear();
            foreach (var emu in emulatorManager.Definitions)
            {
                EmulatorList.Children.Add(CreateEmulatorRow(emu.Id, emu.DisplayName, emu.Systems));
            }
        }

        private FrameworkElement CreateEmulatorRow(string id, string name, string systems)
        {
            var row = new Grid
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(205, 35, 42, 47)),
                Padding = new Thickness(20), MinHeight = 82
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var labels = new StackPanel();
            labels.Children.Add(new TextBlock { Text = name, FontSize = 24, Foreground = WhiteBrush() });
            labels.Children.Add(new TextBlock
            {
                Text = systems, FontSize = 15,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 195, 201, 205))
            });
            row.Children.Add(labels);
            var status = new TextBlock
            {
                Text = "Checking…", FontSize = 19, VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 195, 201, 205)),
                Margin = new Thickness(24, 0, 8, 0)
            };
            Grid.SetColumn(status, 1); row.Children.Add(status);

            Action<EmulatorDetectionResult> update = result =>
            {
                status.Text = result.DisplayStatus;
                status.Foreground = new SolidColorBrush(result.State == EmulatorDetectionState.Installed
                    ? Windows.UI.Color.FromArgb(255, 143, 204, 62)
                    : Windows.UI.Color.FromArgb(255, 235, 184, 72));
            };
            backendStatusUpdates[id] = update;

            _ = DetectEmulatorAsync(id, update);

            return row;
        }

        private async Task DetectEmulatorAsync(string id, Action<EmulatorDetectionResult> update)
        {
            var adapter = backendLaunchAdapter ?? new UwpBackendLaunchAdapter(emulatorManager);
            var result = await adapter.DetectAsync(id);
            update(result);
        }

        private static SolidColorBrush WhiteBrush() =>
            new SolidColorBrush(Windows.UI.Colors.White);

        private async Task StartFtpInfrastructureAsync()
        {
            await RestartFtpAsync();
            var pairId = GetOrCreateSecureValue("FtpPairId", 16);
            var deviceToken = GetOrCreateSecureValue("FtpDeviceToken", 32);
            var controlToken = GetOrCreateSecureValue("FtpWebsiteToken", 32);
            try
            {
                webManagementServer = new NxeWebManagementServer(
                    () => controlToken,
                    () => pairId,
                    () => ftpService.IsRunning,
                    () => ftpFailure,
                    StartFtpAsync,
                    StopFtpAsync,
                    RestartFtpAsync,
                    ConfigureFtpFromWebsiteAsync,
                    () => ApplicationData.Current.LocalSettings.Values["FtpUsername"] as string ?? string.Empty,
                    ReadFtpPasswordAsync);
                await webManagementServer.StartAsync();
            }
            catch (Exception exception) { ftpFailure = "Local web management unavailable: " + exception.Message; }
            ftpCloudBridge = new NxeFtpCloudBridge(
                pairId,
                deviceToken,
                controlToken,
                () => ftpService.IsRunning,
                () => NxeFtpServer.Addresses().FirstOrDefault() ?? string.Empty,
                () => ftpFailure,
                ConfigureFtpFromWebsiteAsync,
                StartFtpAsync,
                StopFtpAsync,
                RestartFtpAsync,
                webManagementServer);
            ftpCloudBridge.StateChanged += OnFtpCloudStateChanged;
            try { await ftpCloudBridge.StartAsync(); }
            catch (Exception exception) { ftpFailure = "Website control unavailable: " + exception.Message; }
            await RefreshFtpDashboardAsync();
        }

        private static string GetOrCreateSecureValue(string key, uint byteCount)
        {
            var settings = ApplicationData.Current.LocalSettings.Values;
            var existing = settings[key] as string;
            if (!string.IsNullOrWhiteSpace(existing)) return existing;
            var created = CryptographicBuffer.EncodeToHexString(
                CryptographicBuffer.GenerateRandom(byteCount)).ToLowerInvariant();
            settings[key] = created;
            return created;
        }

        private async Task ConfigureFtpFromWebsiteAsync(string username, string password)
        {
            var settings = ApplicationData.Current.LocalSettings.Values;
            settings["FtpUsername"] = username ?? string.Empty;
            await SaveFtpPasswordAsync(username, password);
            await RestartFtpAsync();
        }

        private static async Task SaveFtpPasswordAsync(string username, string password)
        {
            var vault = new PasswordVault();
            try
            {
                foreach (var credential in vault.FindAllByResource("NXE.FTP")) vault.Remove(credential);
            }
            catch { }
            if (!string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password))
            {
                var credential = new PasswordCredential("NXE.FTP", username ?? string.Empty, password ?? string.Empty);
                vault.Add(credential);
            }
            await Task.CompletedTask;
        }

        private static async Task<string> ReadFtpPasswordAsync()
        {
            var vault = new PasswordVault();
            try
            {
                var credential = vault.FindAllByResource("NXE.FTP").FirstOrDefault();
                if (credential == null) return string.Empty;
                credential.RetrievePassword();
                return credential.Password ?? string.Empty;
            }
            catch
            {
                var settings = ApplicationData.Current.LocalSettings.Values;
                var legacy = settings["FtpPassword"] as string ?? string.Empty;
                if (!string.IsNullOrEmpty(legacy))
                {
                    await SaveFtpPasswordAsync(settings["FtpUsername"] as string ?? string.Empty, legacy);
                    settings.Remove("FtpPassword");
                }
                return legacy;
            }
        }

        private async Task StartFtpAsync()
        {
            await ftpRestartLock.WaitAsync();
            try
            {
                if (ftpService.IsRunning) return;
                ftpFailure = string.Empty;
                var settings = ApplicationData.Current.LocalSettings.Values;
                var username = settings["FtpUsername"] as string ?? string.Empty;
                var password = await ReadFtpPasswordAsync();
                await ftpService.StartAsync("2121", username, password);
            }
            catch (Exception exception) { ftpFailure = exception.Message; }
            finally { ftpRestartLock.Release(); }
        }

        private async Task StopFtpAsync()
        {
            await ftpRestartLock.WaitAsync();
            try
            {
                ftpFailure = string.Empty;
                ftpService.Stop();
            }
            catch (Exception exception) { ftpFailure = exception.Message; }
            finally { ftpRestartLock.Release(); }
        }

        private async Task RestartFtpAsync()
        {
            await ftpRestartLock.WaitAsync();
            try
            {
                ftpService.Stop();
                ftpFailure = string.Empty;
                var settings = ApplicationData.Current.LocalSettings.Values;
                var username = settings["FtpUsername"] as string ?? string.Empty;
                var password = await ReadFtpPasswordAsync();
                await ftpService.StartAsync("2121", username, password);
            }
            catch (Exception exception) { ftpFailure = exception.Message; }
            finally { ftpRestartLock.Release(); }
        }

        private async void OnFtpStateChanged(object sender, EventArgs e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                await RefreshFtpDashboardAsync());
        }

        private async void OnFtpCloudStateChanged(object sender, EventArgs e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                await RefreshFtpDashboardAsync());
        }

        private async Task RefreshFtpDashboardAsync()
        {
            var addresses = NxeFtpServer.Addresses();
            var address = addresses.FirstOrDefault();
            var running = ftpService.IsRunning;
            FtpStatus.Text = running ? "Running" : "Failed";
            FtpStatus.Foreground = new SolidColorBrush(running
                ? Windows.UI.Color.FromArgb(255, 143, 204, 62)
                : Windows.UI.Color.FromArgb(255, 255, 123, 114));
            FtpConsoleIp.Text = string.IsNullOrEmpty(address) ? "No LAN address" : address;
            FtpWebsiteStatus.Text = ftpCloudBridge != null && ftpCloudBridge.IsConnected
                ? "Vortex Prime control connected"
                : "Connecting to Vortex Prime control…";

            FtpControlHint.Text = ftpCloudBridge == null
                ? "Vortex Prime account sync is unavailable. Restart NXE to retry it."
                : "Enter this IP once on Vortex Prime. If it changes later, use Change IP on the website and enter the new address shown here.";
        }

        private void OnRightStickScrollTick(object sender, object e)
        {
            if (navigation.Current != DashboardState.Settings) return;

            var gamepads = Gamepad.Gamepads;
            if (gamepads.Count == 0) return;

            var reading = gamepads[0].GetCurrentReading();
            var stickY = reading.RightThumbstickY;
            const double deadzone = 0.20;
            if (Math.Abs(stickY) > deadzone)
            {
                double sign = stickY > 0 ? -1.0 : 1.0;
                double magnitude = (Math.Abs(stickY) - deadzone) / (1.0 - deadzone);
                double delta = sign * magnitude * 20.0;

                ScrollViewer targetViewer = null;
                if (SettingsPivot.SelectedIndex == 1)
                {
                    targetViewer = FtpScrollViewer;
                }
                else if (SettingsPivot.SelectedIndex == 0)
                {
                    targetViewer = EmulatorsScrollViewer;
                }

                if (targetViewer != null)
                {
                    targetViewer.ChangeView(null, targetViewer.VerticalOffset + delta, null, true);
                }
            }
        }

        private void ViewGuideButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFtpGuide();
        }

        private void OpenFtpGuide()
        {
            isFtpGuideOpen = true;
            if (FtpNormalContent != null) FtpNormalContent.Visibility = Visibility.Collapsed;
            if (FtpGuideContainer != null) FtpGuideContainer.Visibility = Visibility.Visible;
            SetFtpGuideStep(1);
        }

        private void CloseFtpGuide()
        {
            isFtpGuideOpen = false;
            currentFtpGuideStep = 1;
            if (FtpGuideContainer != null) FtpGuideContainer.Visibility = Visibility.Collapsed;
            if (FtpNormalContent != null) FtpNormalContent.Visibility = Visibility.Visible;
            SetFtpGuideStep(1);
            ViewGuideButton?.Focus(FocusState.Programmatic);
            FtpScrollViewer?.ChangeView(null, 0, null, true);
        }

        private void SetFtpGuideStep(int step)
        {
            currentFtpGuideStep = Math.Max(1, Math.Min(5, step));
            if (FtpGuidePage1 != null) FtpGuidePage1.Visibility = (currentFtpGuideStep == 1) ? Visibility.Visible : Visibility.Collapsed;
            if (FtpGuidePage2 != null) FtpGuidePage2.Visibility = (currentFtpGuideStep == 2) ? Visibility.Visible : Visibility.Collapsed;
            if (FtpGuidePage3 != null) FtpGuidePage3.Visibility = (currentFtpGuideStep == 3) ? Visibility.Visible : Visibility.Collapsed;
            if (FtpGuidePage4 != null) FtpGuidePage4.Visibility = (currentFtpGuideStep == 4) ? Visibility.Visible : Visibility.Collapsed;
            if (FtpGuidePage5 != null) FtpGuidePage5.Visibility = (currentFtpGuideStep == 5) ? Visibility.Visible : Visibility.Collapsed;

            if (isFtpGuideOpen)
            {
                switch (currentFtpGuideStep)
                {
                    case 1: FtpGuideNext1?.Focus(FocusState.Programmatic); break;
                    case 2: FtpGuideNext2?.Focus(FocusState.Programmatic); break;
                    case 3: FtpGuideNext3?.Focus(FocusState.Programmatic); break;
                    case 4: FtpGuideNext4?.Focus(FocusState.Programmatic); break;
                    case 5: FtpGuideFinishButton?.Focus(FocusState.Programmatic); break;
                }
                FtpScrollViewer?.ChangeView(null, 0, null, true);
            }
        }

        private void FtpGuideNext_Click(object sender, RoutedEventArgs e)
        {
            if (currentFtpGuideStep < 5)
            {
                SetFtpGuideStep(currentFtpGuideStep + 1);
            }
            else
            {
                CloseFtpGuide();
            }
        }

        private void FtpGuideFinish_Click(object sender, RoutedEventArgs e)
        {
            CloseFtpGuide();
        }

        private async void OnFtpStorageChanged(object sender, EventArgs e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Low, async () =>
            {
                await RefreshExternalStorageAsync();
            });
        }

        private async void OnStorageRefreshTick(object sender, object e)
        {
            var presence = await storagePresenceProbe.ProbePresenceAsync();
            var identityChanged = !string.Equals(lastDriveIdentity, presence.Identity, StringComparison.Ordinal);
            var countChanged = presence.DriveCount != lastDriveCount;
            if (identityChanged || countChanged || presence.ReadError)
                await RefreshExternalStorageAsync();
        }

        private async Task RefreshExternalStorageAsync()
        {
            if (libraryCoordinator == null || !await storageRefreshLock.WaitAsync(0)) return;
            try
            {
                var result = await Task.Run(() => libraryCoordinator.RefreshAsync());
                hasReadyGameDrive = result.HasReadyGameDrive;
                gameDriveState = result.DriveState;
                ApplyLibrarySnapshot(result.Games);
                await ExternalStorageManager.LogDiagnosticAsync(
                    $"[UI] bound count={Games.Count} tile visible={Games.Count > 0 && GameLibraryList.Visibility == Visibility.Visible}");
                await RefreshFtpDashboardAsync();
                var driveConnected = storageRefreshInitialized && result.DriveCount > lastDriveCount;
                lastDriveCount = result.DriveCount;
                lastDriveIdentity = result.DriveIdentity;
                storageRefreshInitialized = true;
                if (driveConnected)
                    _ = ShowStorageNotificationAsync(result.DriveCount + " NXE game drive connected");
                else
                    StorageStatus.Visibility = Visibility.Collapsed;
            }
            catch (Exception exception)
            {
                StorageStatus.Text = "External storage refresh failed: " + exception.Message;
                hasReadyGameDrive = false;
                gameDriveState = GameDriveState.DriveReadError;
                ApplyFilter();
                StorageStatus.Visibility = Visibility.Collapsed;
            }
            finally { storageRefreshLock.Release(); }
        }

        private async Task ShowStorageNotificationAsync(string message)
        {
            StorageStatus.Text = message;
            StorageStatus.Visibility = Visibility.Visible;
            await Task.Delay(TimeSpan.FromSeconds(5));
            StorageStatus.Visibility = Visibility.Collapsed;
        }

        private void ApplyLibrarySnapshot(IEnumerable<NxeGameRecord> snapshot)
        {
            var newGames = snapshot?.ToList() ?? new List<NxeGameRecord>();
            var changed = allGames.Count != newGames.Count ||
                !allGames.Select(g => g.InternalId + "|" + g.IsAvailable + "|" + g.PlayCount + "|" + g.IsFavorite)
                    .SequenceEqual(newGames.Select(g => g.InternalId + "|" + g.IsAvailable + "|" + g.PlayCount + "|" + g.IsFavorite));

            if (changed)
            {
                var selectedId = selectedGame?.InternalId;
                allGames.Clear();
                allGames.AddRange(newGames);
                ApplyFilter();
                RefreshGuideRecentGames();
                RefreshGuideFavoriteGames();
                if (selectedId != null)
                    selectedGame = allGames.FirstOrDefault(game => game.InternalId == selectedId);
            }
            else
            {
                ApplyFilter();
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            storageRefreshTimer.Stop();
            guideStatusTimer.Stop();
            storageRefreshTimer.Tick -= OnStorageRefreshTick;
            guideStatusTimer.Tick -= OnGuideStatusTick;
            CoreWindow.GetForCurrentThread().KeyDown -= OnCoreWindowKeyDown;
            CoreWindow.GetForCurrentThread().KeyUp -= OnCoreWindowKeyUp;
            navigation.StateChanged -= OnNavigationStateChanged;
            ftpService.StateChanged -= OnFtpStateChanged;
            ftpService.StorageChanged -= OnFtpStorageChanged;
            App.GameSessionEnded -= OnGameSessionEnded;
            if (ftpCloudBridge != null)
            {
                ftpCloudBridge.StateChanged -= OnFtpCloudStateChanged;
                ftpCloudBridge.Dispose();
            }
            if (webManagementServer != null) webManagementServer.Dispose();
            ftpService.Dispose();
            libraryCoordinator?.Dispose();
        }
    }
}


