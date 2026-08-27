using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Collections.ObjectModel;
using System.Linq;
using RadminStreamApp.Models;
using RadminStreamApp.Services;

namespace RadminStreamApp
{
    public partial class MainWindow : Window
    {
        private SignalingServer _server;
        private SignalingClient _client;
        private StreamManager _streamManager;
        private WriteableBitmap _writeableBitmap;
        private System.Windows.Threading.DispatcherTimer _mouseIdleTimer;
        private System.Windows.Threading.DispatcherTimer _statusTimer;

        private string _downloadUrl;
        private ObservableCollection<Friend> _friends;
        private int _lastViewerFps = 0;
        private int _lastLatencyMs = 0;
        private PipWindow _activePip;
        private ObservableCollection<ViewerSession> _watchPartySessions = new ObservableCollection<ViewerSession>();
        private bool _showingSourceChangedMessage = false;

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
            _friends = new ObservableCollection<Friend>(FriendsService.LoadFriends());
            CboFriends.ItemsSource = _friends;
            LstWatchPartyFriends.ItemsSource = _friends;
            TabWatchParty.ItemsSource = _watchPartySessions;
            GridWatchParty.ItemsSource = _watchPartySessions;

            var updateResult = await UpdateManager.CheckForUpdatesAsync();
            if (updateResult.HasUpdate)
            {
                _downloadUrl = updateResult.DownloadUrl;
                UpdateBannerText.Text = $"Uma nova versão ({updateResult.LatestVersion}) está disponível!";
                UpdateBanner.Visibility = Visibility.Visible;
            }

            StartStatusTimer();
        }

        private void StartStatusTimer()
        {
            _statusTimer = new System.Windows.Threading.DispatcherTimer();
            _statusTimer.Interval = TimeSpan.FromSeconds(10);
            _statusTimer.Tick += async (s, ev) => {
                foreach (var friend in _friends.ToList())
                {
                    await CheckFriendStatusAsync(friend);
                }
            };
            _statusTimer.Start();
            
            foreach (var friend in _friends.ToList())
            {
                _ = CheckFriendStatusAsync(friend);
            }
        }

