using System;
using System.Text.Json.Serialization;

namespace RadminStreamApp.Models
{
    public class Friend : System.ComponentModel.INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _ip = string.Empty;
        private bool _isOnline;
        private bool _isStreaming;
        private bool _isWatching;
        private string? _sessionInfo;

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(DisplayName)); }
        }

        public string Ip
        {
            get => _ip;
            set { _ip = value; OnPropertyChanged(nameof(Ip)); OnPropertyChanged(nameof(DisplayName)); }
        }

        [JsonIgnore]
        public bool IsOnline
        {
            get => _isOnline;
            set { _isOnline = value; OnPropertyChanged(nameof(IsOnline)); RaiseStatusChanged(); }
        }

        [JsonIgnore]
        public bool IsStreaming
        {
            get => _isStreaming;
            set { _isStreaming = value; OnPropertyChanged(nameof(IsStreaming)); RaiseStatusChanged(); }
        }

        /// <summary>True enquanto existe uma ViewerSession conectada a este amigo.</summary>
        [JsonIgnore]
        public bool IsWatching
        {
            get => _isWatching;
            set { _isWatching = value; OnPropertyChanged(nameof(IsWatching)); RaiseStatusChanged(); }
        }

        /// <summary>Texto curto com o estado da sessão ativa (ex.: "42ms"), vazio quando não conectado.</summary>
        [JsonIgnore]
        public string? SessionInfo
        {
            get => _sessionInfo;
            set { _sessionInfo = value; OnPropertyChanged(nameof(SessionInfo)); OnPropertyChanged(nameof(SubtitleText)); }
        }

        [JsonIgnore]
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Ip : Name;

        [JsonIgnore]
        public string SubtitleText => string.IsNullOrWhiteSpace(SessionInfo) ? Ip : $"{Ip} · {SessionInfo}";

        [JsonIgnore]
        public string StatusColor => IsOnline ? (IsStreaming ? "#00D26A" : "#FFBB00") : "#4A4A52";

        [JsonIgnore]
        public string StatusTooltip => IsWatching
            ? "Assistindo — clique para sair"
            : IsOnline
                ? (IsStreaming ? "Em live agora — clique para assistir" : "Online, mas sem transmitir")
                : "Offline";

        /// <summary>
        /// Só dá para clicar no card quando há uma live para entrar (ou para sair de uma já
        /// aberta). Antes o clique valia sempre e o app abria uma tentativa de conexão que
        /// nunca ia dar em nada, com um card de "conectando" preso na tela.
        /// </summary>
        [JsonIgnore]
        public bool CanWatch => IsWatching || (IsOnline && IsStreaming);

        /// <summary>Ordenação da sidebar: assistindo → em live → online → offline.</summary>
        [JsonIgnore]
        public int SortRank => IsWatching ? 0 : (IsStreaming ? 1 : (IsOnline ? 2 : 3));

        private void RaiseStatusChanged()
        {
            OnPropertyChanged(nameof(StatusColor));
            OnPropertyChanged(nameof(StatusTooltip));
            OnPropertyChanged(nameof(SortRank));
            OnPropertyChanged(nameof(CanWatch));
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }
}
