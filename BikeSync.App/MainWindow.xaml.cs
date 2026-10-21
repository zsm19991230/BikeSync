using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using BikeSync.App.ViewModels;
using Wpf.Ui.Controls;

namespace BikeSync.App;

public partial class MainWindow : FluentWindow
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmBorderColor = 34;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        var workArea = SystemParameters.WorkArea;
        var targetWidth = Math.Min(1360, Math.Max(1120, workArea.Width - 80));
        var targetHeight = Math.Min(880, Math.Max(720, workArea.Height - 60));
        Width = targetWidth;
        Height = targetHeight;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        // Keep the native caption and border in the same palette as the Fluent
        // content. Without this, Windows may paint a light caption above a dark app.
        var dark = 1;
        _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
        var caption = 0x00160F0A; // COLORREF (BGR) for #0A0F16
        _ = DwmSetWindowAttribute(handle, DwmCaptionColor, ref caption, sizeof(int));
        var border = 0x00160F0A;
        _ = DwmSetWindowAttribute(handle, DwmBorderColor, ref border, sizeof(int));
        var text = 0x00FBF6F2; // COLORREF (BGR) for #F2F6FB
        _ = DwmSetWindowAttribute(handle, DwmTextColor, ref text, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int valueSize);
}
