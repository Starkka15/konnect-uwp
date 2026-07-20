using System;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using ZorinConnect.Core;

namespace ZorinConnect
{
    sealed partial class App : Application
    {
        public App()
        {
            StartupTrace.Mark("app-ctor");
            this.UnhandledException += OnUnhandledException;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, ev) =>
            {
                StartupTrace.MarkError("unobserved-task", ev.Exception);
                ev.SetObserved();
            };
            try
            {
                this.InitializeComponent();
                this.Suspending += OnSuspending;
                this.Resuming += (s, ev) => StartupTrace.Mark("resuming");
                StartupTrace.Mark("app-ctor-done");
            }
            catch (Exception ex)
            {
                StartupTrace.MarkError("app-ctor", ex);
            }
        }

        private void OnUnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            StartupTrace.MarkError("unhandled", e.Exception ?? new Exception(e.Message));
            e.Handled = true;
            ShowFatal(e.Exception?.ToString() ?? e.Message);
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            StartupTrace.Mark("launch");
            try
            {
                var rootFrame = Window.Current.Content as Frame;
                if (rootFrame == null)
                {
                    rootFrame = new Frame();
                    Window.Current.Content = rootFrame;
                }
                if (rootFrame.Content == null)
                {
                    rootFrame.Navigate(typeof(MainPage), e.Arguments);
                }
                Window.Current.Activate();
                ExecutionGuard.Request();
                StartupTrace.Mark("launch-done");
            }
            catch (Exception ex)
            {
                StartupTrace.MarkError("launch", ex);
                ShowFatal(ex.ToString());
            }
        }

        private static void ShowFatal(string text)
        {
            try
            {
                Window.Current.Content = new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Text = "FATAL:\n" + text + "\n\nprev run: " + StartupTrace.PreviousRun(),
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 11,
                        Margin = new Thickness(8),
                    }
                };
                Window.Current.Activate();
            }
            catch { }
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            StartupTrace.Mark($"suspending:deadline={e.SuspendingOperation.Deadline:HH:mm:ss}");
            var deferral = e.SuspendingOperation.GetDeferral();
            deferral.Complete();
        }
    }
}
