using System;
using System.Windows;

namespace RadminStreamApp
{
    public partial class StreamTab : System.Windows.Controls.UserControl
    {
        public event Action<ViewerSession> OnCloseRequested = delegate {};

        public StreamTab()
        {
            InitializeComponent();
        }

        private ViewerSession Session => DataContext as ViewerSession;

        private void SliderTabVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            Session?.SetVolume((float)(e.NewValue / 100.0));
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            if (Session != null)
            {
                OnCloseRequested?.Invoke(Session);
            }
        }
    }
}
