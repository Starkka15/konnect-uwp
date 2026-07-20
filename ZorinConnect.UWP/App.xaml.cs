using System;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace ZorinConnect
{
    sealed partial class App : Application
    {
        private static Exception _startupException;

        public App()
        {
            this.UnhandledException += OnUnhandledException;
            try
            {
                this.InitializeComponent();
                this.Suspending += OnSuspending;
            }
            catch (Exception ex)
            {
                _startupException = ex;
            }
        }

        private async void OnUnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            var dialog = new Windows.UI.Popups.MessageDialog(e.Exception?.ToString() ?? e.Message, "Unhandled Error");
            await dialog.ShowAsync();
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs e)
        {
            var rootFrame = Window.Current.Content as Frame;
            if (rootFrame == null)
            {
                rootFrame = new Frame();
                Window.Current.Content = rootFrame;
            }

            if (_startupException != null)
            {
                var dialog = new Windows.UI.Popups.MessageDialog(_startupException.ToString(), "Startup Error");
                await dialog.ShowAsync();
            }

            if (rootFrame.Content == null)
            {
                rootFrame.Navigate(typeof(MainPage), e.Arguments);
            }
            Window.Current.Activate();
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            deferral.Complete();
        }
    }
}
