using System;
using System.Windows;
using System.Windows.Input;

namespace StreamLiveApp
{
    /// <summary>
    /// Serve aos dois lados da senha de sala: o host define uma (opcional) ao iniciar a live,
    /// e o viewer só vê este modal quando o host realmente exige senha.
    /// </summary>
    public partial class RoomPasswordDialog : Window
    {
        private bool _syncing;

        public string Password { get; private set; } = string.Empty;

        private RoomPasswordDialog()
        {
            InitializeComponent();
            Loaded += (s, e) => TxtPassword.Focus();
        }

        /// <summary>Host: define a senha da sala. Em branco = sala aberta.</summary>
        public static RoomPasswordDialog ForHost(string currentPassword)
        {
            var dialog = new RoomPasswordDialog();
            dialog.TxtTitle.Text = "Senha da sala";
            dialog.TxtSubtitle.Text = "Deixe em branco para que qualquer amigo possa entrar.";
            dialog.BtnConfirm.Content = "Iniciar";
            dialog.SetPasswordText(currentPassword ?? string.Empty);
            return dialog;
        }

        /// <summary>Viewer: o host pediu senha.</summary>
        public static RoomPasswordDialog ForViewer(string friendName, bool previousAttemptFailed)
        {
            var dialog = new RoomPasswordDialog();
            dialog.TxtTitle.Text = $"Senha da sala de {friendName}";
            dialog.TxtSubtitle.Text = "Esta live é protegida. Peça a senha para quem está transmitindo.";
            dialog.BtnConfirm.Content = "Entrar";

            if (previousAttemptFailed)
            {
                dialog.TxtError.Text = "Senha incorreta. Tente novamente.";
                dialog.TxtError.Visibility = Visibility.Visible;
            }

            return dialog;
        }

        private void SetPasswordText(string value)
        {
            _syncing = true;
            TxtPassword.Password = value;
            TxtPasswordVisible.Text = value;
            _syncing = false;
        }

        private string CurrentText => BtnReveal.IsChecked == true ? TxtPasswordVisible.Text : TxtPassword.Password;

        private void Password_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;

            _syncing = true;
            if (sender == TxtPassword) TxtPasswordVisible.Text = TxtPassword.Password;
            else TxtPassword.Password = TxtPasswordVisible.Text;
            _syncing = false;

            TxtError.Visibility = Visibility.Collapsed;
        }

        private void BtnReveal_Changed(object sender, RoutedEventArgs e)
        {
            bool reveal = BtnReveal.IsChecked == true;
            TxtPasswordVisible.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
            TxtPassword.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;

            if (reveal)
            {
                TxtPasswordVisible.Focus();
                TxtPasswordVisible.CaretIndex = TxtPasswordVisible.Text.Length;
            }
            else
            {
                TxtPassword.Focus();
            }
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            Password = CurrentText ?? string.Empty;
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
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
            if (e.Key == Key.Enter) BtnConfirm_Click(sender, e);
            else if (e.Key == Key.Escape) BtnCancel_Click(sender, e);
        }
    }
}
