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

        public MainWindow()
        {
            InitializeComponent();
            
            _mouseIdleTimer = new System.Windows.Threading.DispatcherTimer();
            _mouseIdleTimer.Interval = TimeSpan.FromSeconds(3);
            _mouseIdleTimer.Tick += MouseIdleTimer_Tick;
            this.MouseMove += MainWindow_MouseMove;
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
            PanelHost.Visibility = Visibility.Visible;
            PanelClient.Visibility = Visibility.Collapsed;
            
            CboWindows.ItemsSource = WindowHelper.GetCapturableWindows();

            if (_server == null)
            {
                _server = new SignalingServer();
                _server.OnMessageReceived += Server_OnMessageReceived;
                _server.OnClientConnected += (socket) => {
                    System.Windows.Application.Current.Dispatcher.Invoke(() => {
                        // MessageBox removed
                    });
                };
                _server.Start("0.0.0.0", 8080);
                
                _streamManager = new StreamManager();
                _streamManager.OnAudioCaptureError += (error) => {
                    System.Windows.Application.Current.Dispatcher.Invoke(() => {
                        System.Windows.MessageBox.Show(error, "Aviso - Captura de Áudio", 
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                };
                _streamManager.OnLocalSdpReady += (sdp) => {
                    // Send SDP/ICE to all clients (in a real app, send to specific client)
                    foreach (var client in _server.GetType().GetField("_clients", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(_server) as System.Collections.Generic.List<Fleck.IWebSocketConnection>)
                    {
                        _server.SendMessage(client, sdp);
                    }
                };

                _streamManager.OnBinaryDataReady += (data) => {
                    _server.BroadcastBinary(data);
                    if (data.Length > 0 && data[0] == 0) // Local preview only needs video
                    {
                        var jpeg = new byte[data.Length - 1];
                        Buffer.BlockCopy(data, 1, jpeg, 0, jpeg.Length);
                        ShowFrameLocally(jpeg);
                    }
                };
            }
        }

        private void Server_OnMessageReceived(Fleck.IWebSocketConnection socket, string message)
        {
            _streamManager.SetRemoteDescription(message);
        }

        private void BtnClient_Click(object sender, RoutedEventArgs e)
        {
            PanelHost.Visibility = Visibility.Collapsed;
            PanelClient.Visibility = Visibility.Visible;
        }

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            string ip = TxtHostIp.Text;
            if (string.IsNullOrWhiteSpace(ip)) return;

            if (_client == null)
            {
                _client = new SignalingClient();
                _streamManager = new StreamManager();
                
                _client.OnMessageReceived += (message) => {
                    if (message == "STREAM_STOPPED")
                    {
                        System.Windows.Application.Current.Dispatcher.Invoke(() => {
                            VideoPlayer.Source = null;
                            StatusText.Text = "Transmissão Encerrada";
                            StatusText.Visibility = Visibility.Visible;
                        });
                        return;
                    }
                    _streamManager.SetRemoteDescription(message);
                };
                
                _client.OnBinaryReceived += (data) => {
                    _streamManager.ProcessReceivedBinary(data);
                };

                _streamManager.OnJpegFrameReceived += (jpeg) => {
                    ShowFrameLocally(jpeg);
                };
                
                try
                {
                    await _streamManager.InitializeClient();
                    await _client.StartAsync(ip, 8080);
                    BtnConnect.Content = "Connected";
                    BtnConnect.IsEnabled = false;
                    BtnDisconnect.IsEnabled = true;
                    TxtHostIp.IsEnabled = false;
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Erro ao conectar: {ex.Message}");
                }
            }
        }

        private void ShowFrameLocally(byte[] jpegBytes)
        {
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    using (var ms = new System.IO.MemoryStream(jpegBytes))
                    {
                        var bitmapImage = new BitmapImage();
                        bitmapImage.BeginInit();
                        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                        bitmapImage.StreamSource = ms;
                        bitmapImage.EndInit();
                        VideoPlayer.Source = bitmapImage;
                        StatusText.Visibility = Visibility.Collapsed;
                    }
                }
                catch
                {
                    // Ignore broken frames
                }
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
                if (_writeableBitmap == null || _writeableBitmap.PixelWidth != width || _writeableBitmap.PixelHeight != height)
                {
                    _writeableBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
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

                await System.Threading.Tasks.Task.Run(() => 
                {
                    _streamManager.SetTargetSource(selectedSource);
                    _streamManager.InitializeHost();
                });
            }
            else
            {
                System.Windows.MessageBox.Show("Selecione uma janela para transmitir.");
            }
        }

        private void BtnStopStream_Click(object sender, RoutedEventArgs e)
        {
            _streamManager.Stop();
            _server?.BroadcastMessage("STREAM_STOPPED");
            BtnStartStream.Content = "Start Stream";
            BtnStartStream.IsEnabled = true;
            BtnStopStream.IsEnabled = false;
            CboWindows.IsEnabled = true;
            VideoPlayer.Source = null;
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
            VideoPlayer.Source = null;
            StatusText.Text = "No Signal";
            StatusText.Visibility = Visibility.Visible;
        }
        
        protected override void OnClosed(EventArgs e)
        {
            _server?.Stop();
            _client?.Stop();
            _streamManager?.Stop();
            base.OnClosed(e);
        }

        private void BtnFullscreen_Click(object sender, RoutedEventArgs e)
        {
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

        private void EnterFullscreen()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize; // Fixes white border in fullscreen
            WindowState = WindowState.Maximized;
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
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Normal;
            TitleBarGrid.Visibility = Visibility.Visible;
            TopPanel.Visibility = Visibility.Visible;
            OverlayGrid.Visibility = Visibility.Collapsed;
            VideoBorder.Margin = new Thickness(15, 0, 15, 15);
            VideoBorder.CornerRadius = new CornerRadius(8);
        }

        private void BtnTheater_Click(object sender, RoutedEventArgs e)
        {
            EnterTheaterMode();
        }

        private void BtnExitFullscreen_Click(object sender, RoutedEventArgs e)
        {
            ExitFullscreen();
        }

        private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left && e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }

        private void MainWindow_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (OverlayGrid.Visibility == Visibility.Visible)
            {
                FullscreenControls.Visibility = Visibility.Visible;
                Cursor = System.Windows.Input.Cursors.Arrow;
                _mouseIdleTimer.Stop();
                _mouseIdleTimer.Start();
            }
            else
            {
                Cursor = System.Windows.Input.Cursors.Arrow;
            }
        }

        private void MouseIdleTimer_Tick(object sender, EventArgs e)
        {
            _mouseIdleTimer.Stop();
            if (OverlayGrid.Visibility == Visibility.Visible)
            {
                FullscreenControls.Visibility = Visibility.Collapsed;
                Cursor = System.Windows.Input.Cursors.None;
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
    }
}