        private async System.Threading.Tasks.Task CheckFriendStatusAsync(Friend friend)
        {
            try
            {
                using var tcpClient = new System.Net.Sockets.TcpClient();
                var connectTask = tcpClient.ConnectAsync(friend.Ip, 8080);
                if (await System.Threading.Tasks.Task.WhenAny(connectTask, System.Threading.Tasks.Task.Delay(1000)) != connectTask)
                {
                    friend.IsOnline = false;
                    friend.IsStreaming = false;
                    return;
                }
                
                friend.IsOnline = true;
                
                using var ws = new System.Net.WebSockets.ClientWebSocket();
                var wsConnectTask = ws.ConnectAsync(new Uri($"ws://{friend.Ip}:8080"), System.Threading.CancellationToken.None);
                if (await System.Threading.Tasks.Task.WhenAny(wsConnectTask, System.Threading.Tasks.Task.Delay(1000)) != wsConnectTask)
                {
                    friend.IsStreaming = false;
                    return;
                }
                
                var checkMsg = new SignalingMessage { Type = "STATUS_CHECK" };
                var bytes = System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(checkMsg));
                await ws.SendAsync(new ArraySegment<byte>(bytes), System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);
                
                var buffer = new byte[1024];
                var receiveTask = ws.ReceiveAsync(new ArraySegment<byte>(buffer), System.Threading.CancellationToken.None);
                if (await System.Threading.Tasks.Task.WhenAny(receiveTask, System.Threading.Tasks.Task.Delay(1000)) == receiveTask)
                {
                    var result = receiveTask.Result;
                    var responseText = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
                    var responseMsg = SignalingMessage.Deserialize(responseText);
                    if (responseMsg != null && responseMsg.Type == "STATUS_RESPONSE")
                    {
                        friend.IsStreaming = (responseMsg.Data == "STREAMING");
                    }
                }
                
                try { await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "", System.Threading.CancellationToken.None); } catch { }
            }
            catch
            {
                friend.IsOnline = false;
                friend.IsStreaming = false;
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

        private void CboWindows_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_streamManager != null && BtnStopStream.IsEnabled && CboWindows.SelectedItem is CaptureSource selectedSource)
            {
                _streamManager.SetTargetSource(selectedSource);
                _streamManager.ForceKeyFrame();
                _server?.BroadcastMessage("SOURCE_CHANGED");
            }
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
            BtnWatchParty.IsEnabled = true;
            BtnHost.IsEnabled = false;

            PanelHost.Visibility = Visibility.Visible;
            PanelClient.Visibility = Visibility.Collapsed;
            PanelWatchParty.Visibility = Visibility.Collapsed;
            WatchPartyArea.Visibility = Visibility.Collapsed;
            VideoBorder.Visibility = Visibility.Visible;

            IconVolume.Visibility = Visibility.Collapsed;
            SliderVolume.Visibility = Visibility.Collapsed;
            BtnSettings.Visibility = Visibility.Collapsed;
            BtnPip.Visibility = Visibility.Collapsed;
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
                    _streamManager?.RemoveClient(socket.ConnectionInfo.Id.ToString());
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
            BtnWatchParty.IsEnabled = true;
            BtnClient.IsEnabled = false;

            PanelHost.Visibility = Visibility.Collapsed;
            PanelClient.Visibility = Visibility.Visible;
            PanelWatchParty.Visibility = Visibility.Collapsed;
            WatchPartyArea.Visibility = Visibility.Collapsed;
            VideoBorder.Visibility = Visibility.Visible;

            IconVolume.Visibility = Visibility.Visible;
            SliderVolume.Visibility = Visibility.Visible;
            BtnSettings.Visibility = Visibility.Visible;
            BtnPip.Visibility = Visibility.Visible;
            BtnTheater.Visibility = Visibility.Visible;
            BtnFullscreen.Visibility = Visibility.Visible;
        }

        private void BtnWatchParty_Click(object sender, RoutedEventArgs e)
        {
            BtnHost.Visibility = Visibility.Visible;
            BtnHost.IsEnabled = true;
            BtnClient.Visibility = Visibility.Visible;
            BtnClient.IsEnabled = true;
            BtnWatchParty.IsEnabled = false;

            PanelHost.Visibility = Visibility.Collapsed;
            PanelClient.Visibility = Visibility.Collapsed;
            PanelWatchParty.Visibility = Visibility.Visible;

            IconVolume.Visibility = Visibility.Collapsed;
            SliderVolume.Visibility = Visibility.Collapsed;
            BtnSettings.Visibility = Visibility.Collapsed;
            BtnPip.Visibility = Visibility.Collapsed;
            BtnTheater.Visibility = Visibility.Collapsed;
            BtnFullscreen.Visibility = Visibility.Collapsed;

            VideoBorder.Visibility = Visibility.Collapsed;
            WatchPartyArea.Visibility = Visibility.Visible;
        }

        private async void BtnConnectWatchParty_Click(object sender, RoutedEventArgs e)
        {
            var selected = LstWatchPartyFriends.SelectedItems.Cast<Friend>().ToList();
            if (selected.Count == 0)
            {
                System.Windows.MessageBox.Show("Selecione ao menos um amigo na lista.");
                return;
            }

            string password = TxtWatchPartyPassword.Text;
            foreach (var friend in selected)
            {
                if (_watchPartySessions.Any(s => s.Ip == friend.Ip)) continue;

                var session = new ViewerSession(friend.Name, friend.Ip, password);
                _watchPartySessions.Add(session);
                TabWatchParty.SelectedItem = session;

                try
                {
                    await session.ConnectAsync();
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Erro ao conectar com {friend.Name}: {ex.Message}");
                    _watchPartySessions.Remove(session);
                }
            }
        }

        private void StreamTab_OnCloseRequested(ViewerSession session)
        {
            session.Disconnect();
            _watchPartySessions.Remove(session);
        }

        private void ToggleGridView_Changed(object sender, RoutedEventArgs e)
        {
            bool gridMode = ToggleGridView.IsChecked == true;
            TabWatchParty.Visibility = gridMode ? Visibility.Collapsed : Visibility.Visible;
            GridWatchParty.Visibility = gridMode ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            string ip = CboFriends.Text;
            if (string.IsNullOrWhiteSpace(ip)) return;

            if (_client == null)
            {
                _client = new SignalingClient();

                _client.OnMessageReceived += async (message) => {
                    if (message == "AUTH_REQUIRED")
                    {
                        var pwd = "";
                        System.Windows.Application.Current.Dispatcher.Invoke(() => { pwd = TxtClientPassword.Text; });
                        if (string.IsNullOrEmpty(pwd))
                        {
                            System.Windows.Application.Current.Dispatcher.Invoke(() => {
                                System.Windows.MessageBox.Show("Esta sala requer uma senha. Preencha o campo de Senha e tente novamente.");
                                BtnDisconnect_Click(null, null);
                            });
                        }
                        else
                        {
                            var authMsg = new SignalingMessage { Type = "AUTH", Data = pwd };
                            _client.SendMessage(System.Text.Json.JsonSerializer.Serialize(authMsg));
                        }
                        return;
                    }
                    if (message == "AUTH_FAIL")
                    {
                        System.Windows.Application.Current.Dispatcher.Invoke(() => {
                            System.Windows.MessageBox.Show("Senha incorreta!");
                            BtnDisconnect_Click(null, null);
                        });
                        return;
                    }
                    if (message == "AUTH_OK")
                    {
                        System.Windows.Application.Current.Dispatcher.Invoke(() => {
                            _client.EnableEncryption(TxtClientPassword.Text);
                            var helloMsg = new SignalingMessage { Type = "CLIENT_CONNECTED", Data = "", SenderId = "client" };
                            _client.SendMessage(System.Text.Json.JsonSerializer.Serialize(helloMsg));
                        });
                        return;
                    }
                    if (message == "SOURCE_CHANGED")
                    {
                        System.Windows.Application.Current.Dispatcher.InvokeAsync(async () => {
                            _showingSourceChangedMessage = true;
                            StatusText.Text = "Host trocou de tela...";
                            StatusText.Visibility = Visibility.Visible;
                            await System.Threading.Tasks.Task.Delay(2000);
                            _showingSourceChangedMessage = false;
                            if (_streamManager != null) StatusText.Visibility = Visibility.Collapsed;
                        });
                        return;
                    }
                    if (message == "STREAM_STOPPED")
                    {
                        System.Windows.Application.Current.Dispatcher.Invoke(() => {
                            try { _streamManager?.Stop(); } catch { }
                            VideoPlayer.Source = null;
                            StatusText.Text = "Transmissão Encerrada";
                            StatusText.Visibility = Visibility.Visible;
                            StatsOverlay.Visibility = Visibility.Collapsed;
                        });
                        return;
                    }
                    if (message == "STREAM_STARTED")
                    {
                        System.Windows.Application.Current.Dispatcher.Invoke(async () => {
                            await SetupClientStreamManagerAsync();
                            var msg = new SignalingMessage { Type = "CLIENT_CONNECTED", Data = "", SenderId = "client" };
                            _client.SendMessage(System.Text.Json.JsonSerializer.Serialize(msg));
                        });
                        return;
                    }
                    var parsed = SignalingMessage.Deserialize(message);
                    if (parsed != null && parsed.Type == "STATUS_RESPONSE")
                    {
                        if (parsed.Data == "IDLE")
                        {
                            System.Windows.Application.Current.Dispatcher.Invoke(() => {
                                StatusText.Text = "Host não está em live";
                                StatusText.Visibility = Visibility.Visible;
                            });
                        }
                        return;
                    }

                    if (_streamManager != null)
                        await _streamManager.HandleSignalingMessage("host", message);
                };

                _client.OnBinaryReceived += (data) => {
                    _streamManager?.ProcessReceivedBinary(data);
                };

                _client.OnConnected += (isReconnect) => {
                    System.Windows.Application.Current.Dispatcher.Invoke(async () => {
                        if (isReconnect)
                        {
                            StatusText.Text = "Reconectado!";
                            StatusText.Visibility = Visibility.Visible;
                            await SetupClientStreamManagerAsync();
                            _ = HideStatusTextAfterDelay(2000);
                        }
                        var statusMsg = new SignalingMessage { Type = "STATUS_CHECK" };
                        _client.SendMessage(System.Text.Json.JsonSerializer.Serialize(statusMsg));
                        var helloMsg = new SignalingMessage { Type = "CLIENT_CONNECTED", Data = "", SenderId = "client" };
                        _client.SendMessage(System.Text.Json.JsonSerializer.Serialize(helloMsg));
                    });
                };

                _client.OnReconnecting += (attempt) => {
                    System.Windows.Application.Current.Dispatcher.Invoke(() => {
                        StatusText.Text = $"Reconectando... ({attempt}/10)";
                        StatusText.Visibility = Visibility.Visible;
                    });
                };

                _client.OnReconnectFailed += () => {
                    System.Windows.Application.Current.Dispatcher.Invoke(() => {
                        System.Windows.MessageBox.Show("Não foi possível reconectar ao host.");
                        BtnDisconnect_Click(null, null);
                    });
                };

                _client.OnLatencyUpdated += (ms) => {
                    _lastLatencyMs = ms;
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(UpdateViewerStatsOverlay);
                };

                try
                {
                    await SetupClientStreamManagerAsync();
                    await _client.StartAsync(ip, 8080);

                    BtnConnect.Content = "Connected";
                    BtnConnect.IsEnabled = false;
                    BtnDisconnect.IsEnabled = true;
                    CboFriends.IsEnabled = false;
                    BtnAddFriend.IsEnabled = false;
                    BtnRemoveFriend.IsEnabled = false;
                    BtnHost.Visibility = Visibility.Collapsed;
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Erro ao conectar: {ex.Message}");
                    _client = null;
                }
            }
        }

        private async System.Threading.Tasks.Task SetupClientStreamManagerAsync()
        {
            if (_streamManager != null)
            {
                try { _streamManager.Stop(); } catch { }
            }

            _streamManager = new StreamManager();
            _streamManager.SetVolume((float)SliderVolume.Value / 100f);

            _streamManager.OnVideoFrameDecoded += (pixelData, width, height, stride) => {
                StreamManager_OnVideoFrameDecoded(pixelData, width, height, stride);
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                    if (!_showingSourceChangedMessage) StatusText.Visibility = Visibility.Collapsed;
                });
            };

            _streamManager.OnConnectionStateChanged += (state) => {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                    if (StatusText.Text == "Transmissão Encerrada" && (state.ToString() == "closed" || state.ToString() == "disconnected" || state.ToString() == "failed")) return;
                    StatusText.Text = $"WebRTC: {state}";
                    StatusText.Visibility = Visibility.Visible;
                });
            };

            _streamManager.OnLocalSdpReady += (clientId, sdpJson) => {
                _client?.SendMessage(sdpJson);
            };

            _streamManager.OnViewerFpsUpdated += (fps) => {
                _lastViewerFps = fps;
                System.Windows.Application.Current.Dispatcher.InvokeAsync(UpdateViewerStatsOverlay);
            };

            await _streamManager.InitializeClient();
        }

        private void UpdateViewerStatsOverlay()
        {
            StatsOverlay.Visibility = Visibility.Visible;
            StatsText.Text = $"📥 {_lastViewerFps}fps | {_lastLatencyMs}ms";
        }

        private async System.Threading.Tasks.Task HideStatusTextAfterDelay(int delayMs)
        {
            await System.Threading.Tasks.Task.Delay(delayMs);
            System.Windows.Application.Current.Dispatcher.Invoke(() => {
                if (_streamManager != null) StatusText.Visibility = Visibility.Collapsed;
            });
        }

        private void StreamManager_OnLocalVideoFrameReady(byte[] pixelData, int width, int height, int stride)
        {
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (_writeableBitmap == null || _writeableBitmap.PixelWidth != width || _writeableBitmap.PixelHeight != height || _writeableBitmap.Format != PixelFormats.Bgr24)
                {
                    _writeableBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr24, null);
                    VideoPlayer.Source = _writeableBitmap;
                    _activePip?.SetBitmap(_writeableBitmap);
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
                    _activePip?.SetBitmap(_writeableBitmap);
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
                    _server.SendToClient(clientId, sdpJson);
                };

                _streamManager.OnBinaryDataReady += (data) => {
                    _server?.BroadcastBinary(data);
                };

                if (_server != null)
                {
                    _server.RoomPassword = TxtRoomPassword.Text;
                    _server.IsStreaming = true;
                }

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

                _streamManager.OnHostStatsUpdated += (fps, kbps) => {
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                        StatsOverlay.Visibility = Visibility.Visible;
                        StatsText.Text = $"📤 {fps}fps | {kbps:F1} kbps";
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
            if (_server != null)
            {
                _server.IsStreaming = false;
                _server.BroadcastMessage("STREAM_STOPPED");
            }
            if (_streamManager != null)
            {
                try { _streamManager.Stop(); } catch { }
                _streamManager = null;
            }
            BtnStartStream.Content = "Start Stream";
            BtnStartStream.IsEnabled = true;
            BtnStopStream.IsEnabled = false;
            CboWindows.IsEnabled = true;
            BtnClient.Visibility = Visibility.Visible;
            VideoPlayer.Source = null;
            _writeableBitmap = null;
            StatusText.Text = "No Signal";
            StatusText.Visibility = Visibility.Visible;
            StatsOverlay.Visibility = Visibility.Collapsed;
        }

        private void CboFriends_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (CboFriends.SelectedItem is Friend friend)
            {
                // No action needed for text update since DisplayMemberPath is set or we handle it via binding
            }
        }

        private void BtnAddFriend_Click(object sender, RoutedEventArgs e)
        {
            string ip = CboFriends.Text?.Trim();
            if (string.IsNullOrWhiteSpace(ip)) return;

            var dialog = new AddFriendDialog(ip) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                string name = string.IsNullOrWhiteSpace(dialog.FriendName) ? ip : dialog.FriendName;
                var friend = new Friend { Name = name, Ip = ip };
                _friends.Add(friend);
                FriendsService.SaveFriends(new System.Collections.Generic.List<Friend>(_friends));
                CboFriends.SelectedItem = friend;
                _ = CheckFriendStatusAsync(friend);
            }
        }

        private void BtnRemoveFriend_Click(object sender, RoutedEventArgs e)
        {
            if (CboFriends.SelectedItem is Friend friend)
            {
                _friends.Remove(friend);
                FriendsService.SaveFriends(new System.Collections.Generic.List<Friend>(_friends));
            }
        }

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            _client?.Stop();
            _streamManager?.Stop();

            _client = null;
            _streamManager = null;

            _activePip?.Close();
            _activePip = null;

            BtnConnect.Content = "Connect";
            BtnConnect.IsEnabled = true;
            BtnDisconnect.IsEnabled = false;
            CboFriends.IsEnabled = true;
            BtnAddFriend.IsEnabled = true;
            BtnRemoveFriend.IsEnabled = true;
            BtnHost.Visibility = Visibility.Visible;
            VideoPlayer.Source = null;
            _writeableBitmap = null;
            StatusText.Text = "No Signal";
            StatusText.Visibility = Visibility.Visible;
            StatsOverlay.Visibility = Visibility.Collapsed;
        }
        
        protected override void OnClosed(EventArgs e)
        {
            _server?.Stop();
            _client?.Stop();
            _streamManager?.Stop();
            foreach (var session in _watchPartySessions.ToList())
            {
                session.Dispose();
            }
            base.OnClosed(e);
            Environment.Exit(0);
        }

        private void BtnPip_Click(object sender, RoutedEventArgs e)
        {
            if (_writeableBitmap == null) return;

            if (_activePip == null)
            {
                _activePip = new PipWindow(_writeableBitmap, v => _streamManager?.SetVolume(v));
                _activePip.OnRestoreRequested += () => {
                    System.Windows.Application.Current.Dispatcher.Invoke(() => {
                        WindowState = WindowState.Normal;
                        Activate();
                        _activePip?.Close();
                    });
                };
                _activePip.Closed += (s, ev) => { _activePip = null; };
                _activePip.Show();
            }

            WindowState = WindowState.Minimized;
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