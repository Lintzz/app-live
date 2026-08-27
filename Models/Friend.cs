using System;

namespace RadminStreamApp.Models
{
    public class Friend : System.ComponentModel.INotifyPropertyChanged
    {
        private string _name;
        private string _ip;
        private bool _isOnline;
        private bool _isStreaming;

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(nameof(Name)); }
        }

        public string Ip
        {
            get => _ip;
            set { _ip = value; OnPropertyChanged(nameof(Ip)); }
        }

        public bool IsOnline
        {
            get => _isOnline;
            set { _isOnline = value; OnPropertyChanged(nameof(IsOnline)); OnPropertyChanged(nameof(StatusColor)); }
        }

        public bool IsStreaming
        {
            get => _isStreaming;
            set { _isStreaming = value; OnPropertyChanged(nameof(IsStreaming)); OnPropertyChanged(nameof(StatusColor)); }
        }

        public string StatusColor => IsOnline ? (IsStreaming ? "#00FF00" : "#FFBB00") : "#555555";

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }
}
