using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace Luma.WindowsApp;

/// <summary>Owned, noninteractive caption HWND above a native video child window.</summary>
internal sealed class NativeCaptionOverlay : IDisposable
{
    readonly Window owner;
    readonly FrameworkElement target;
    readonly TextBlock text = new() { FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    readonly Border panel;
    readonly Popup popup;
    bool requested, disposed;
    DpiScale? lastDpi;
    internal bool IsOpen => popup.IsOpen;
    internal string CaptionText => text.Text;

    public NativeCaptionOverlay(Window owner, FrameworkElement target)
    {
        this.owner = owner; this.target = target;
        panel = new Border { Child = text, Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 6, 12, 6), IsHitTestVisible = false };
        popup = new Popup { Child = panel, PlacementTarget = target, Placement = PlacementMode.Relative, AllowsTransparency = true, StaysOpen = true, IsHitTestVisible = false, Focusable = false };
        popup.Opened += (_, _) => MakeNoninteractive();
        owner.LocationChanged += Reposition; owner.SizeChanged += Reposition;
        owner.Activated += Reposition; owner.Deactivated += Reposition; owner.IsVisibleChanged += Reposition;
        target.SizeChanged += Reposition;
    }

    public void Update(string value, bool enabled)
    {
        if (disposed) return;
        requested = enabled && !string.IsNullOrEmpty(value);
        if (text.Text != value) text.Text = value;
        Arrange();
    }
    void Reposition(object? sender, EventArgs e) => Arrange();
    void Reposition(object? sender, DependencyPropertyChangedEventArgs e) => Arrange();
    void Arrange()
    {
        if (disposed) return;
        bool visible = requested && owner.IsActive && owner.IsVisible && owner.IsEnabled && owner.WindowState != WindowState.Minimized && target.IsVisible && target.ActualWidth > 0 && target.ActualHeight > 0;
        if (!visible) { popup.IsOpen = false; return; }
        var dpi = VisualTreeHelper.GetDpi(target);
        if (lastDpi is DpiScale previous && (previous.DpiScaleX != dpi.DpiScaleX || previous.DpiScaleY != dpi.DpiScaleY)) popup.IsOpen = false;
        lastDpi = dpi;
        text.MaxWidth = Math.Max(1, target.ActualWidth * .85 - 24);
        panel.Measure(new Size(target.ActualWidth, target.ActualHeight));
        double x = Math.Max(0, (target.ActualWidth - panel.DesiredSize.Width) / 2);
        double y = Math.Max(0, target.ActualHeight - panel.DesiredSize.Height - 34);
        // Popup does not automatically follow an unchanged relative offset when
        // its owner moves; reopen above when the owner changed monitor DPI.
        if (popup.HorizontalOffset == x) popup.HorizontalOffset = x + 1;
        popup.HorizontalOffset = x; popup.VerticalOffset = y; popup.IsOpen = true;
    }
    void MakeNoninteractive()
    {
        if (PresentationSource.FromVisual(panel) is not HwndSource source) return;
        nint style = GetWindowLongPtr(source.Handle, -20);
        _ = SetWindowLongPtr(source.Handle, -20, style | 0x20 | 0x80 | 0x08000000); // transparent, toolwindow, noactivate
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; popup.IsOpen = false;
        owner.LocationChanged -= Reposition; owner.SizeChanged -= Reposition;
        owner.Activated -= Reposition; owner.Deactivated -= Reposition; owner.IsVisibleChanged -= Reposition;
        target.SizeChanged -= Reposition; popup.Child = null;
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
}
