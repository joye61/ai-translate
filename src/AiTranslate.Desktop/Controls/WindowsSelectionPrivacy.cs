using AiTranslate.Platform.Windows;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace AiTranslate.Desktop.Controls;

/// <summary>Protect selections made in our text controls without changing their selection or copy commands.</summary>
internal sealed class WindowsSelectionPrivacy : IDisposable
{
    private readonly Action<Window?> _protect;
    private readonly Func<bool> _isInjectedCopy;
    private readonly IDisposable[] _handlers;
    private Window? _window;

    public WindowsSelectionPrivacy(WindowsDesktopPlatformService platform)
        : this(window => platform.SetLocalTextSelection(window?.TryGetPlatformHandle()?.Handle ?? 0), WindowsCopyInput.IsInjectedShortcut) { }

    internal WindowsSelectionPrivacy(Action<Window?> protect, Func<bool> isInjectedCopy)
    {
        _protect = protect;
        _isInjectedCopy = isInjectedCopy;
        _handlers =
        [
            InputElement.PointerPressedEvent.AddClassHandler<Window>(Pressed, RoutingStrategies.Tunnel, handledEventsToo: true),
            InputElement.PointerReleasedEvent.AddClassHandler<Window>(Released, RoutingStrategies.Tunnel, handledEventsToo: true),
            SelectableTextBlock.CopyingToClipboardEvent.AddClassHandler<SelectableTextBlock>(FilterCopy),
            TextBox.CopyingToClipboardEvent.AddClassHandler<TextBox>(FilterCopy)
        ];
    }

    private void Pressed(Window window, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Mouse ||
            e.GetCurrentPoint(window).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;
        EndGesture();
        if (e.Source is not Avalonia.Visual hit ||
            !hit.GetSelfAndVisualAncestors().Any(v => v is SelectableTextBlock or TextBox)) return;
        _window = window;
        window.Deactivated += WindowEnded;
        window.Closed += WindowEnded;
        _protect(window);
    }

    private void Released(Window window, PointerReleasedEventArgs e)
    {
        if (window == _window && e.GetCurrentPoint(window).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonReleased)
            EndGesture();
    }

    private void FilterCopy(object? sender, RoutedEventArgs e)
    {
        if (_isInjectedCopy()) e.Handled = true;
    }

    private void WindowEnded(object? sender, EventArgs e) => EndGesture();

    private void EndGesture()
    {
        if (_window is null) return;
        _window.Deactivated -= WindowEnded;
        _window.Closed -= WindowEnded;
        _window = null;
        _protect(null);
    }

    public void Dispose()
    {
        EndGesture();
        foreach (var handler in _handlers) handler.Dispose();
    }
}
