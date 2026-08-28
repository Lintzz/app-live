using System;
using System.Windows;

namespace RadminStreamApp
{
    public partial class StreamTab : System.Windows.Controls.UserControl
    {
        public event Action<ViewerSession> OnCloseRequested = delegate {};
        public event Action<ViewerSession> OnActivated = delegate {};

        /// <summary>Duplo clique na live: alterna entre focar só nela e voltar para a grade.</summary>
        public event Action<ViewerSession> OnFocusRequested = delegate {};

        public StreamTab()
        {
            InitializeComponent();
        }

        private ViewerSession? Session => DataContext as ViewerSession;

        private void Root_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var session = Session;
            if (session == null) return;

            OnActivated?.Invoke(session);
            if (e.ClickCount == 2) OnFocusRequested?.Invoke(session);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            var session = Session;
            if (session != null) OnCloseRequested?.Invoke(session);
        }
    }
}
