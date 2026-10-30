using Augora.Mobile.Services;
using Augora.Mobile.ViewModels;

namespace Augora.Mobile.Views;

public sealed partial class SearchView : ContentView
{
    private readonly SearchViewModel _viewModel = new();

    public SearchView()
    {
        InitializeComponent();
        BindingContext = _viewModel;
        TitleLabel.Text = Strings.Search;
        QueryEntry.Placeholder = Strings.SearchHint;
    }

    private async void OnSearch(object? sender, EventArgs e) => await _viewModel.SearchAsync(QueryEntry.Text);
}
