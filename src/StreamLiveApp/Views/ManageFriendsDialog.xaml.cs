using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using StreamLiveApp.Models;
using StreamLiveApp.Services;

namespace StreamLiveApp
{
    public partial class ManageFriendsDialog : Window
    {
        private readonly ObservableCollection<Friend> _friends;

        public event Action<Friend> FriendAdded = delegate {};

        public ManageFriendsDialog(ObservableCollection<Friend> friends, string? suggestedIp = null)
        {
            InitializeComponent();

            _friends = friends;
            LstFriends.ItemsSource = _friends;
            _friends.CollectionChanged += Friends_CollectionChanged;

            if (!string.IsNullOrWhiteSpace(suggestedIp) && !_friends.Any(f => f.Ip == suggestedIp))
            {
                TxtNewIp.Text = suggestedIp;
            }

            UpdateEmptyState();
            Loaded += (s, e) => TxtNewName.Focus();
        }

        private void Friends_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyState();

        private void UpdateEmptyState()
        {
            TxtEmpty.Visibility = _friends.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            string ip = TxtNewIp.Text?.Trim() ?? string.Empty;
            string name = TxtNewName.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(ip))
            {
                System.Windows.MessageBox.Show(this, "Informe o IP do amigo.", "Gerenciar Amigos",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtNewIp.Focus();
                return;
            }

            if (_friends.Any(f => string.Equals(f.Ip, ip, StringComparison.OrdinalIgnoreCase)))
            {
                System.Windows.MessageBox.Show(this, "Esse IP já está na lista.", "Gerenciar Amigos",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var friend = new Friend { Name = string.IsNullOrWhiteSpace(name) ? ip : name, Ip = ip };
            _friends.Add(friend);
            Save();

            TxtNewName.Text = string.Empty;
            TxtNewIp.Text = string.Empty;
            TxtNewName.Focus();

            FriendAdded?.Invoke(friend);
        }

        private void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button && button.DataContext is Friend friend)
            {
                _friends.Remove(friend);
                Save();
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { }
            }
        }

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
            else if (e.Key == Key.Enter && (TxtNewName.IsKeyboardFocused || TxtNewIp.IsKeyboardFocused))
            {
                BtnAdd_Click(sender, e);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void Save()
        {
            FriendsService.SaveFriends(new List<Friend>(_friends));
        }

        protected override void OnClosed(EventArgs e)
        {
            _friends.CollectionChanged -= Friends_CollectionChanged;
            Save();
            base.OnClosed(e);
        }
    }
}
