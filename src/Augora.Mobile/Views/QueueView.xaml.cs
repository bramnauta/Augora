using Augora.Mobile.Services;
using Augora.Mobile.ViewModels;

namespace Augora.Mobile.Views;

public sealed partial class QueueView : ContentView
{
    private readonly QueueViewModel _viewModel = Ioc.Get<QueueViewModel>();

    public QueueView()
    {
        InitializeComponent();
        BindingContext = _viewModel;
        TitleLabel.Text = Strings.Queue;
        ClearButton.Text = Strings.ClearQueue;
        EmptyLabel.Text = Strings.QueueEmpty;
    }

    private async void OnClear(object? sender, EventArgs e) => await _viewModel.ClearAsync();

    private async void OnPlay(object? sender, TappedEventArgs e)
    {
        if (((BindableObject)sender!).BindingContext is QueueRow row)
        {
            await _viewModel.PlayAsync(row);
        }
    }

    private async void OnRemove(object? sender, TappedEventArgs e)
    {
        if (((BindableObject)sender!).BindingContext is QueueRow row)
        {
            await _viewModel.RemoveAsync(row);
        }
    }
}
