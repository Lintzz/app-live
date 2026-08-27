using System.Windows;
using System.Windows.Input;

namespace RadminStreamApp
{
    public partial class AddFriendDialog : Window
    {
        public string FriendName { get; private set; }

        public AddFriendDialog(string ip)
        {
            InitializeComponent();
            TxtIpLabel.Text = $"IP: {ip}";
            Loaded += (s, e) => TxtNickname.Focus();
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void TxtNickname_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Confirm();
            else if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e) => Confirm();

        private void Confirm()
        {
            FriendName = TxtNickname.Text?.Trim();
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
