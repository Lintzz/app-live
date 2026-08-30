using System;
using System.Windows;

namespace RadminStreamApp
{
    public partial class StreamTab : System.Windows.Controls.UserControl
    {
        public event Action<ViewerSession> OnCloseRequested = delegate {};
        public event Action<ViewerSession> OnActivated = delegate {};

        /// <summary>Clique na live: alterna entre focar só nela e voltar para a grade.</summary>
        public event Action<ViewerSession> OnFocusRequested = delegate {};

        /// <summary>Botão "Reconectar" do overlay, quando as tentativas automáticas acabaram.</summary>
        public event Action<ViewerSession> OnRetryRequested = delegate {};

        public StreamTab()
        {
            InitializeComponent();
        }

        private ViewerSession? Session => DataContext as ViewerSession;

        private void Root_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var session = Session;
            if (session == null) return;

            // Um clique basta para alternar foco/grade. O segundo clique de um duplo é
            // ignorado, senão ele desfaria na hora o que o primeiro acabou de fazer.
            if (e.ClickCount > 1) return;

            OnActivated?.Invoke(session);
            OnFocusRequested?.Invoke(session);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            var session = Session;
            if (session != null) OnCloseRequested?.Invoke(session);
        }

        private void BtnRetry_Click(object sender, RoutedEventArgs e)
        {
            var session = Session;
            if (session != null) OnRetryRequested?.Invoke(session);
        }
    }
}
