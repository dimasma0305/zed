using Avalonia.Controls;
using Avalonia.Input;
using Zyber.Models;

namespace Zyber.Views;

public sealed partial class DiagnosticsView : UserControl
{
    public event EventHandler<DiagnosticItem>? ResultRequested;
    public DiagnosticsView() => InitializeComponent();
    private void OpenResult(object? sender, TappedEventArgs arguments) => OpenSelected();
    private void ResultsKeyDown(object? sender, KeyEventArgs arguments)
    {
        if (arguments.Key != Key.Enter) return;
        arguments.Handled = true;
        OpenSelected();
    }
    private void OpenSelected()
    {
        if (Results.SelectedItem is DiagnosticItem item) ResultRequested?.Invoke(this, item);
    }
}
