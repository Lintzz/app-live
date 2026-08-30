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
        private SignalingServer? _server;
        private HostBroadcast? _hostBroadcast;
        private WriteableBitmap? _hostBitmap;

        private ObservableCollection<Friend>? _friends;
        private ICollectionView? _friendsView;
        private readonly ObservableCollection<ViewerSession> _sessions = new ObservableCollection<ViewerSession>();
        private ICollectionView? _gridView;
        private ViewerSession? _focusedSession;

        private ViewerSession? _activeSession;
        /// <summary>Live em foco: recebe o volume da barra, o PiP e o destaque na borda.</summary>
        public ViewerSession? ActiveSession
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

        private PipWindow? _activePip;
        private string _lastRoomPassword = string.Empty;
        private string? _downloadUrl;
        private string? _downloadChecksumUrl;

        private readonly System.Windows.Threading.DispatcherTimer _mouseIdleTimer;
        private System.Windows.Threading.DispatcherTimer? _statusTimer;
        private int _statusRefreshRunning;

        // Evita que sincronizar o estado visual dos toggles dispare os handlers de novo.
        private bool _syncingToggles;

        // Ligada enquanto as preferências salvas são aplicadas aos controles, para a carga
        // não disparar uma regravação do arquivo que acabou de ser lido.
        private bool _loadingSettings;
        private bool _isBroadcasting;
        private string _hostVideoStats = string.Empty;
        private string _hostAudioStats = string.Empty;

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
            FriendsService.OnPersistenceError += (message) =>
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    System.Windows.MessageBox.Show(message, "Lista de amigos",
                        MessageBoxButton.OK, MessageBoxImage.Warning));

            SettingsService.OnPersistenceError += (message) =>
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    System.Windows.MessageBox.Show(message, "Configurações",
                        MessageBoxButton.OK, MessageBoxImage.Warning));

            _settings = SettingsService.Load();
            _excludedAudioProcessName = _settings.ExcludedAudioProcessName;
            ApplyLoadedSettings();

            _friends = new ObservableCollection<Friend>(FriendsService.LoadFriends());

            _friendsView = CollectionViewSource.GetDefaultView(_friends);
            _friendsView.SortDescriptions.Add(new SortDescription(nameof(Friend.SortRank), ListSortDirection.Ascending));
            _friendsView.SortDescriptions.Add(new SortDescription(nameof(Friend.Name), ListSortDirection.Ascending));

            LstFriendsSidebar.ItemsSource = _friendsView;

            // View própria: filtrar o foco aqui não afeta a lista de abas.
            _gridView = new CollectionViewSource { Source = _sessions }.View;
            GridSessions.ItemsSource = _gridView;

            _friends.CollectionChanged += (s, ev) => { UpdateSidebarEmptyStates(); SyncAllowedIps(); };
            UpdateSidebarEmptyStates();

            VersionText.Text = "Versão " + AppInfo.Version;
            LoadAudioExclusionOptions();

            var updateResult = await UpdateManager.CheckForUpdatesAsync();
            if (updateResult.HasUpdate)
            {
                _downloadUrl = updateResult.DownloadUrl;
                _downloadChecksumUrl = updateResult.ChecksumUrl;
                UpdateBannerText.Text = $"Uma nova versão ({updateResult.LatestVersion}) está disponível!";
                UpdateBanner.Visibility = Visibility.Visible;
            }

            // O servidor precisa estar no ar antes do primeiro teste de status: senao um amigo
            // apontando para esta maquina (ou o loopback) aparece offline ate o proximo ciclo.
            StartSignalingServer();
            StartStatusTimer();
            UpdatePlayerControlsState();
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
            _server.RestrictToAllowedIps = ChkFriendsOnly?.IsChecked != false;
            _server.OnMessageReceived += Server_OnMessageReceived;
            _server.OnConnectionRejected += (ip) =>
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    ShowTransientStatus($"Conexão recusada: {ip} não está na sua lista de amigos."));
            };
            _server.OnViewerCongested += (count) =>
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => ShowCongestion(count));
            };
            _server.OnClientConnected += (socket) => {
                System.Windows.Application.Current.Dispatcher.Invoke(UpdateViewerCount);
            };
            _server.OnClientDisconnected += (socket) => {
                _hostBroadcast?.RemoveClient(socket.ConnectionInfo.Id.ToString());
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

            _hostBroadcast = new HostBroadcast(_server);
            // InvokeAsync, e não Invoke: este aviso nasce na thread de captura, e um Invoke
            // bloqueante trava as duas se a thread de UI já estiver esperando a captura parar.
            _hostBroadcast.AudioCaptureError += (error) =>
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    System.Windows.MessageBox.Show(error, "Aviso - Captura de Áudio",
                        MessageBoxButton.OK, MessageBoxImage.Warning));

            _hostBroadcast.BinaryAudioReady += (data) => _server?.BroadcastBinary(data);

            _hostBroadcast.FrameReady += (pixels, width, height) =>
            {
                UpdateHostBitmap(pixels, width, height);
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    StatusText.Visibility = Visibility.Collapsed);
            };

            _hostBroadcast.StatsUpdated += (fps, kbps) =>
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    _hostVideoStats = $"📤 {fps}fps | {kbps:F1} kbps";
                    StatsOverlay.Visibility = Visibility.Visible;
                    StatsText.Text = _hostVideoStats + _hostAudioStats;
                });

            // Sem viewer conectado nao ha audio a enviar, entao o contador so diz algo
            // quando alguem esta assistindo.
            _hostBroadcast.AudioStatsUpdated += (frames) =>
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    _hostAudioStats = _server?.ConnectedClientsCount > 0
                        ? $" | 🔊 {frames}/s"
                        : string.Empty;
                    StatsText.Text = _hostVideoStats + _hostAudioStats;
                });

            SyncAllowedIps();
            UpdateViewerCount();
        }

        /// <summary>Repassa a lista de amigos ao servidor: só esses IPs conseguem conectar.</summary>
        private void SyncAllowedIps()
        {
            if (_server == null || _friends == null) return;
            _server.SetAllowedIps(_friends.Select(f => f.Ip));
        }

        private void ChkFriendsOnly_Changed(object sender, RoutedEventArgs e)
        {
            if (ChkFriendsOnly == null) return;
            if (_server != null) _server.RestrictToAllowedIps = ChkFriendsOnly.IsChecked == true;
            PersistSettings();
        }

        /// <summary>Aviso curto na barra de status, sem roubar o foco com um MessageBox.</summary>
        private void ShowTransientStatus(string message)
        {
            StatusText.Text = message;
            StatusText.Visibility = Visibility.Visible;
        }

        // ───────────────────────────── Status dos amigos ─────────────────────────────

        /// <summary>De quanto em quanto tempo a bolinha de status de cada amigo é revalidada.</summary>
        private static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(5);

        private void StartStatusTimer()
        {
            _statusTimer = new System.Windows.Threading.DispatcherTimer();
            _statusTimer.Interval = StatusPollInterval;
            _statusTimer.Tick += async (s, ev) => await RefreshAllFriendsStatusAsync();
            _statusTimer.Start();

            _ = RefreshAllFriendsStatusAsync();
        }

        private async System.Threading.Tasks.Task RefreshAllFriendsStatusAsync()
        {
            // Um ciclo de cada vez: um amigo offline consome o timeout inteiro, e sem esta
            // trava os ciclos se empilhariam no intervalo curto de atualização.
            if (System.Threading.Interlocked.Exchange(ref _statusRefreshRunning, 1) != 0) return;

            try
            {
                // Em paralelo: em série, uma lista de 8+ amigos offline levaria mais tempo
                // que o próprio intervalo entre atualizações.
                await System.Threading.Tasks.Task.WhenAll(
                    _friends!.ToList().Select(CheckFriendStatusAsync));
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _statusRefreshRunning, 0);
            }

            _friendsView?.Refresh();
            UpdateSidebarEmptyStates();
        }

        private static async System.Threading.Tasks.Task CheckFriendStatusAsync(Friend friend)
        {
            var status = await Services.FriendStatusService.CheckAsync(friend.Ip);
            friend.IsOnline = status.IsOnline;
            friend.IsStreaming = status.IsStreaming;
        }

        private void UpdateSidebarEmptyStates()
        {
            bool hasFriends = _friends != null && _friends.Count > 0;
            SidebarEmptyState.Visibility = hasFriends ? Visibility.Collapsed : Visibility.Visible;

            bool anyOnline = hasFriends && _friends!.Any(f => f.IsOnline);
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
            if (_isBroadcasting && CboWindows.SelectedItem is CaptureSource selectedSource)
            {
                _hostBroadcast?.ChangeSource(selectedSource);
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
            if (_hostBroadcast != null)
                await _hostBroadcast.HandleSignalingAsync(socket.ConnectionInfo.Id.ToString(), message);
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

        // ─────────────────────── Viewer com a conexão ruim (lado host) ───────────────────────

        private System.Windows.Threading.DispatcherTimer? _congestionTimer;
        private DateTime _lastCongestionUtc = DateTime.MinValue;
        private int _congestedViewers;

        /// <summary>
        /// Troca a contagem de viewers por um aviso enquanto alguém não estiver dando conta de
        /// receber. Sem isto, o host só via "o áudio sumiu para o fulano" e nada dizia que o
        /// gargalo era a rede do outro lado — parecia bug do app.
        /// </summary>
        private void ShowCongestion(int count)
        {
            _congestedViewers = count;
            _lastCongestionUtc = DateTime.UtcNow;

            if (_congestionTimer == null)
            {
                _congestionTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(2)
                };
                _congestionTimer.Tick += (_, _) =>
                {
                    if (DateTime.UtcNow - _lastCongestionUtc < TimeSpan.FromSeconds(4))
                    {
                        RenderCongestion();
                    }
                    else
                    {
                        _congestionTimer!.Stop();
                        UpdateViewerCount();
                    }
                };
            }

            if (!_congestionTimer.IsEnabled) _congestionTimer.Start();
            RenderCongestion();
        }

        private void RenderCongestion()
        {
            ViewerCountText.Text = _congestedViewers == 1
                ? "1 com conexão ruim"
                : $"{_congestedViewers} com conexão ruim";

            ViewerCountPanel.ToolTip =
                "A rede de quem está assistindo não está dando conta. O áudio dessas pessoas está " +
                "sendo descartado para a live não travar para as outras — não é problema da sua conexão.";
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

            if (_hostBroadcast == null) return;

            await _hostBroadcast.StartAsync(new BroadcastSettings
            {
                Source = selectedSource,
                RoomPassword = _lastRoomPassword,
                MaxPerformance = ChkMaxPerformance?.IsChecked == true,
                ForceGdiCapture = ChkForceGdiCapture?.IsChecked == true,
                ExcludedAudioProcessId = ResolveExcludedAudioPid()
            });
        }

        private async void BtnStopStream_Click(object sender, RoutedEventArgs e)
        {
            // A janela volta ao estado "parado" na hora; a desmontagem vem logo atrás, fora da
            // thread de UI. O botão de transmitir fica desabilitado nesse intervalo para não
            // subir uma live nova por cima da que ainda está sendo encerrada.
            _isBroadcasting = false;
            BtnStartStream.Visibility = Visibility.Visible;
            BtnStartStream.IsEnabled = false;
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

            if (_hostBroadcast != null)
            {
                await _hostBroadcast.StopAsync();
            }

            BtnStartStream.IsEnabled = true;
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
                if (_hostBitmap == null || _hostBitmap.PixelWidth != width || _hostBitmap.PixelHeight != height || _hostBitmap.Format != PixelFormats.Bgr32)
                {
                    _hostBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
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

            // Sem live no ar não há o que assistir. O card já fica inerte nesse estado; esta
            // guarda cobre o caminho por código (e a corrida com a checagem de 30s).
            if (!friend.IsOnline || !friend.IsStreaming)
            {
                await CheckFriendStatusAsync(friend);
                _friendsView?.Refresh();

                if (!friend.IsOnline || !friend.IsStreaming)
                {
                    ShowTransientStatus(friend.IsOnline
                        ? $"{friend.DisplayName} está online, mas não está transmitindo."
                        : $"{friend.DisplayName} está offline.");
                    return;
                }
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
            // Dispose, e não só Disconnect: a sessão carrega o timer do vigia de vídeo parado,
            // que sem isto continuaria rodando depois de a célula sair da grade.
            session.Dispose();
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

        private void SetActiveSession(ViewerSession? session)
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
                if (ActiveSession.VideoBitmap != null) _activePip?.SetBitmap(ActiveSession.VideoBitmap);
            }

            UpdatePlayerControlsState();
        }

        /// <summary>Mantém volume, mudo e foco da barra apontando para a live ativa.</summary>
        private void UpdatePlayerControlsState()
        {
            bool hasSession = ActiveSession != null;
            VolumeControls.Visibility = hasSession ? Visibility.Visible : Visibility.Collapsed;

            // Com uma live só, focar não muda nada na tela: o botão só aparece a partir de duas.
            ToggleFocus.Visibility = (hasSession && _sessions.Count > 1) ? Visibility.Visible : Visibility.Collapsed;

            _syncingToggles = true;
            BtnMute.IsChecked = hasSession && ActiveSession!.IsMuted;
            ToggleFocus.IsChecked = hasSession && _focusedSession == ActiveSession;
            _syncingToggles = false;
        }

        private void ActiveSession_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewerSession.IsMuted))
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(UpdatePlayerControlsState);
                return;
            }

            if (e.PropertyName == nameof(ViewerSession.VideoBitmap) && _activePip != null)
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                        var bitmap = ActiveSession?.VideoBitmap;
                        if (bitmap != null) _activePip?.SetBitmap(bitmap);
                    });
            }
        }

        private void Sessions_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // Abrir ou fechar uma live devolve a grade: com o foco preso em uma delas, a nova
            // entraria filtrada e simplesmente nao apareceria.
            if (_focusedSession != null) SetFocusedSession(null);

            UpdateViewerLayout();
            _friendsView?.Refresh();
        }

        private void UpdateViewerLayout()
        {
            int count = _sessions.Count;

            // Limpar so o campo nao basta: o filtro do CollectionView fecha sobre ele e, com
            // ele nulo, passaria a esconder tudo -- a grade ficava vazia depois de fechar a
            // live que estava em foco.
            if (_focusedSession != null && !_sessions.Contains(_focusedSession))
            {
                _focusedSession = null;
                if (_gridView != null)
                {
                    _gridView.Filter = null;
                    _gridView.Refresh();
                }
            }

            int visible = _focusedSession != null ? 1 : count;
            GridColumns = visible <= 1 ? 1 : (visible == 2 ? 2 : (visible <= 4 ? 2 : 3));
            ViewerEmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (count == 0)
            {
                ViewerEmptyText.Text = "Clique em um amigo à esquerda para assistir";
            }

            // Com uma live só a sidebar sai da frente e volta quando o mouse encosta na borda esquerda.
            // Sem lives a lista fica aberta; com live ela recolhe e a aba a traz de volta,
            // empurrando o vídeo em vez de cobrir.
            SetSidebarOpen(count == 0);

            // Sem nenhuma live o painel de cima e a unica coisa util na tela: a abinha some e o
            // painel volta a aparecer. Sem isso, quem escondesse o painel e fechasse a ultima
            // live ficava sem caminho de volta.
            bool hasSessions = count > 0;
            TopPanelHandle.Visibility = hasSessions ? Visibility.Visible : Visibility.Collapsed;
            if (hasSessions) SyncFloatingHandle(TopPanelHandle);
            else SetTopPanelOpen(true);
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
            SidebarColumn.Width = open ? new GridLength(230) : new GridLength(0);
            SidebarHandleArrow.Text = open ? "\uE76B" : "\uE76C";
            SidebarHandle.ToolTip = open ? "Esconder amigos" : "Mostrar amigos";
        }

        /// <summary>
        /// Painel de cima (tela, transmitir, contador) aberto. Guardado à parte de
        /// <c>TopPanel.Visibility</c> porque teatro e tela cheia escondem o painel por conta
        /// própria: sem esta lembrança, sair do modo imersivo devolveria o painel a quem já
        /// tinha pedido para escondê-lo.
        /// </summary>
        private bool _topPanelOpen = true;

        private void TopPanelHandle_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Sem marcar como tratado, o clique sobe para a janela e vira DragMove.
            e.Handled = true;
            SetTopPanelOpen(!_topPanelOpen);
        }

        private void SetTopPanelOpen(bool open)
        {
            _topPanelOpen = open;
            TopPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            TopPanelHandleArrow.Text = open ? "\uE70E" : "\uE70D";
            TopPanelHandle.ToolTip = open ? "Esconder controles de transmissão" : "Mostrar controles de transmissão";
        }

        /// <summary>Em teatro e tela cheia nada além do vídeo fica na tela.</summary>
        private void SetSidebarChromeVisible(bool visible)
        {
            SidebarHandle.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible) SyncFloatingHandle(SidebarHandle);
            if (!visible)
            {
                TopPanelHandle.Visibility = Visibility.Collapsed;
                SidebarPanel.Visibility = Visibility.Collapsed;
                SidebarColumn.Width = new GridLength(0);
            }
            else
            {
                // Devolve sidebar, abinha do painel de cima e faixa de abas conforme o número
                // de lives; voltar tudo direto acendia a faixa vazia com zero lives.
                UpdateViewerLayout();
            }
        }

        private void StreamTab_OnCloseRequested(ViewerSession session) => CloseSession(session);

        /// <summary>
        /// Botão "Reconectar" da célula. Existe porque, esgotadas as tentativas automáticas, a
        /// sessão morta continuava ocupando a grade sem nenhum caminho de volta além de fechar
        /// pelo X e reabrir a live pela lista de amigos.
        /// </summary>
        private async void StreamTab_OnRetryRequested(ViewerSession session)
        {
            if (session == null) return;

            try
            {
                await session.RetryAsync();
            }
            catch (Exception ex)
            {
                ShowTransientStatus($"Não deu para reconectar em {session.FriendName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Clique numa live: ela vira a ativa e, se houver outras na grade, entra em foco
        /// total. O clique seguinte devolve a grade. Com uma live só não há o que alternar.
        /// </summary>
        private void StreamTab_OnFocusRequested(ViewerSession session)
        {
            SetActiveSession(session);
            if (_sessions.Count <= 1) return;
            SetFocusedSession(_focusedSession == session ? null : session);
        }

        /// <summary>
        /// Deixa só uma live na tela sem desconectar as outras — elas seguem recebendo vídeo
        /// e voltam quando o foco é desligado.
        /// </summary>
        private void SetFocusedSession(ViewerSession? session)
        {
            _focusedSession = session;

            if (_gridView != null)
            {
                _gridView.Filter = _focusedSession == null
                    ? (Predicate<object>?)null
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

        private void BtnManageFriends_Click(object sender, RoutedEventArgs e)
        {
            if (_friends == null) return;

            var dialog = new ManageFriendsDialog(_friends) { Owner = this };
            dialog.FriendAdded += (friend) => { _ = CheckFriendStatusAsync(friend); };
            dialog.ShowDialog();

            SyncAllowedIps();
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
            SetSidebarChromeVisible(false);
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
            SetSidebarChromeVisible(false);
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
            SetTopPanelOpen(_topPanelOpen);
            OverlayGrid.Visibility = Visibility.Collapsed;
            ApplyImmersiveMargins(false);
            SetSidebarChromeVisible(true);
            SyncToggle(BtnTheater, false);
            SyncToggle(BtnFullscreen, false);
        }

        private void ApplyImmersiveMargins(bool immersive)
        {
            ViewerArea.Margin = immersive ? new Thickness(0) : new Thickness(15, 0, 15, 15);
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

            if (ViewerArea.IsMouseOver || VideoControlsButtons.IsMouseOver || SidebarHandle.IsMouseOver || TopPanelHandle.IsMouseOver)
            {
                ShowVideoControls();
            }
        }

        private void MouseIdleTimer_Tick(object? sender, EventArgs e)
        {
            _mouseIdleTimer.Stop();

            // Enquanto o mouse estiver na barra ou num dos botões flutuantes, nada some.
            if (VideoControlsButtons.IsMouseOver || SidebarHandle.IsMouseOver || TopPanelHandle.IsMouseOver)
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

            var fade = new System.Windows.Media.Animation.DoubleAnimation
            {
                To = target,
                Duration = TimeSpan.FromMilliseconds(160)
            };

            VideoControlsBar.IsHitTestVisible = interactive;
            VideoControlsBar.BeginAnimation(OpacityProperty, fade);

            // Os botões de amigos e do painel de cima vivem sobre o vídeo e aparecem junto com o resto.
            if (SidebarHandle.Visibility == Visibility.Visible)
            {
                SidebarHandle.IsHitTestVisible = interactive;
                SidebarHandle.BeginAnimation(OpacityProperty, fade);
            }

            if (TopPanelHandle.Visibility == Visibility.Visible)
            {
                TopPanelHandle.IsHitTestVisible = interactive;
                TopPanelHandle.BeginAnimation(OpacityProperty, fade);
            }
        }

        /// <summary>
        /// Alinha um botão flutuante ao estado atual da barra do player.
        ///
        /// Os dois botões nascem com <c>Opacity="0"</c> e só acendem dentro do fade, mas o fade
        /// desiste na primeira linha quando a barra já está no estado pedido. Quem abrisse uma
        /// live com o mouse sobre a janela — os controles já acesos — ganhava a abinha em
        /// <c>Visible</c> e opacidade zero: ela só aparecia depois de o mouse parar, tudo sumir
        /// e voltar. Aqui a opacidade é copiada da barra na hora em que o botão entra na tela.
        /// </summary>
        private void SyncFloatingHandle(UIElement handle)
        {
            // Sem soltar a animação, o valor animado tem precedência e o Opacity abaixo não pega.
            handle.BeginAnimation(OpacityProperty, null);
            handle.Opacity = VideoControlsBar.Opacity;
            handle.IsHitTestVisible = VideoControlsBar.IsHitTestVisible;
        }

        private void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_downloadUrl))
            {
                BtnUpdate.Content = "Baixando...";
                BtnUpdate.IsEnabled = false;
                BtnDismissUpdate.IsEnabled = false;
                _ = UpdateManager.DownloadAndInstallUpdateAsync(_downloadUrl, _downloadChecksumUrl);
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
        {
            // Lido na abertura: o caminho só se define depois que a captura sobe, e pode
            // trocar sozinho no meio da transmissão.
            UpdateCaptureModeText();
            SettingsModalOverlay.Visibility = Visibility.Visible;
        }

        private void BtnCloseSettingsModal_Click(object sender, RoutedEventArgs e)
            => SettingsModalOverlay.Visibility = Visibility.Collapsed;

        private void ChkMaxPerformance_Changed(object sender, RoutedEventArgs e)
        {
            if (ChkMaxPerformance == null) return;

            ApplyLightweightMode(ChkMaxPerformance.IsChecked == true);
            PersistSettings();
        }

        /// <summary>
        /// Efeito do modo leve, separado do handler porque também precisa rodar na carga das
        /// preferências: marcar um CheckBox no valor em que ele já está não dispara evento
        /// nenhum, então confiar no handler deixaria a preferência salva sem aplicar.
        /// </summary>
        private void ApplyLightweightMode(bool lightweight)
        {
            try
            {
                // BelowNormal é intencional: o modo leve cede CPU aos outros programas (o
                // jogo que você está transmitindo, principalmente). O rótulo antigo dizia
                // "Desempenho Máximo" e prometia o contrário do que a opção faz.
                Process.GetCurrentProcess().PriorityClass = lightweight
                    ? ProcessPriorityClass.BelowNormal
                    : ProcessPriorityClass.Normal;
            }
            catch { }

            _hostBroadcast?.ApplyMaxPerformance(lightweight);
        }

        /// <summary>
        /// Reflete nos controles o que foi lido do settings.json. Os handlers disparam ao
        /// marcar cada caixa — é assim que a prioridade do processo e a captura GDI passam a
        /// valer —, e a trava evita que essa carga regrave o arquivo que acabou de ser lido.
        /// </summary>
        private void ApplyLoadedSettings()
        {
            _loadingSettings = true;
            try
            {
                ChkMaxPerformance.IsChecked = _settings.LightweightMode;
                ChkFriendsOnly.IsChecked = _settings.RestrictToFriends;
                ChkForceGdiCapture.IsChecked = _settings.ForceGdiCapture;
            }
            finally
            {
                _loadingSettings = false;
            }

            // O servidor ainda não existe aqui (sobe logo abaixo, já lendo ChkFriendsOnly) e o
            // GDI é lido ao iniciar a transmissão. Só a prioridade precisa ser aplicada agora.
            ApplyLightweightMode(_settings.LightweightMode);
        }

        /// <summary>Grava o estado atual dos controles de configuração.</summary>
        private void PersistSettings()
        {
            if (_loadingSettings) return;

            _settings.LightweightMode = ChkMaxPerformance?.IsChecked == true;
            _settings.RestrictToFriends = ChkFriendsOnly?.IsChecked != false;
            _settings.ForceGdiCapture = ChkForceGdiCapture?.IsChecked == true;
            _settings.ExcludedAudioProcessName = _excludedAudioProcessName;

            SettingsService.Save(_settings);
        }

        // ──────────────────── Exclusão de áudio por processo ────────────────────

        private Services.AppSettings _settings = new();

        // Vem do settings.json e, na primeira execução, do padrão de fábrica (Discord). Começar
        // vazio fazia toda abertura do app voltar a "capturar todo o áudio", e aí a mesa inteira
        // se escutava sem ninguém entender por quê.
        private string _excludedAudioProcessName = Services.AppSettings.DefaultExcludedAudioProcessName;

        private void LoadAudioExclusionOptions()
        {
            var options = Services.AudioExclusionService.ListOptions(_excludedAudioProcessName);

            CboAudioExclusion.ItemsSource = options;
            CboAudioExclusion.SelectedItem = options.FirstOrDefault(o =>
                string.Equals(o.Name, _excludedAudioProcessName, StringComparison.OrdinalIgnoreCase)) ?? options[0];

            UpdateAudioExclusionWarning();
        }

        private void CboAudioExclusion_DropDownOpened(object sender, EventArgs e) => LoadAudioExclusionOptions();

        private void CboAudioExclusion_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (CboAudioExclusion.SelectedItem is not Services.AudioExclusionOption option) return;

            _excludedAudioProcessName = option.Name;
            UpdateAudioExclusionWarning();

            PersistSettings();

            // Vale já na transmissão em andamento.
            _hostBroadcast?.ApplyExcludedAudioProcess(ResolveExcludedAudioPid());
        }

        private uint ResolveExcludedAudioPid()
            => Services.AudioExclusionService.ResolvePid(_excludedAudioProcessName);

        /// <summary>Avisa quando o programa escolhido não está aberto — nesse caso nada é excluído.</summary>
        private void UpdateAudioExclusionWarning()
        {
            if (AudioExclusionWarning == null) return;

            if (!string.IsNullOrEmpty(_excludedAudioProcessName) && ResolveExcludedAudioPid() == 0)
            {
                AudioExclusionWarning.Text =
                    $"\"{_excludedAudioProcessName}\" não está em execução — enquanto isso, todo o áudio do sistema será transmitido.";
                AudioExclusionWarning.Visibility = Visibility.Visible;
            }
            else
            {
                AudioExclusionWarning.Visibility = Visibility.Collapsed;
            }
        }

        private void ChkForceGdiCapture_Changed(object sender, RoutedEventArgs e)
        {
            _hostBroadcast?.ApplyForceGdiCapture(ChkForceGdiCapture?.IsChecked == true);
            PersistSettings();
            UpdateCaptureModeText();
        }

        /// <summary>
        /// Mostra qual caminho de captura está valendo. O DXGI cai sozinho para o GDI em
        /// máquinas onde a duplicação não funciona (RDP, GPU híbrida, driver antigo), e sem
        /// isso na tela o usuário só percebia pela queda de desempenho, sem saber a causa.
        /// </summary>
        private void UpdateCaptureModeText()
        {
            if (CaptureModeText == null) return;

            var mode = _hostBroadcast?.ActiveCaptureMode ?? "—";
            CaptureModeText.Text = mode switch
            {
                "DXGI" => "Modo de captura: DXGI (Desktop Duplication) — o caminho rápido.",
                "GDI" => "Modo de captura: GDI — mais pesado; é a reserva usada quando a duplicação não está disponível.",
                _ => "Modo de captura: — (definido quando a transmissão começa)"
            };
        }

        private void GithubLink_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = AppInfo.RepositoryUrl,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        protected override void OnClosed(EventArgs e)
        {
            _server?.Stop();
            _hostBroadcast?.Dispose();
            foreach (var session in _sessions.ToList())
            {
                session.Dispose();
            }
            base.OnClosed(e);

            ForceExit();
        }

        /// <summary>
        /// Encerra o processo sem passar pelo desligamento normal do Windows.
        ///
        /// A captura de áudio por processo deixa uma thread presa para sempre dentro da
        /// ApplicationLoopback.dll — o StartCaptureAsync nunca desenrola, nem depois do
        /// StopCaptureAsync (ver <see cref="ProcessAudioCapturer"/>). É por isso que o
        /// encerramento precisa ser forçado: sem isso o processo fica vivo em segundo plano
        /// depois de a janela fechar.
        ///
        /// O <c>Environment.Exit</c> que fazia esse papel termina em ExitProcess, que chama
        /// o DllMain de descarregamento de cada DLL carregada — inclusive o da
        /// ApplicationLoopback, com a thread dela ainda lá dentro. Daí saía o
        /// <c>SEHException</c> ao fechar com a live ligada, que o handler do App capturava e
        /// transformava num aviso de erro bem na hora de sair.
        ///
        /// O TerminateProcess não chama DllMain nenhum: derruba o processo direto. Medido:
        /// o mesmo teardown termina em 0xC000000D com Environment.Exit e em 0 com este.
        /// Nada se perde — amigos e configurações são gravados de forma síncrona bem antes
        /// daqui.
        /// </summary>
        private static void ForceExit()
        {
            try
            {
                TerminateProcess(GetCurrentProcess(), 0);
            }
            catch
            {
                // Se o P/Invoke falhar, cair no Exit ainda é melhor que deixar o processo vivo.
            }

            Environment.Exit(0);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
