using System;
using System.Windows;

namespace RadminStreamApp
{
    public partial class StreamTab : System.Windows.Controls.UserControl
    {
        public event Action<ViewerSession> OnCloseRequested = delegate {};
        public event Action<ViewerSession> OnActivated = delegate {};
        public event Action<ViewerSession> OnFullscreenRequested = delegate {};

        public StreamTab()
        {
            InitializeComponent();
        }

        private ViewerSession Session => DataContext as ViewerSession;

        private void Root_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (Session == null) return;

            OnActivated?.Invoke(Session);
            if (e.ClickCount == 2) OnFullscreenRequested?.Invoke(Session);
        }

        private void BtnMute_Click(object sender, RoutedEventArgs e)
        {
            if (Session == null) return;
            OnActivated?.Invoke(Session);
            Session.ToggleMute();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            if (Session != null) OnCloseRequested?.Invoke(Session);
        }
    }
}
