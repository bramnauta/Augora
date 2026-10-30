using Microsoft.Maui.Controls.Shapes;
using ShapePath = Microsoft.Maui.Controls.Shapes.Path;

namespace Augora.Mobile.Views;

/// <summary>A small vector icon (Material Symbols paths), tinted with <see cref="Fill"/>.</summary>
public sealed class Icon : ContentView
{
    public static readonly BindableProperty KindProperty = BindableProperty.Create(
        nameof(Kind), typeof(string), typeof(Icon), null, propertyChanged: (bindable, _, kind) => ((Icon)bindable).UpdateShape((string?)kind));

    public static readonly BindableProperty FillProperty = BindableProperty.Create(
        nameof(Fill), typeof(Color), typeof(Icon), Colors.White, propertyChanged: (bindable, _, color) => ((Icon)bindable).UpdateFill((Color)color));

    private static readonly Dictionary<string, string> Paths = new()
    {
        ["Play"] = "M8 5v14l11-7z",
        ["Pause"] = "M6 19h4V5H6v14zm8-14v14h4V5h-4z",
        ["Next"] = "M6 18l8.5-6L6 6v12zM16 6v12h2V6h-2z",
        ["Previous"] = "M6 6h2v12H6zm3.5 6l8.5 6V6z",
        ["Shuffle"] = "M10.59 9.17L5.41 4 4 5.41l5.17 5.17 1.42-1.41zM14.5 4l2.04 2.04L4 18.59 5.41 20 17.96 7.46 20 9.5V4h-5.5zm.33 9.41l-1.41 1.41 3.13 3.13L14.5 20H20v-5.5l-2.04 2.04-3.13-3.13z",
        ["Repeat"] = "M7 7h10v3l4-4-4-4v3H5v6h2V7zm10 10H7v-3l-4 4 4 4v-3h12v-6h-2v4z",
        ["RepeatOne"] = "M7 7h10v3l4-4-4-4v3H5v6h2V7zm10 10H7v-3l-4 4 4 4v-3h12v-6h-2v4zm-4-2V9h-1l-2 1v1h1.5v4H13z",
        ["Volume"] = "M3 9v6h4l5 5V4L7 9H3zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02zM14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77z",
        ["Queue"] = "M15 6H3v2h12V6zm0 4H3v2h12v-2zM3 16h8v-2H3v2zM17 6v8.18c-.31-.11-.65-.18-1-.18-1.66 0-3 1.34-3 3s1.34 3 3 3 3-1.34 3-3V8h3V6h-5z",
        ["Search"] = "M15.5 14h-.79l-.28-.27A6.471 6.471 0 0 0 16 9.5 6.5 6.5 0 1 0 9.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5zm-6 0C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14z",
        ["Home"] = "M10 20v-6h4v6h5v-8h3L12 3 2 12h3v8z",
        ["Down"] = "M7.41 8.59L12 13.17l4.59-4.58L18 10l-6 6-6-6 1.41-1.41z",
        ["Back"] = "M15.41 7.41L14 6l-6 6 6 6 1.41-1.41L10.83 12z",
        ["Chevron"] = "M10 6L8.59 7.41 13.17 12l-4.58 4.59L10 18l6-6z",
        ["More"] = "M12 8c1.1 0 2-.9 2-2s-.9-2-2-2-2 .9-2 2 .9 2 2 2zm0 2c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2zm0 6c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2z",
        ["Close"] = "M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z",
        ["Speaker"] = "M17 2H7c-1.1 0-2 .9-2 2v16c0 1.1.9 2 2 2h10c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zm-5 2c1.1 0 2 .9 2 2s-.9 2-2 2-2-.9-2-2 .9-2 2-2zm0 16c-2.76 0-5-2.24-5-5s2.24-5 5-5 5 2.24 5 5-2.24 5-5 5zm0-8c-1.66 0-3 1.34-3 3s1.34 3 3 3 3-1.34 3-3-1.34-3-3-3z",
        ["Note"] = "M12 3v10.55c-.59-.34-1.27-.55-2-.55-2.21 0-4 1.79-4 4s1.79 4 4 4 4-1.79 4-4V7h4V3h-6z",
        ["Check"] = "M9 16.17L4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41z",
    };

    private static readonly PathGeometryConverter Converter = new();

    private readonly ShapePath _path = new() { Aspect = Stretch.Uniform, Fill = new SolidColorBrush(Colors.White) };

    public Icon()
    {
        WidthRequest = 24;
        HeightRequest = 24;
        HorizontalOptions = LayoutOptions.Center;
        VerticalOptions = LayoutOptions.Center;
        Content = _path;
    }

    public string? Kind
    {
        get => (string?)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public Color Fill
    {
        get => (Color)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    private void UpdateShape(string? kind) =>
        _path.Data = kind is not null && Paths.TryGetValue(kind, out var data) ? (Geometry?)Converter.ConvertFromInvariantString(data) : null;

    private void UpdateFill(Color color) => _path.Fill = new SolidColorBrush(color);
}
