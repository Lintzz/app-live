using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace RadminStreamApp
{
    public partial class PipWindow : Window
    {
        private readonly Action<float> _setVolume;

        public event Action OnRestoreRequested = delegate {};

        public PipWindow(WriteableBitmap bitmap, Action<float> setVolume)
        {
            InitializeComponent();
            _setVolume = setVolume;
            PipVideo.Source = bitmap;

            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - Width - 20;
            Top = workArea.Bottom - Height - 20;
        }

        public void SetBitmap(WriteableBitmap bitmap)
        {
            PipVideo.Source = bitmap;
        }

        private void Grid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                OnRestoreRequested?.Invoke();
            }
            else
            {
                try { DragMove(); } catch { }
            }
        }

        private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            PipControls.Visibility = Visibility.Visible;
        }

        private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            PipControls.Visibility = Visibility.Collapsed;
        }

        private void SliderPipVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _setVolume?.Invoke((float)(e.NewValue / 100.0));
        }
    }
}
