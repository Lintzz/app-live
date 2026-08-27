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
        private ICollectionView _gridView;
        private ViewerSession _focusedSession;

        private ViewerSession _activeSession;
        /// <summary>Live em foco: recebe o volume da barra, o PiP e o destaque na borda.</summary>
        public ViewerSession ActiveSession
        {
            get => _activeSession;
            private set { if (_activeSession != value) { _activeSession = value; OnPropertyChanged(); } }
        }

        private bool _showStats;
        /// <summary>Liga o contador de fps/latência sobre cada live.</summary>
        public bool ShowStats
        {
            get => _showStats;
            private set { if (_showStats != value) { _showStats = value; OnPropertyChanged(); } }
        }

        private PipWindow _activePip;
        private string _lastRoomPassword = string.Empty;
        private string _downloadUrl;

        private System.Windows.Threading.DispatcherTimer _mouseIdleTimer;
        private System.Windows.Threading.DispatcherTimer _statusTimer;

        // Evita que sincronizar o estado visual dos toggles dispare os handlers de novo.
        private bool _syncingToggles;
        private bool _isBroadcasting;

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
            _mouseIdleTimer.Interval = TimeSpan.FromSeconds(2.5);
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

            // View própria: filtrar o foco aqui não afeta a lista de abas.
            _gridView = new CollectionViewSource { Source = _sessions }.View;
            GridSessions.ItemsSource = _gridView;
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
            StartSignalingServer();
            UpdateScreenOverlapWarning();

            LocationChanged += (s2, e2) => UpdateScreenOverlapWarning();
            SizeChanged += (s2, e2) => UpdateScreenOverlapWarning();

            LoadScreens();
        }

        /// <summary>
        /// O servidor sobe junto com o app (e não só ao transmitir): é ele que responde ao
        /// STATUS_CHECK dos amigos, o que faz você aparecer como "online" na lista deles.
        /// </summary>
        private void StartSignalingServer()
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

            if (!_server.Start("0.0.0.0", 8080))
            {
                BtnStartStream.IsEnabled = false;
                BtnStartStream.ToolTip = "A porta 8080 já está em uso (outra instância do app aberta). " +
                                         "Você pode assistir normalmente, mas não transmitir.";
                System.Windows.MessageBox.Show(
                    "Não foi possível abrir a porta 8080 — provavelmente há outra instância do app aberta.\n\n" +
                    "Você ainda pode assistir às lives dos seus amigos, mas não vai conseguir transmitir por esta janela.",
                    "Transmissão indisponível", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            UpdateViewerCount();
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
                    Observe(connectTask);
                    friend.IsOnline = false;
                    friend.IsStreaming = false;
                    return;
                }

                friend.IsOnline = true;

                using var ws = new System.Net.WebSockets.ClientWebSocket();
                var wsConnectTask = ws.ConnectAsync(new Uri($"ws://{friend.Ip}:8080"), System.Threading.CancellationToken.None);
                if (await System.Threading.Tasks.Task.WhenAny(wsConnectTask, System.Threading.Tasks.Task.Delay(1000)) != wsConnectTask)
                {
                    Observe(wsConnectTask);
                    friend.IsStreaming = false;
                    return;
                }

                var checkMsg = new SignalingMessage { Type = "STATUS_CHECK" };
                var bytes = System.Text.Encoding.UTF8.GetBytes(SignalingMessage.Serialize(checkMsg));
                await ws.SendAsync(new ArraySegment<byte>(bytes), System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);

                var buffer = new byte[1024];
                var receiveTask = ws.ReceiveAsync(new ArraySegment<byte>(buffer), System.Threading.CancellationToken.None);
                if (await System.Threading.Tasks.Task.WhenAny(receiveTask, System.Threading.Tasks.Task.Delay(1000)) != receiveTask)
                {
                    Observe(receiveTask);
                }
                else
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

        /// <summary>
        /// Marca como observada a task que perdeu a corrida para o timeout. Sem isso o socket
        /// abortado no Dispose vira uma exceção não observada relançada pelo finalizador.
        /// </summary>
        private static void Observe(System.Threading.Tasks.Task task)
        {
            task.ContinueWith(t => { _ = t.Exception; },
                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted |
                System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously);
        }

        private void UpdateSidebarEmptyStates()
        {
            bool hasFriends = _friends != null && _friends.Count > 0;
            SidebarEmptyState.Visibility = hasFriends ? Visibility.Collapsed : Visibility.Visible;

            bool anyOnline = hasFriends && _friends.Any(f => f.IsOnline);
            SidebarNoneOnline.Visibility = (hasFriends && !anyOnline) ? Visibility.Visible : Visibility.Collapsed;
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
            if (_hostStreamManager != null && _isBroadcasting && CboWindows.SelectedItem is CaptureSource selectedSource)
            {
                _hostStreamManager.SetTargetSource(selectedSource);
                _hostStreamManager.ForceKeyFrame();
                _server?.BroadcastMessage("SOURCE_CHANGED");
            }

            UpdateScreenOverlapWarning();
        }

        private void BtnTogglePreview_Changed(object sender, RoutedEventArgs e)
        {
            HostPreviewCard.Visibility = BtnTogglePreview.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnClosePreview_Click(object sender, RoutedEventArgs e)
        {
            BtnTogglePreview.IsChecked = false;
        }

        /// <summary>
        /// Avisa quando a janela do app está no monitor que está sendo transmitido — nesse caso
        /// as lives que você assiste aparecem dentro da sua própria transmissão.
        /// </summary>
        private void UpdateScreenOverlapWarning()
        {
            if (ScreenOverlapWarning == null) return;

            if (!_isBroadcasting || !(CboWindows.SelectedItem is CaptureSource source))
            {
                ScreenOverlapWarning.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                var centerX = Left + Width / 2;
                var centerY = Top + Height / 2;
                var bounds = source.ScreenBounds;
                bool onSameScreen = centerX >= bounds.Left && centerX <= bounds.Right
                                 && centerY >= bounds.Top && centerY <= bounds.Bottom;

                ScreenOverlapWarning.Visibility = onSameScreen ? Visibility.Visible : Visibility.Collapsed;
            }
            catch
            {
                ScreenOverlapWarning.Visibility = Visibility.Collapsed;
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
            ViewerCountText.Text = count == 1 ? "1 assistindo" : $"{count} assistindo";

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

            _isBroadcasting = true;
            BtnStartStream.Visibility = Visibility.Collapsed;
            BtnStopStream.Visibility = Visibility.Visible;
            BtnTogglePreview.Visibility = Visibility.Visible;
            ViewerCountPanel.Visibility = Visibility.Visible;
            LiveBadge.Visibility = Visibility.Visible;
            UpdateScreenOverlapWarning();

            // As lives abertas silenciam: o som delas voltaria para os seus viewers pelo loopback.
            ApplyBroadcastMuteToSessions(true);

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
                    StatusText.Visibility = Visibility.Collapsed;
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

            _isBroadcasting = false;
            BtnStartStream.Visibility = Visibility.Visible;
            BtnStopStream.Visibility = Visibility.Collapsed;
            BtnTogglePreview.Visibility = Visibility.Collapsed;
            BtnTogglePreview.IsChecked = false;
            ViewerCountPanel.Visibility = Visibility.Collapsed;
            LiveBadge.Visibility = Visibility.Collapsed;
            ScreenOverlapWarning.Visibility = Visibility.Collapsed;

            // Devolve o som só das lives que o próprio app silenciou.
            ApplyBroadcastMuteToSessions(false);

            VideoPlayer.Source = null;
            _hostBitmap = null;
            StatusText.Text = "Sem sinal";
            StatusText.Visibility = Visibility.Visible;
            StatsOverlay.Visibility = Visibility.Collapsed;
        }

        private void ApplyBroadcastMuteToSessions(bool broadcasting)
        {
            foreach (var session in _sessions.ToList())
            {
                session.ApplyBroadcastMute(broadcasting);
            }
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
            if (_isBroadcasting) session.ApplyBroadcastMute(true);

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

            if (ActiveSession == session)
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
            if (ActiveSession == session)
            {
                UpdatePlayerControlsState();
                return;
            }

            if (ActiveSession != null)
            {
                ActiveSession.IsActive = false;
                ActiveSession.PropertyChanged -= ActiveSession_PropertyChanged;
            }

            ActiveSession = session;

            if (ActiveSession != null)
            {
                ActiveSession.IsActive = true;
                ActiveSession.PropertyChanged += ActiveSession_PropertyChanged;
                _activePip?.SetBitmap(ActiveSession.VideoBitmap);
            }

            UpdatePlayerControlsState();
        }

        /// <summary>Mantém volume, mudo e foco da barra apontando para a live ativa.</summary>
        private void UpdatePlayerControlsState()
        {
            bool hasSession = ActiveSession != null;
            bool tabsMode = ToggleTabsView.IsChecked == true;
            VolumeControls.Visibility = hasSession ? Visibility.Visible : Visibility.Collapsed;

            // Em abas já se vê uma live por vez; focar ali não significa nada.
            ToggleFocus.Visibility = (hasSession && !tabsMode) ? Visibility.Visible : Visibility.Collapsed;

            _syncingToggles = true;
            BtnMute.IsChecked = hasSession && ActiveSession.IsMuted;
            ToggleFocus.IsChecked = hasSession && _focusedSession == ActiveSession;
            _syncingToggles = false;
        }

        private void ActiveSession_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewerSession.IsMuted))
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(UpdatePlayerControlsState);
                return;
            }

            if (e.PropertyName == nameof(ViewerSession.VideoBitmap) && _activePip != null)
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    _activePip?.SetBitmap(ActiveSession?.VideoBitmap));
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

            if (_focusedSession != null && !_sessions.Contains(_focusedSession))
            {
                _focusedSession = null;
                _gridView?.Refresh();
            }

            int visible = _focusedSession != null ? 1 : count;
            GridColumns = visible <= 1 ? 1 : (visible == 2 ? 2 : (visible <= 4 ? 2 : 3));
            ViewerEmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (count == 0)
            {
                ViewerEmptyText.Text = "Clique em um amigo à esquerda para assistir";
            }

            // Com uma live só a sidebar sai da frente e volta quando o mouse encosta na borda esquerda.
            // Com uma live só a sidebar sai da frente; a aba na borda esquerda a traz de volta.
            bool collapsible = count >= 1;
            if (collapsible)
            {
                SidebarColumn.Width = new GridLength(0);
                System.Windows.Controls.Grid.SetColumnSpan(SidebarPanel, 2);
                SidebarPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
                SidebarPanel.Width = 230;
                SidebarHandle.Visibility = Visibility.Visible;
                SetSidebarOpen(false);
            }
            else
            {
                SidebarColumn.Width = new GridLength(230);
                System.Windows.Controls.Grid.SetColumnSpan(SidebarPanel, 1);
                SidebarPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
                SidebarPanel.Width = double.NaN;
                SidebarPanel.Visibility = Visibility.Visible;
                SidebarHandle.Visibility = Visibility.Collapsed;
            }
        }

        private void SidebarHandle_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Sem marcar como tratado, o clique sobe para a janela e vira DragMove.
            e.Handled = true;
            SetSidebarOpen(SidebarPanel.Visibility != Visibility.Visible);
        }

        private void SetSidebarOpen(bool open)
        {
            SidebarPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            SidebarHandle.Margin = open ? new Thickness(215, 0, 0, 0) : new Thickness(-15, 0, 0, 0);
            SidebarHandleArrow.Text = open ? "\uE76B" : "\uE76C";
            SidebarHandle.ToolTip = open ? "Esconder amigos" : "Mostrar amigos";
        }

        private void StreamTab_OnCloseRequested(ViewerSession session) => CloseSession(session);

        private void StreamTab_OnFocusRequested(ViewerSession session)
        {
            SetActiveSession(session);
            SetFocusedSession(_focusedSession == session ? null : session);
        }

        /// <summary>
        /// Deixa só uma live na tela sem desconectar as outras — elas seguem recebendo vídeo
        /// e voltam quando o foco é desligado.
        /// </summary>
        private void SetFocusedSession(ViewerSession session)
        {
            _focusedSession = session;

            if (_gridView != null)
            {
                _gridView.Filter = _focusedSession == null
                    ? (Predicate<object>)null
                    : (o => ReferenceEquals(o, _focusedSession));
                _gridView.Refresh();
            }

            UpdateViewerLayout();
            UpdatePlayerControlsState();
        }

        private void ToggleFocus_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingToggles) return;
            SetFocusedSession(ToggleFocus.IsChecked == true ? ActiveSession : null);
        }

        private void ToggleStats_Changed(object sender, RoutedEventArgs e)
        {
            ShowStats = ToggleStats.IsChecked == true;
        }

        private void BtnMute_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingToggles || ActiveSession == null) return;

            bool wantMuted = BtnMute.IsChecked == true;
            if (ActiveSession.IsMuted != wantMuted) ActiveSession.ToggleMute();
        }

        private void StreamTab_OnActivated(ViewerSession session) => SetActiveSession(session);

        private void TabSessions_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (TabSessions.SelectedItem is ViewerSession session) SetActiveSession(session);
        }

        private void ToggleTabsView_Changed(object sender, RoutedEventArgs e)
        {
            bool tabsMode = ToggleTabsView.IsChecked == true;
            ToggleTabsView.ToolTip = tabsMode ? "Ver em grade" : "Ver em abas";
            TabSessions.Visibility = tabsMode ? Visibility.Visible : Visibility.Collapsed;
            GridSessions.Visibility = tabsMode ? Visibility.Collapsed : Visibility.Visible;

            if (tabsMode)
            {
                if (_focusedSession != null) SetFocusedSession(null);
                if (ActiveSession != null) TabSessions.SelectedItem = ActiveSession;
            }

            UpdatePlayerControlsState();
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

        private void BtnPip_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingToggles) return;

            if (BtnPip.IsChecked == true)
            {
                if (ActiveSession?.VideoBitmap == null)
                {
                    SyncToggle(BtnPip, false);
                    return;
                }

                _activePip = new PipWindow(ActiveSession.VideoBitmap, v => ActiveSession?.SetVolumeFromPip(v));
                _activePip.OnRestoreRequested += () => {
                    System.Windows.Application.Current.Dispatcher.Invoke(() => {
                        WindowState = WindowState.Normal;
                        Activate();
                        _activePip?.Close();
                    });
                };
                _activePip.Closed += (s, ev) => {
                    _activePip = null;
                    SyncToggle(BtnPip, false);
                };
                _activePip.Show();
                WindowState = WindowState.Minimized;
            }
            else
            {
                _activePip?.Close();
                _activePip = null;
                WindowState = WindowState.Normal;
                Activate();
            }
        }

        private void BtnFullscreen_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingToggles) return;

            if (BtnFullscreen.IsChecked == true) EnterFullscreen();
            else ExitFullscreen();
        }

        private void BtnTheater_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingToggles) return;

            if (BtnTheater.IsChecked == true) EnterTheaterMode();
            else ExitFullscreen();
        }

        /// <summary>Ajusta o visual do toggle sem disparar o handler correspondente.</summary>
        private void SyncToggle(System.Windows.Controls.Primitives.ToggleButton toggle, bool isChecked)
        {
            _syncingToggles = true;
            toggle.IsChecked = isChecked;
            _syncingToggles = false;
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
            SyncToggle(BtnFullscreen, true);
            SyncToggle(BtnTheater, false);
        }

        private void EnterTheaterMode()
        {
            WindowStyle = WindowStyle.None;
            TitleBarGrid.Visibility = Visibility.Collapsed;
            TopPanel.Visibility = Visibility.Collapsed;
            OverlayGrid.Visibility = Visibility.Visible;
            ApplyImmersiveMargins(true);
            SyncToggle(BtnTheater, true);
            SyncToggle(BtnFullscreen, false);
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
            SyncToggle(BtnTheater, false);
            SyncToggle(BtnFullscreen, false);
        }

        private void ApplyImmersiveMargins(bool immersive)
        {
            ViewerArea.Margin = immersive ? new Thickness(0) : new Thickness(15, 0, 15, 15);
            VideoControlsBar.Margin = immersive ? new Thickness(0) : new Thickness(15, 0, 15, 15);
            VideoControlsGradient.CornerRadius = immersive ? new CornerRadius(0) : new CornerRadius(0, 0, 12, 12);
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

            if (ViewerArea.IsMouseOver || VideoControlsButtons.IsMouseOver)
            {
                ShowVideoControls();
            }
        }

        private void MouseIdleTimer_Tick(object sender, EventArgs e)
        {
            _mouseIdleTimer.Stop();

            // Enquanto o mouse estiver na própria barra ela não some.
            if (VideoControlsButtons.IsMouseOver)
            {
                _mouseIdleTimer.Start();
                return;
            }

            HideVideoControls();
            if (WindowStyle == WindowStyle.None) Cursor = System.Windows.Input.Cursors.None;
        }

        private void ShowVideoControls() => FadeVideoControls(1.0, true);

        private void HideVideoControls() => FadeVideoControls(0.0, false);

        private void FadeVideoControls(double target, bool interactive)
        {
            if (Math.Abs(VideoControlsBar.Opacity - target) < 0.01 && VideoControlsBar.IsHitTestVisible == interactive) return;

            VideoControlsBar.IsHitTestVisible = interactive;
            VideoControlsBar.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation
            {
                To = target,
                Duration = TimeSpan.FromMilliseconds(160)
            });
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
            e.Handled = true;
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
