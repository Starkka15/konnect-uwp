using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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
            core.LinksChanged += RenderDevices;
            core.PairingChanged += _ => RenderDevices();
            core.RingStarted += ShowRingOverlay;
            core.RingStopped += HideRingOverlay;

            try
            {
                await Helpers.DeviceInfoProbe.ProbeAsync(); // diagnostic: log all device-info sources
                var _probe = RescapProbe.RunAsync();        // T31: fire-and-forget so a hang can't wedge startup
                StartupTrace.Mark("core-start");
                await core.StartAsync();
                StartupTrace.Mark("core-started");
                NameBox.Text = DeviceHelper.DeviceName;
                OwnIdText.Text = $"{DeviceHelper.DeviceId} · proto v{DeviceHelper.ProtocolVersion} · tcp {core.Lan.TcpPort}";
                SensSlider.Value = Plugins.RemoteInputPlugin.Sensitivity;
                SensValue.Text = $"{Plugins.RemoteInputPlugin.Sensitivity:0.0}x";
                await UpdateStorageStatusAsync();
            }
            catch (Exception ex)
            {
                NameBox.Text = "startup failed";
                OnLog(ex.ToString());
            }
        }

        private async void OnRenameClick(object sender, RoutedEventArgs e)
        {
            var name = NameBox.Text?.Trim();
            if (string.IsNullOrEmpty(name)) return;
            DeviceHelper.DeviceName = name;
            NameBox.Text = DeviceHelper.DeviceName; // reflect filtering
            OnLog($"renamed to {DeviceHelper.DeviceName}; re-broadcasting");
            await KdeConnectCore.Instance.RefreshAsync(); // re-send identity so the desktop updates
        }

        private void RenderDevices()
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                var core = KdeConnectCore.Instance;
                DevicePanel.Children.Clear();
                foreach (var kv in core.Links.Values.OrderBy(t => t.Item1.Name))
                {
                    var info = kv.Item1;
                    core.Pairing.TryGetValue(info.Id, out var handler);
                    var state = handler?.State ?? PairState.NotPaired;

                    var box = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
                    box.Children.Add(new TextBlock
                    {
                        Text = $"{info.Name}  ·  {info.Type.ToProtocolString()}  ·  v{info.ProtocolVersion}",
                        FontSize = 16,
                    });
                    box.Children.Add(new TextBlock
                    {
                        Text = $"{kv.Item2.RemoteAddress}  ·  {state}",
                        FontSize = 11, Opacity = 0.6,
                    });

                    var vkey = handler?.VerificationKey();
                    if (vkey != null)
                        box.Children.Add(new TextBlock { Text = $"Verify: {vkey}", FontSize = 14, FontFamily = new Windows.UI.Xaml.Media.FontFamily("Consolas") });

                    var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
                    string id = info.Id;
                    switch (state)
                    {
                        case PairState.NotPaired:
                            buttons.Children.Add(MakeButton("Pair", () => core.RequestPair(id)));
                            break;
                        case PairState.Requested:
                            buttons.Children.Add(new TextBlock { Text = "waiting for peer…", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
                            break;
                        case PairState.RequestedByPeer:
                            buttons.Children.Add(MakeButton("Accept", () => core.AcceptPair(id)));
                            buttons.Children.Add(MakeButton("Reject", () => core.RejectPair(id)));
                            break;
                        case PairState.Paired:
                            buttons.Children.Add(MakeButton("Ping", () => core.GetPlugin<Plugins.PingPlugin>(id)?.SendPing()));
                            buttons.Children.Add(MakeButton("Ring PC", () => core.GetPlugin<Plugins.FindMyPhonePlugin>(id)?.RingRemote()));
                            buttons.Children.Add(MakeButton("Unpair", () => core.Unpair(id)));
                            break;
                    }
                    box.Children.Add(buttons);
                    DevicePanel.Children.Add(box);
                }
                if (DevicePanel.Children.Count == 0)
                    DevicePanel.Children.Add(new TextBlock { Text = "No devices connected", Opacity = 0.5 });
            });
        }

        private Button MakeButton(string text, Action onClick)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0) };
            b.Click += (s, e) => onClick();
            return b;
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

        private void ShowRingOverlay()
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.High, () => RingOverlay.Visibility = Visibility.Visible);
        }

        private void HideRingOverlay()
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.High, () => RingOverlay.Visibility = Visibility.Collapsed);
        }

        private void OnDismissRing(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            Plugins.FindMyPhonePlugin.Current?.StopRing();
        }

        private void OnSensChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            Plugins.RemoteInputPlugin.Sensitivity = e.NewValue;
            if (SensValue != null) SensValue.Text = $"{e.NewValue:0.0}x";
        }

        // ---- storage locations (FolderPicker -> FutureAccessList) ----

        private static Windows.Storage.Pickers.FolderPicker MakeFolderPicker()
        {
            var picker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
            };
            picker.FileTypeFilter.Add("*"); // required or PickSingleFolderAsync throws
            return picker;
        }

        private async void OnPickShareFolder(object sender, RoutedEventArgs e)
        {
            try
            {
                var folder = await MakeFolderPicker().PickSingleFolderAsync();
                if (folder == null) return;
                StorageRoots.SetShareFolder(folder);
                OnLog($"received files will save to: {folder.Path}");
                await UpdateStorageStatusAsync();
            }
            catch (Exception ex) { OnLog($"pick save folder failed: {ex.Message}"); }
        }

        private async void OnGrantMain(object sender, RoutedEventArgs e) => await GrantMountAsync("Main");
        private async void OnGrantSdCard(object sender, RoutedEventArgs e) => await GrantMountAsync("SD Card");

        private async Task GrantMountAsync(string name)
        {
            try
            {
                var folder = await MakeFolderPicker().PickSingleFolderAsync();
                if (folder == null) return;
                StorageRoots.SetMountRoot(folder, name);   // fixed name = the button; not user-typed
                OnLog($"'{name}' mount granted: {folder.Path}");
                await UpdateStorageStatusAsync();
            }
            catch (Exception ex) { OnLog($"grant '{name}' failed: {ex.Message}"); }
        }

        private async void OnClearMountFolders(object sender, RoutedEventArgs e)
        {
            StorageRoots.ClearMountRoots();
            OnLog("mount roots cleared (defaults to Pictures/Music/Videos)");
            await UpdateStorageStatusAsync();
        }

        private async System.Threading.Tasks.Task UpdateStorageStatusAsync()
        {
            try
            {
                var share = await StorageRoots.GetShareFolderAsync();
                var mounts = await StorageRoots.GetMountRootsAsync();
                var shareText = share != null ? share.Path : "Pictures\\Konnect (default)";
                string mountText;
                if (mounts.Count == 0)
                    mountText = "Pictures, Music, Videos (default)";
                else
                    mountText = string.Join(", ", mounts.Select(m => $"{m.Name} → {m.Folder.Name}"));
                StorageStatus.Text = $"Save to: {shareText}\nMount exposes: {mountText}";
            }
            catch (Exception ex) { StorageStatus.Text = $"storage status error: {ex.Message}"; }
        }
    }
}
