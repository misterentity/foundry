using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Foundry.App.ViewModels;

namespace Foundry.App.Views;

public partial class ShellView : UserControl
{
    /// <summary>The chat panel auto-collapses below this width. User can still toggle it back on.</summary>
    private const double NarrowBreakpointPx = 1280;
    private bool _userOverrodeChatVisible;
    private int _lastWidthBucket = -1;   // -1 = not measured yet; 0 = narrow; 1 = wide

    public ShellView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ShellViewModel vm) vm.PropertyChanged += OnVmChanged;
        };
    }

    /// <summary>
    /// Chat composer keyboard shortcut: ⏎ sends, ⇧⏎ inserts a newline. The hint label in XAML
    /// promises this, so the binding has to actually exist (PRD §12 — keyboard parity).
    /// </summary>
    private void ChatComposer_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift) return;  // ⇧⏎ → newline
        if (DataContext is not ShellViewModel vm) return;
        e.Handled = true;
        if (vm.SendCommand.CanExecute(null)) vm.SendCommand.Execute(null);
    }

    /// <summary>
    /// Responsive chat collapse: below 1280px wide the chat hides automatically so the main
    /// panel keeps space for the 3D preview / tables. If the user clicks the CHAT toggle we
    /// stop auto-collapsing for the rest of the session.
    /// </summary>
    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is not ShellViewModel vm) return;
        var bucket = e.NewSize.Width < NarrowBreakpointPx ? 0 : 1;
        if (bucket == _lastWidthBucket) return;
        _lastWidthBucket = bucket;
        if (_userOverrodeChatVisible) return;
        vm.ChatVisible = bucket == 1;
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShellViewModel.ChatVisible)) return;
        // If the VM changes ChatVisible while we're below the breakpoint (or above & it doesn't
        // match what auto-resize would have set), assume the user toggled it intentionally.
        if (DataContext is not ShellViewModel vm) return;
        var expected = ActualWidth >= NarrowBreakpointPx;
        if (vm.ChatVisible != expected) _userOverrodeChatVisible = true;
    }
}
