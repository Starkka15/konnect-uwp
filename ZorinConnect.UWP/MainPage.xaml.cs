using System;
using System.Linq;
using System.Text;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using ZorinConnect.Core;
using ZorinConnect.Helpers;

namespace ZorinConnect
{
    public sealed partial class MainPage : Page
    {
        private readonly StringBuilder _log = new StringBuilder();

        public MainPage()
        {
            this.InitializeComponent();
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            StartupTrace.Mark("page-loaded");
            var prev = StartupTrace.PreviousRun();
            if (prev.Length > 0 && !prev.EndsWith("core-started;"))
                OnLog($"PREV RUN TRACE: {prev}");

            var core = KdeConnectCore.Instance;
            core.Log += OnLog;
            core.LinksChanged += OnLinksChanged;

            try
            {
                StartupTrace.Mark("core-start");
                await core.StartAsync();
                StartupTrace.Mark("core-started");
                OwnNameText.Text = DeviceHelper.DeviceName;
                OwnIdText.Text = $"{DeviceHelper.DeviceId} · proto v{DeviceHelper.ProtocolVersion} · tcp {core.Lan.TcpPort}\n{SslHelper.CertificateHash(SslHelper.Certificate)}";
            }
            catch (Exception ex)
            {
                OwnNameText.Text = "startup failed";
                OnLog(ex.ToString());
            }
        }

        private void OnLinksChanged()
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                DeviceList.Items.Clear();
                foreach (var kv in KdeConnectCore.Instance.Links.Values.OrderBy(t => t.Item1.Name))
                {
                    DeviceList.Items.Add($"{kv.Item1.Name} · {kv.Item1.Type.ToProtocolString()} · v{kv.Item1.ProtocolVersion} · {kv.Item2.RemoteAddress}");
                }
            });
        }

        private void OnLog(string msg)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                _log.AppendLine($"{DateTime.Now:HH:mm:ss} {msg}");
                if (_log.Length > 20000) _log.Remove(0, _log.Length - 20000);
                LogText.Text = _log.ToString();
                LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, true);
            });
        }

        private async void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            await KdeConnectCore.Instance.RefreshAsync();
        }
    }
}
