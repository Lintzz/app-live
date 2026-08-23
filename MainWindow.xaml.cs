using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;

namespace RadminStreamApp
{
    public partial class MainWindow : Window
    {
        private SignalingServer _server;
        private SignalingClient _client;
        private StreamManager _streamManager;
        private WriteableBitmap _writeableBitmap;
        private System.Windows.Threading.DispatcherTimer _mouseIdleTimer;

        private string _downloadUrl;

        public MainWindow()
        {
            InitializeComponent();
            
            _mouseIdleTimer = new System.Windows.Threading.DispatcherTimer();
            _mouseIdleTimer.Interval = TimeSpan.FromSeconds(3);
            _mouseIdleTimer.Tick += MouseIdleTimer_Tick;
            this.MouseMove += MainWindow_MouseMove;
            this.Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var updateResult = await UpdateManager.CheckForUpdatesAsync();
            if (updateResult.HasUpdate)
            {
                _downloadUrl = updateResult.DownloadUrl;
                UpdateBannerText.Text = $"Uma nova versão ({updateResult.LatestVersion}) está disponível!";
                UpdateBanner.Visibility = Visibility.Visible;
            }
        }

        private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_downloadUrl))
            {
                BtnUpdate.Content = "Baixando...";
                BtnUpdate.IsEnabled = false;
                BtnDismissUpdate.IsEnabled = false;
                await UpdateManager.DownloadAndInstallUpdateAsync(_downloadUrl);
            }
        }

        private void BtnDismissUpdate_Click(object sender, RoutedEventArgs e)
        {
            UpdateBanner.Visibility = Visibility.Collapsed;
        }

        private void CboWindows_DropDownOpened(object sender, EventArgs e)
        {
            CboWindows.ItemsSource = WindowHelper.GetCapturableWindows();
        }

        private void SliderVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_streamManager != null)
            {
                _streamManager.SetVolume((float)e.NewValue / 100f);
            }
        }

        private void BtnHost_Click(object sender, RoutedEventArgs e)
        {
            BtnClient.Visibility = Visibility.Visible;
            BtnClient.IsEnabled = true;
            BtnHost.IsEnabled = false;

            PanelHost.Visibility = Visibility.Visible;
            PanelClient.Visibility = Visibility.Collapsed;
            
            IconVolume.Visibility = Visibility.Collapsed;
            SliderVolume.Visibility = Visibility.Collapsed;
            BtnSettings.Visibility = Visibility.Collapsed;
            BtnTheater.Visibility = Visibility.Collapsed;
            BtnFullscreen.Visibility = Visibility.Collapsed;
            
            CboWindows.ItemsSource = WindowHelper.GetCapturableWindows();

            if (_server == null)
            {
                _server = new SignalingServer();
                _server.OnMessageReceived += Server_OnMessageReceived;
                _server.OnClientConnected += (socket) => {
                    System.Windows.Application.Current.Dispatcher.Invoke(() => {
                        UpdateViewerCount();
                    });
                };
                _server.OnClientDisconnected += (socket) => {
                    System.Windows.Application.Current.Dispatcher.Invoke(() => {
                        UpdateViewerCount();
                    });
                };
                _server.Start("0.0.0.0", 8080);
                UpdateViewerCount();
            }
        }

        private void UpdateViewerCount()
        {
            if (_server != null)
            {
                int count = _server.ConnectedClientsCount;
                ViewerCountText.Text = $"{count} Viewer{(count != 1 ? "s" : "")}";
            }
        }

        private async void Server_OnMessageReceived(Fleck.IWebSocketConnection socket, string message)
        {
            if (_streamManager != null)
                await _streamManager.HandleSignalingMessage(socket.ConnectionInfo.Id.ToString(), message);
        }

        private void BtnClient_Click(object sender, RoutedEventArgs e)
        {
            BtnHost.Visibility = Visibility.Visible;
            BtnHost.IsEnabled = true;
            BtnClient.IsEnabled = false;

            PanelHost.Visibility = Visibility.Collapsed;
            PanelClient.Visibility = Visibility.Visible;
            
            IconVolume.Visibility = Visibility.Visible;
            SliderVolume.Visibility = Visibility.Visible;
            BtnSettings.Visibility = Visibility.Visible;
            BtnTheater.Visibility = Visibility.Visible;
            BtnFullscreen.Visibility = Visibility.Visible;
        }

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            string ip = TxtHostIp.Text;
            if (string.IsNullOrWhiteSpace(ip)) return;

            if (_client == null)
            {
                _client = new SignalingClient();
                _streamManager = new StreamManager();
                
                _client.OnMessageReceived += async (message) => {
                    if (message == "STREAM_STOPPED")
                    {
                        System.Windows.Application.Current.Dispatcher.Invoke(() => {
                            VideoPlayer.Source = null;
                            StatusText.Text = "Transmissão Encerrada";
                            StatusText.Visibility = Visibility.Visible;
                            _streamManager?.Stop();
                        });
                        return;
                    }
                    if (message == "STREAM_STARTED")
                    {
                        if (_streamManager != null)
                        {
                            await _streamManager.InitializeClient();
                            var msg = new SignalingMessage { Type = "CLIENT_CONNECTED", Data = "", SenderId = "client" };
                            _client.SendMessage(System.Text.Json.JsonSerializer.Serialize(msg));
                        }
                        return;
                    }
                    if (_streamManager != null)
                        await _streamManager.HandleSignalingMessage("host", message);
                };
                
                _client.OnBinaryReceived += (data) => {
                    _streamManager.ProcessReceivedBinary(data);
                };

                // Sync initial volume
                _streamManager.SetVolume((float)SliderVolume.Value / 100f);

                _streamManager.OnVideoFrameDecoded += (pixelData, width, height, stride) => {
                    StreamManager_OnVideoFrameDecoded(pixelData, width, height, stride);
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                        StatusText.Visibility = Visibility.Collapsed;
                    });
                };

                _streamManager.OnConnectionStateChanged += (state) => {
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                        StatusText.Text = $"WebRTC: {state}";
                        StatusText.Visibility = Visibility.Visible;
                    });
                };

                _streamManager.OnLocalSdpReady += (clientId, sdpJson) => {
                    _client?.SendMessage(sdpJson);
                };
                
                try
                {
                    await _streamManager.InitializeClient();
                    await _client.StartAsync(ip, 8080);
                    
                    var helloMsg = new SignalingMessage { Type = "CLIENT_CONNECTED", Data = "", SenderId = "client" };
                    _client.SendMessage(System.Text.Json.JsonSerializer.Serialize(helloMsg));

                    BtnConnect.Content = "Connected";
                    BtnConnect.IsEnabled = false;
                    BtnDisconnect.IsEnabled = true;
                    TxtHostIp.IsEnabled = false;
                    BtnHost.Visibility = Visibility.Collapsed;
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Erro ao conectar: {ex.Message}");
                }
            }
        }

        private void StreamManager_OnLocalVideoFrameReady(byte[] pixelData, int width, int height, int stride)
        {
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (_writeableBitmap == null || _writeableBitmap.PixelWidth != width || _writeableBitmap.PixelHeight != height || _writeableBitmap.Format != PixelFormats.Bgr24)
                {
                    _writeableBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr24, null);
                    VideoPlayer.Source = _writeableBitmap;
                }

                _writeableBitmap.Lock();
                Marshal.Copy(pixelData, 0, _writeableBitmap.BackBuffer, pixelData.Length);
                _writeableBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
                _writeableBitmap.Unlock();
            });
        }

        private void Client_OnBinaryReceived(byte[] data)
        {
            _streamManager?.ProcessReceivedBinary(data);
        }

        private void StreamManager_OnVideoFrameDecoded(byte[] pixelData, int width, int height, int stride)
        {
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (_writeableBitmap == null || _writeableBitmap.PixelWidth != width || _writeableBitmap.PixelHeight != height || _writeableBitmap.Format != PixelFormats.Bgr24)
                {
                    _writeableBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr24, null);
                    VideoPlayer.Source = _writeableBitmap;
                }

                _writeableBitmap.Lock();
                Marshal.Copy(pixelData, 0, _writeableBitmap.BackBuffer, pixelData.Length);
                _writeableBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
                _writeableBitmap.Unlock();
            });
        }


        private async void BtnStartStream_Click(object sender, RoutedEventArgs e)
        {
            if (CboWindows.SelectedItem is CaptureSource selectedSource)
            {
                BtnStartStream.Content = "Streaming...";
                BtnStartStream.IsEnabled = false;
                BtnStopStream.IsEnabled = true;
                CboWindows.IsEnabled = false;
                BtnClient.Visibility = Visibility.Collapsed;

                if (_streamManager != null)
                {
                    _streamManager.Stop();
                }

                _streamManager = new StreamManager();
                _streamManager.SetMaxPerformanceMode(ChkMaxPerformance?.IsChecked == true);
                
                _streamManager.OnAudioCaptureError += (error) => {
                    System.Windows.Application.Current.Dispatcher.Invoke(() => {
                        System.Windows.MessageBox.Show(error, "Aviso - Captura de Áudio", 
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                };
                
                _streamManager.OnLocalSdpReady += (clientId, sdpJson) => {
                    if (_server == null) return;
                    var clients = _server.GetType().GetField("_clients", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(_server) as System.Collections.Generic.List<Fleck.IWebSocketConnection>;
                    if (clients != null)
                    {
                        foreach (var client in clients)
                        {
                            if (client.ConnectionInfo.Id.ToString() == clientId || clientId == "host")
                            {
                                _server.SendMessage(client, sdpJson);
                            }
                        }
                    }
                };

                _streamManager.OnBinaryDataReady += (data) => {
                    _server?.BroadcastBinary(data);
                };

                _streamManager.OnLocalVideoFrameReady += (pixelData, width, height, stride) => {
                    StreamManager_OnLocalVideoFrameReady(pixelData, width, height, stride);
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                        StatusText.Visibility = Visibility.Collapsed;
                    });
                };

                _streamManager.OnConnectionStateChanged += (state) => {
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                        StatusText.Text = $"WebRTC: {state}";
                        StatusText.Visibility = Visibility.Visible;
                    });
                };

                await System.Threading.Tasks.Task.Run(() => 
                {
                    _streamManager.SetTargetSource(selectedSource);
                    _streamManager.SetResolution(1920, 1080); // Forçar 1080p HD
                    _streamManager.InitializeHost();
                });
                
                _server?.BroadcastMessage("STREAM_STARTED");
            }
            else
            {
                System.Windows.MessageBox.Show("Selecione uma janela para transmitir.");
            }
        }

        private void BtnStopStream_Click(object sender, RoutedEventArgs e)
        {
            if (_streamManager != null)
            {
                _streamManager.Stop();
                _streamManager = null;
            }
            _server?.BroadcastMessage("STREAM_STOPPED");
            BtnStartStream.Content = "Start Stream";
            BtnStartStream.IsEnabled = true;
            BtnStopStream.IsEnabled = false;
            CboWindows.IsEnabled = true;
            BtnClient.Visibility = Visibility.Visible;
            VideoPlayer.Source = null;
            _writeableBitmap = null;
            StatusText.Text = "No Signal";
            StatusText.Visibility = Visibility.Visible;
        }

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            _client?.Stop();
            _streamManager?.Stop();
            
            _client = null;
            _streamManager = null;
            
            BtnConnect.Content = "Connect";
            BtnConnect.IsEnabled = true;
            BtnDisconnect.IsEnabled = false;
            TxtHostIp.IsEnabled = true;
            BtnHost.Visibility = Visibility.Visible;
            VideoPlayer.Source = null;
            _writeableBitmap = null;
            StatusText.Text = "No Signal";
            StatusText.Visibility = Visibility.Visible;
        }
        
        protected override void OnClosed(EventArgs e)
        {
            _server?.Stop();
            _client?.Stop();
            _streamManager?.Stop();
            base.OnClosed(e);
            Environment.Exit(0);
        }

        private void BtnFullscreen_Click(object sender, RoutedEventArgs e)
        {
            if (WindowStyle == WindowStyle.None)
                ExitFullscreen();
            else
                EnterFullscreen();
        }

        private void VideoPlayer_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                if (TitleBarGrid.Visibility == Visibility.Collapsed)
                    ExitFullscreen();
                else
                    EnterFullscreen();
            }
        }

        private WindowState _previousWindowState = WindowState.Normal;

        private void EnterFullscreen()
        {
            _previousWindowState = WindowState;
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, null);
            Visibility = Visibility.Collapsed;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize; // Fixes white border in fullscreen
            Topmost = true;
            WindowState = WindowState.Maximized;
            Visibility = Visibility.Visible;
            TitleBarGrid.Visibility = Visibility.Collapsed;
            TopPanel.Visibility = Visibility.Collapsed;
            OverlayGrid.Visibility = Visibility.Visible;
            VideoBorder.Margin = new Thickness(0);
            VideoBorder.CornerRadius = new CornerRadius(0);
        }

        private void EnterTheaterMode()
        {
            WindowStyle = WindowStyle.None;
            TitleBarGrid.Visibility = Visibility.Collapsed;
            TopPanel.Visibility = Visibility.Collapsed;
            OverlayGrid.Visibility = Visibility.Visible;
            VideoBorder.Margin = new Thickness(0);
            VideoBorder.CornerRadius = new CornerRadius(0);
        }

        private void ExitFullscreen()
        {
            Topmost = false;
            var chrome = new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 32,
                ResizeBorderThickness = new Thickness(5),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0)
            };
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, chrome);

            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _previousWindowState;
            TitleBarGrid.Visibility = Visibility.Visible;
            TopPanel.Visibility = Visibility.Visible;
            OverlayGrid.Visibility = Visibility.Collapsed;
            VideoBorder.Margin = new Thickness(15, 0, 15, 15);
            VideoBorder.CornerRadius = new CornerRadius(8);
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            if (BtnSettings.ContextMenu != null)
            {
                BtnSettings.ContextMenu.PlacementTarget = BtnSettings;
                BtnSettings.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
                BtnSettings.ContextMenu.IsOpen = true;
            }
        }

        private void MenuItemQuality_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.MenuItem menuItem && menuItem.Tag is string quality)
            {
                if (quality == "1080p" || quality == "720p")
                {
                    BadgeHD.Visibility = Visibility.Visible;
                    TextHD.Text = "HD";
                }
                else
                {
                    BadgeHD.Visibility = Visibility.Collapsed;
                }

                if (_client != null)
                {
                    var msg = new SignalingMessage { Type = "SET_QUALITY", Data = quality, SenderId = "client" };
                    _client.SendMessage(System.Text.Json.JsonSerializer.Serialize(msg));
                }
                else if (_streamManager != null)
                {
                    if (quality == "1080p") _streamManager.SetResolution(1920, 1080);
                    else if (quality == "720p") _streamManager.SetResolution(1280, 720);
                    else if (quality == "480p") _streamManager.SetResolution(854, 480);
                }
            }
        }

        private void BtnTheater_Click(object sender, RoutedEventArgs e)
        {
            if (WindowStyle == WindowStyle.None)
                ExitFullscreen();
            else
                EnterTheaterMode();
        }

        private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left && e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                if (WindowStyle == WindowStyle.None)
                {
                    ExitFullscreen();
                }
            }
        }

        private void MainWindow_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (VideoControlsPanel != null)
            {
                VideoControlsPanel.Visibility = Visibility.Visible;
                Cursor = System.Windows.Input.Cursors.Arrow;
                _mouseIdleTimer.Stop();
                _mouseIdleTimer.Start();
            }
        }

        private void MouseIdleTimer_Tick(object sender, EventArgs e)
        {
            _mouseIdleTimer.Stop();
            if (VideoControlsPanel != null)
            {
                VideoControlsPanel.Visibility = Visibility.Collapsed;
                if (WindowStyle == WindowStyle.None) Cursor = System.Windows.Input.Cursors.None;
            }
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void BtnSettingsModal_Click(object sender, RoutedEventArgs e)
        {
            SettingsModalOverlay.Visibility = Visibility.Visible;
        }

        private void BtnCloseSettingsModal_Click(object sender, RoutedEventArgs e)
        {
            SettingsModalOverlay.Visibility = Visibility.Collapsed;
        }

        private void ChkMaxPerformance_Changed(object sender, RoutedEventArgs e)
        {
            if (ChkMaxPerformance == null) return;
            
            bool isMaxPerformance = ChkMaxPerformance.IsChecked == true;
            
            try
            {
                if (isMaxPerformance)
                {
                    Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
                }
                else
                {
                    Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Normal;
                }
            }
            catch { }
            
            if (_streamManager != null)
            {
                _streamManager.SetMaxPerformanceMode(isMaxPerformance);
            }
        }

        private void GithubLink_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://github.com",
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }
}