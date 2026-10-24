using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LocalFileAudit.App.Services;

public partial class ToastNotificationWindow : Window
{
    private readonly DispatcherTimer _timer;

    public ToastNotificationWindow(string title, string message, bool isSuccess)
    {
        InitializeComponent();

        TitleBlock.Text = title;
        MessageBlock.Text = message;

        if (!isSuccess)
        {
            var warningBrush = Application.Current?.TryFindResource("WarningBrush") as Brush
                ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));
            var badgeBg = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#332414"));

            IconBadge.Background = badgeBg;
            IconBadge.BorderBrush = warningBrush;
            IconText.Text = "⚠";
            IconText.Foreground = warningBrush;
        }

        // Position window at bottom right of working area
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 20;
        Top = workArea.Bottom - Height - 20;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(6)
        };
        _timer.Tick += (s, e) =>
        {
            _timer.Stop();
            Close();
        };
        _timer.Start();
    }

    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _timer.Stop();
        try
        {
            if (Application.Current.MainWindow != null)
            {
                if (Application.Current.MainWindow.WindowState == WindowState.Minimized)
                {
                    Application.Current.MainWindow.WindowState = WindowState.Normal;
                }
                Application.Current.MainWindow.Activate();
            }
        }
        catch
        {
            // Best effort
        }
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Close();
    }
}
