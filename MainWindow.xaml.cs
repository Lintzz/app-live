using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RadminStreamApp.Models;
using RadminStreamApp.Services;

namespace RadminStreamApp
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private SignalingServer _server;
        private StreamManager _hostStreamManager;
        private WriteableBitmap _hostBitmap;

        private ObservableCollection<Friend> _friends;
        private ICollectionView _friendsView;
        private readonly ObservableCollection<ViewerSession> _sessions = new ObservableCollection<ViewerSession>();
        private ViewerSession _activeSession;

        private PipWindow _activePip;
        private string _lastRoomPassword = string.Empty;
        private string _downloadUrl;

        private System.Windows.Threading.DispatcherTimer _mouseIdleTimer;
        private System.Windows.Threading.DispatcherTimer _statusTimer;

        private int _gridColumns = 1;
        /// <summary>Colunas da grade de lives — 1 live ocupa tudo, 2 lado a lado, 3-4 em 2x2, 5+ em 3 colunas.</summary>
        public int GridColumns
        {
            get => _gridColumns;
            private set { if (_gridColumns != value) { _gridColumns = value; OnPropertyChanged(); } }
        }

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            _mouseIdleTimer = new System.Windows.Threading.DispatcherTimer();
            _mouseIdleTimer.Interval = TimeSpan.FromSeconds(3);
            _mouseIdleTimer.Tick += MouseIdleTimer_Tick;
            this.MouseMove += MainWindow_MouseMove;
            this.Loaded += MainWindow_Loaded;

            _sessions.CollectionChanged += Sessions_CollectionChanged;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _friends = new ObservableCollection<Friend>(FriendsService.LoadFriends());

            _friendsView = CollectionViewSource.GetDefaultView(_friends);
            _friendsView.SortDescriptions.Add(new SortDescription(nameof(Friend.SortRank), ListSortDirection.Ascending));
            _friendsView.SortDescriptions.Add(new SortDescription(nameof(Friend.Name), ListSortDirection.Ascending));

            LstFriendsSidebar.ItemsSource = _friendsView;
            GridSessions.ItemsSource = _sessions;
            TabSessions.ItemsSource = _sessions;

            _friends.CollectionChanged += (s, ev) => UpdateSidebarEmptyStates();
            UpdateSidebarEmptyStates();

            var updateResult = await UpdateManager.CheckForUpdatesAsync();
            if (updateResult.HasUpdate)
            {
                _downloadUrl = updateResult.DownloadUrl;
                UpdateBannerText.Text = $"Uma nova versão ({updateResult.LatestVersion}) está disponível!";
                UpdateBanner.Visibility = Visibility.Visible;
            }

            StartStatusTimer();
        }

        // ───────────────────────────── Status dos amigos ─────────────────────────────

        private void StartStatusTimer()
        {
            _statusTimer = new System.Windows.Threading.DispatcherTimer();
            _statusTimer.Interval = TimeSpan.FromSeconds(30);
            _statusTimer.Tick += async (s, ev) => await RefreshAllFriendsStatusAsync();
            _statusTimer.Start();

            _ = RefreshAllFriendsStatusAsync();
        }

        private async System.Threading.Tasks.Task RefreshAllFriendsStatusAsync()
        {
            foreach (var friend in _friends.ToList())
            {
                await CheckFriendStatusAsync(friend);
            }

            _friendsView?.Refresh();
            UpdateSidebarEmptyStates();
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
                var bytes = System.Text.Encoding.UTF8.GetBytes(SignalingMessage.Serialize(checkMsg));
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

        private void UpdateSidebarEmptyStates()
        {
            bool hasFriends = _friends != null && _friends.Count > 0;
            SidebarEmptyState.Visibility = hasFriends ? Visibility.Collapsed : Visibility.Visible;

            bool anyOnline = hasFriends && _friends.Any(f => f.IsOnline);
            SidebarNoneOnline.Visibility = (hasFriends && !anyOnline) ? Visibility.Visible : Visibility.Collapsed;
        }

        // ───────────────────────────── Modos ─────────────────────────────

        private void BtnHost_Click(object sender, RoutedEventArgs e)
        {
            BtnHost.IsEnabled = false;
            BtnViewer.IsEnabled = true;

            PanelHost.Visibility = Visibility.Visible;
            PanelViewer.Visibility = Visibility.Collapsed;
            VideoBorder.Visibility = Visibility.Visible;
            ViewerArea.Visibility = Visibility.Collapsed;

            LoadScreens();

            if (_server == null)
            {
                _server = new SignalingServer();
                _server.OnMessageReceived += Server_OnMessageReceived;
                _server.OnClientConnected += (socket) => {
                    System.Windows.Application.Current.Dispatcher.Invoke(UpdateViewerCount);
                };
                _server.OnClientDisconnected += (socket) => {
                    _hostStreamManager?.RemoveClient(socket.ConnectionInfo.Id.ToString());
                    System.Windows.Application.Current.Dispatcher.Invoke(UpdateViewerCount);
                };
                _server.Start("0.0.0.0", 8080);
                UpdateViewerCount();
            }
        }

        private void BtnViewer_Click(object sender, RoutedEventArgs e)
        {
            BtnViewer.IsEnabled = false;
            BtnHost.IsEnabled = true;

            PanelHost.Visibility = Visibility.Collapsed;
            PanelViewer.Visibility = Visibility.Visible;
            VideoBorder.Visibility = Visibility.Collapsed;
            ViewerArea.Visibility = Visibility.Visible;

            UpdateSidebarEmptyStates();
            UpdateViewerLayout();
        }

        // ───────────────────────────── Host ─────────────────────────────

        private void LoadScreens()
        {
            var screens = WindowHelper.GetCapturableScreens();
            CboWindows.ItemsSource = screens;

            // Sem isso o campo abre em branco e o usuário precisa abrir o dropdown para conseguir dar Start.
            if (CboWindows.SelectedItem == null && screens.Count > 0)
            {
                CboWindows.SelectedIndex = 0;
            }
        }

        private void CboWindows_DropDownOpened(object sender, EventArgs e)
        {
            var previous = CboWindows.SelectedItem as CaptureSource;
            var screens = WindowHelper.GetCapturableScreens();
            CboWindows.ItemsSource = screens;

            if (previous != null)
            {
                var match = screens.FirstOrDefault(s => s.Title == previous.Title);
                if (match != null) CboWindows.SelectedItem = match;
            }

            if (CboWindows.SelectedItem == null && screens.Count > 0)
            {
                CboWindows.SelectedIndex = 0;
            }
        }

        private void CboWindows_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_hostStreamManager != null && BtnStopStream.IsEnabled && CboWindows.SelectedItem is CaptureSource selectedSource)
            {
                _hostStreamManager.SetTargetSource(selectedSource);
                _hostStreamManager.ForceKeyFrame();
                _server?.BroadcastMessage("SOURCE_CHANGED");
            }
        }

        private async void Server_OnMessageReceived(Fleck.IWebSocketConnection socket, string message)
        {
            if (_hostStreamManager != null)
                await _hostStreamManager.HandleSignalingMessage(socket.ConnectionInfo.Id.ToString(), message);
        }

        private void UpdateViewerCount()
        {
            if (_server == null) return;

            var ips = _server.ConnectedClientIps;
            int count = ips.Count;
            ViewerCountText.Text = $"{count} Viewer{(count != 1 ? "s" : "")}";

            if (count == 0)
            {
                ViewerCountPanel.ToolTip = "Ninguém assistindo ainda";
                return;
            }

            // IP conhecido vira o apelido salvo; desconhecido aparece como IP mesmo.
            var names = ips.Select(ip =>
            {
                var friend = _friends?.FirstOrDefault(f =>
                    string.Equals(SignalingServer.NormalizeIp(f.Ip), ip, StringComparison.OrdinalIgnoreCase));
                return friend != null ? friend.DisplayName : ip;
            });

            ViewerCountPanel.ToolTip = "Assistindo agora:\n• " + string.Join("\n• ", names);
        }

        private async void BtnStartStream_Click(object sender, RoutedEventArgs e)
        {
            if (!(CboWindows.SelectedItem is CaptureSource selectedSource))
            {
                System.Windows.MessageBox.Show("Selecione uma tela para transmitir.");
                return;
            }

            var passwordDialog = RoomPasswordDialog.ForHost(_lastRoomPassword);
            passwordDialog.Owner = this;
            if (passwordDialog.ShowDialog() != true) return;

            _lastRoomPassword = passwordDialog.Password ?? string.Empty;

            BtnStartStream.Content = "Streaming...";
            BtnStartStream.IsEnabled = false;
            BtnStopStream.IsEnabled = true;
            BtnViewer.IsEnabled = false;

            if (_hostStreamManager != null)
            {
                _hostStreamManager.Stop();
            }

            _hostStreamManager = new StreamManager();
            _hostStreamManager.SetMaxPerformanceMode(ChkMaxPerformance?.IsChecked == true);

            _hostStreamManager.OnAudioCaptureError += (error) => {
                System.Windows.Application.Current.Dispatcher.Invoke(() => {
                    System.Windows.MessageBox.Show(error, "Aviso - Captura de Áudio",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                });
            };

            _hostStreamManager.OnLocalSdpReady += (clientId, sdpJson) => {
                _server?.SendToClient(clientId, sdpJson);
            };

            _hostStreamManager.OnBinaryDataReady += (data) => {
                _server?.BroadcastBinary(data);
            };

            if (_server != null)
            {
                _server.RoomPassword = _lastRoomPassword;
                _server.IsStreaming = true;
            }

            _hostStreamManager.OnLocalVideoFrameReady += (pixelData, width, height, stride) => {
                UpdateHostBitmap(pixelData, width, height);
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                    HostEmptyState.Visibility = Visibility.Collapsed;
                });
            };

            _hostStreamManager.OnConnectionStateChanged += (state) => {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                    StatusText.Text = $"WebRTC: {state}";
                });
            };

            _hostStreamManager.OnHostStatsUpdated += (fps, kbps) => {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                    StatsOverlay.Visibility = Visibility.Visible;
                    StatsText.Text = $"📤 {fps}fps | {kbps:F1} kbps";
                });
            };

            await System.Threading.Tasks.Task.Run(() =>
            {
                _hostStreamManager.SetTargetSource(selectedSource);
                _hostStreamManager.SetResolution(1920, 1080);
                _hostStreamManager.InitializeHost();
            });

            _server?.BroadcastMessage("STREAM_STARTED");
        }

        private void BtnStopStream_Click(object sender, RoutedEventArgs e)
        {
            if (_server != null)
            {
                _server.IsStreaming = false;
                _server.BroadcastMessage("STREAM_STOPPED");
                _server.RoomPassword = string.Empty;
            }
            if (_hostStreamManager != null)
            {
                try { _hostStreamManager.Stop(); } catch { }
                _hostStreamManager = null;
            }

            BtnStartStream.Content = "Start";
            BtnStartStream.IsEnabled = true;
            BtnStopStream.IsEnabled = false;
            BtnViewer.IsEnabled = true;
            VideoPlayer.Source = null;
            _hostBitmap = null;
            StatusText.Text = "No Signal";
            HostEmptyState.Visibility = Visibility.Visible;
            StatsOverlay.Visibility = Visibility.Collapsed;
        }

        private void UpdateHostBitmap(byte[] pixelData, int width, int height)
        {
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (_hostBitmap == null || _hostBitmap.PixelWidth != width || _hostBitmap.PixelHeight != height || _hostBitmap.Format != PixelFormats.Bgr24)
                {
                    _hostBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr24, null);
                    VideoPlayer.Source = _hostBitmap;
                }

                _hostBitmap.Lock();
                Marshal.Copy(pixelData, 0, _hostBitmap.BackBuffer, pixelData.Length);
                _hostBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
                _hostBitmap.Unlock();
            });
        }

        // ───────────────────────────── Assistir ─────────────────────────────

        private async void FriendCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!(sender is FrameworkElement element) || !(element.DataContext is Friend friend)) return;
            await ToggleFriendSessionAsync(friend);
        }

        private async System.Threading.Tasks.Task ToggleFriendSessionAsync(Friend friend)
        {
            var existing = _sessions.FirstOrDefault(s => s.Friend == friend);
            if (existing != null)
            {
                CloseSession(existing);
                return;
            }

            if (!friend.IsOnline)
            {
                ViewerEmptyText.Text = $"{friend.DisplayName} está offline";
                return;
            }

            var session = new ViewerSession(friend);
            session.PasswordRequested += Session_PasswordRequested;

            _sessions.Add(session);
            SetActiveSession(session);

            try
            {
                await session.ConnectAsync();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Erro ao conectar com {friend.DisplayName}: {ex.Message}");
                CloseSession(session);
            }
        }

        private void Session_PasswordRequested(ViewerSession session, bool previousAttemptFailed)
        {
            var dialog = RoomPasswordDialog.ForViewer(session.FriendName, previousAttemptFailed);
            dialog.Owner = this;

            if (dialog.ShowDialog() == true)
            {
                session.SubmitPassword(dialog.Password);
            }
            else
            {
                session.CancelPassword();
                CloseSession(session);
            }
        }

        private void CloseSession(ViewerSession session)
        {
            if (session == null) return;

            session.PasswordRequested -= Session_PasswordRequested;
            session.Disconnect();
            _sessions.Remove(session);

            if (_activeSession == session)
            {
                SetActiveSession(_sessions.FirstOrDefault());
            }

            if (_activePip != null && _sessions.Count == 0)
            {
                _activePip.Close();
                _activePip = null;
            }
        }

        private void SetActiveSession(ViewerSession session)
        {
            if (_activeSession == session) return;

            if (_activeSession != null)
            {
                _activeSession.IsActive = false;
                _activeSession.PropertyChanged -= ActiveSession_PropertyChanged;
            }

            _activeSession = session;

            if (_activeSession != null)
            {
                _activeSession.IsActive = true;
                _activeSession.PropertyChanged += ActiveSession_PropertyChanged;
                _activePip?.SetBitmap(_activeSession.VideoBitmap);
            }
        }

        private void ActiveSession_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewerSession.VideoBitmap) && _activePip != null)
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    _activePip?.SetBitmap(_activeSession?.VideoBitmap));
            }
        }

        private void Sessions_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            UpdateViewerLayout();
            _friendsView?.Refresh();
        }

        private void UpdateViewerLayout()
        {
            int count = _sessions.Count;

            GridColumns = count <= 1 ? 1 : (count == 2 ? 2 : (count <= 4 ? 2 : 3));
            ViewerEmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (count == 0)
            {
                ViewerEmptyText.Text = "Clique em um amigo à esquerda para assistir";
            }

            // Com uma live só a sidebar sai da frente e volta quando o mouse encosta na borda esquerda.
            bool autoHideSidebar = count == 1;
            if (autoHideSidebar)
            {
                SidebarColumn.Width = new GridLength(0);
                System.Windows.Controls.Grid.SetColumnSpan(SidebarPanel, 2);
                SidebarPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
                SidebarPanel.Width = 230;
                SidebarPanel.Visibility = Visibility.Collapsed;
                SidebarHoverStrip.Visibility = Visibility.Visible;
            }
            else
            {
                SidebarColumn.Width = new GridLength(230);
                System.Windows.Controls.Grid.SetColumnSpan(SidebarPanel, 1);
                SidebarPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
                SidebarPanel.Width = double.NaN;
                SidebarPanel.Visibility = Visibility.Visible;
                SidebarHoverStrip.Visibility = Visibility.Collapsed;
            }
        }

        private void SidebarHoverStrip_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (SidebarHoverStrip.Visibility == Visibility.Visible)
            {
                SidebarPanel.Visibility = Visibility.Visible;
            }
        }

        private void SidebarPanel_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (SidebarHoverStrip.Visibility == Visibility.Visible)
            {
                SidebarPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void StreamTab_OnCloseRequested(ViewerSession session) => CloseSession(session);

        private void StreamTab_OnActivated(ViewerSession session) => SetActiveSession(session);

        private void StreamTab_OnFullscreenRequested(ViewerSession session)
        {
            SetActiveSession(session);
            if (WindowStyle == WindowStyle.None) ExitFullscreen();
            else EnterFullscreen();
        }

        private void TabSessions_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (TabSessions.SelectedItem is ViewerSession session) SetActiveSession(session);
        }

        private void ToggleTabsView_Changed(object sender, RoutedEventArgs e)
        {
            bool tabsMode = ToggleTabsView.IsChecked == true;
            TxtLayoutMode.Text = tabsMode ? "Abas" : "Grade";
            TabSessions.Visibility = tabsMode ? Visibility.Visible : Visibility.Collapsed;
            GridSessions.Visibility = tabsMode ? Visibility.Collapsed : Visibility.Visible;

            if (tabsMode && _activeSession != null)
            {
                TabSessions.SelectedItem = _activeSession;
            }
        }

        private void BtnManageFriends_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ManageFriendsDialog(_friends) { Owner = this };
            dialog.FriendAdded += (friend) => { _ = CheckFriendStatusAsync(friend); };
            dialog.ShowDialog();

            _ = RefreshAllFriendsStatusAsync();
            UpdateSidebarEmptyStates();
        }

        // ───────────────────────────── Controles de vídeo ─────────────────────────────

        private void BtnPip_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession?.VideoBitmap == null) return;

            if (_activePip == null)
            {
                _activePip = new PipWindow(_activeSession.VideoBitmap, v => _activeSession?.SetVolumeFromPip(v));
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

        private void BtnQuality_Click(object sender, RoutedEventArgs e)
        {
            if (BtnQuality.ContextMenu != null)
            {
                BtnQuality.ContextMenu.PlacementTarget = BtnQuality;
                BtnQuality.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                BtnQuality.ContextMenu.IsOpen = true;
            }
        }

        private void MenuItemQuality_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.MenuItem menuItem && menuItem.Tag is string quality)
            {
                _activeSession?.SetQuality(quality);
            }
        }

        private void BtnFullscreen_Click(object sender, RoutedEventArgs e)
        {
            if (WindowStyle == WindowStyle.None) ExitFullscreen();
            else EnterFullscreen();
        }

        private void BtnTheater_Click(object sender, RoutedEventArgs e)
        {
            if (WindowStyle == WindowStyle.None) ExitFullscreen();
            else EnterTheaterMode();
        }

        private void VideoPlayer_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                if (TitleBarGrid.Visibility == Visibility.Collapsed) ExitFullscreen();
                else EnterFullscreen();
            }
        }

        private WindowState _previousWindowState = WindowState.Normal;

        private void EnterFullscreen()
        {
            _previousWindowState = WindowState;
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, null);
            Visibility = Visibility.Collapsed;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            WindowState = WindowState.Maximized;
            Visibility = Visibility.Visible;
            TitleBarGrid.Visibility = Visibility.Collapsed;
            TopPanel.Visibility = Visibility.Collapsed;
            OverlayGrid.Visibility = Visibility.Visible;
            ApplyImmersiveMargins(true);
        }

        private void EnterTheaterMode()
        {
            WindowStyle = WindowStyle.None;
            TitleBarGrid.Visibility = Visibility.Collapsed;
            TopPanel.Visibility = Visibility.Collapsed;
            OverlayGrid.Visibility = Visibility.Visible;
            ApplyImmersiveMargins(true);
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
            ApplyImmersiveMargins(false);
        }

        private void ApplyImmersiveMargins(bool immersive)
        {
            VideoBorder.Margin = immersive ? new Thickness(0) : new Thickness(15, 0, 15, 15);
            VideoBorder.CornerRadius = immersive ? new CornerRadius(0) : new CornerRadius(12);
            ViewerArea.Margin = immersive ? new Thickness(0) : new Thickness(15, 0, 15, 15);
        }

        // ───────────────────────────── Janela ─────────────────────────────

        private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left && e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape && WindowStyle == WindowStyle.None)
            {
                ExitFullscreen();
            }
        }

        private void MainWindow_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            Cursor = System.Windows.Input.Cursors.Arrow;
            _mouseIdleTimer.Stop();
            _mouseIdleTimer.Start();
        }

        private void MouseIdleTimer_Tick(object sender, EventArgs e)
        {
            _mouseIdleTimer.Stop();
            if (WindowStyle == WindowStyle.None) Cursor = System.Windows.Input.Cursors.None;
        }

        private void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_downloadUrl))
            {
                BtnUpdate.Content = "Baixando...";
                BtnUpdate.IsEnabled = false;
                BtnDismissUpdate.IsEnabled = false;
                _ = UpdateManager.DownloadAndInstallUpdateAsync(_downloadUrl);
            }
        }

        private void BtnDismissUpdate_Click(object sender, RoutedEventArgs e)
        {
            UpdateBanner.Visibility = Visibility.Collapsed;
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnSettingsModal_Click(object sender, RoutedEventArgs e)
            => SettingsModalOverlay.Visibility = Visibility.Visible;

        private void BtnCloseSettingsModal_Click(object sender, RoutedEventArgs e)
            => SettingsModalOverlay.Visibility = Visibility.Collapsed;

        private void ChkMaxPerformance_Changed(object sender, RoutedEventArgs e)
        {
            if (ChkMaxPerformance == null) return;

            bool isMaxPerformance = ChkMaxPerformance.IsChecked == true;

            try
            {
                Process.GetCurrentProcess().PriorityClass = isMaxPerformance
                    ? ProcessPriorityClass.BelowNormal
                    : ProcessPriorityClass.Normal;
            }
            catch { }

            _hostStreamManager?.SetMaxPerformanceMode(isMaxPerformance);
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

        protected override void OnClosed(EventArgs e)
        {
            _server?.Stop();
            _hostStreamManager?.Stop();
            foreach (var session in _sessions.ToList())
            {
                session.Dispose();
            }
            base.OnClosed(e);
            Environment.Exit(0);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
