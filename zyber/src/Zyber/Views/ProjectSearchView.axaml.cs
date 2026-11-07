using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Zyber.Models;
using Zyber.Services;

namespace Zyber.Views;

public sealed partial class ProjectSearchView : UserControl
{
    public event EventHandler<SearchHit>? ResultRequested;
    public ProjectSearchView() => InitializeComponent();
    public void FocusQuery() { Query.Focus(); Query.SelectAll(); }
    private async void SearchClicked(object? sender, RoutedEventArgs arguments)
    {
        if (DataContext is SearchTab search) await search.SearchAsync();
    }
    private void CancelClicked(object? sender, RoutedEventArgs arguments)
    {
        if (DataContext is SearchTab search) search.Cancel();
    }
    private async void QueryKeyDown(object? sender, KeyEventArgs arguments)
    {
        if (arguments.Key != Key.Enter || DataContext is not SearchTab search) return;
        arguments.Handled = true;
        await search.SearchAsync();
    }
    private void ResultDoubleTapped(object? sender, TappedEventArgs arguments) => OpenSelected();
    private void ResultKeyDown(object? sender, KeyEventArgs arguments)
    {
        if (arguments.Key != Key.Enter) return;
        arguments.Handled = true;
        OpenSelected();
    }
    private void OpenSelected()
    {
        if (Results.SelectedItem is SearchHit hit) ResultRequested?.Invoke(this, hit);
    }
}
