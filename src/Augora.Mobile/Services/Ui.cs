namespace Augora.Mobile.Services;

/// <summary>Dialogs on the current page.</summary>
internal static class Ui
{
    private static Page? Page => Application.Current is { Windows.Count: > 0 } app ? app.Windows[0].Page : null;

    public static Task AlertAsync(string title, string message) =>
        Page?.DisplayAlertAsync(title, message, Strings.Ok) ?? Task.CompletedTask;

    /// <summary>The chosen option, or <c>null</c> when cancelled.</summary>
    public static async Task<string?> ChooseAsync(string title, params string[] options)
    {
        if (Page is not { } page)
        {
            return null;
        }

        var choice = await page.DisplayActionSheetAsync(title, Strings.Cancel, null, options);
        return choice is null || choice == Strings.Cancel ? null : choice;
    }
}
