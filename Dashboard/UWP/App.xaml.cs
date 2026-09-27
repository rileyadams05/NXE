using System;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace NxeDashboard
{
    sealed partial class App : Application
    {
        internal static string LaunchArguments { get; private set; }
        internal static event Action GameSessionEnded;

        public App()
        {
            this.RequiresPointerMode = Windows.UI.Xaml.ApplicationRequiresPointerMode.WhenRequested;
            this.FocusVisualKind = Windows.UI.Xaml.FocusVisualKind.HighVisibility;
            InitializeComponent();
            Suspending += OnSuspending;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            LaunchArguments = e.Arguments ?? string.Empty;
            var frame = EnsureFrame();

            if (!e.PrelaunchActivated)
            {
                if (frame.Content == null) frame.Navigate(typeof(MainPage));
                Window.Current.Activate();
            }
        }

        protected override void OnActivated(IActivatedEventArgs args)
        {
            var frame = EnsureFrame();
            if (frame.Content == null) frame.Navigate(typeof(MainPage));
            Window.Current.Activate();

            if (args is ProtocolActivatedEventArgs protocolArgs)
            {
                var uri = protocolArgs.Uri;
                if ((string.Equals(uri.Scheme, "nxe", StringComparison.OrdinalIgnoreCase) &&
                     (string.Equals(uri.Host, "return", StringComparison.OrdinalIgnoreCase) || uri.AbsoluteUri.IndexOf("return", StringComparison.OrdinalIgnoreCase) >= 0)) ||
                    (string.Equals(uri.Scheme, "nxe-dashboard", StringComparison.OrdinalIgnoreCase) &&
                     uri.Query.IndexOf("ended=true", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    GameSessionEnded?.Invoke();
                }
            }
        }

        private static Frame EnsureFrame()
        {
            var frame = Window.Current.Content as Frame;
            if (frame != null) return frame;

            frame = new Frame();
            frame.NavigationFailed += (_, args) =>
                throw new InvalidOperationException("Failed to load " + args.SourcePageType.FullName);
            Window.Current.Content = frame;
            return frame;
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            var frame = Window.Current.Content as Frame;
            (frame?.Content as MainPage)?.SaveDashboardState();
            deferral.Complete();
        }
    }
}
