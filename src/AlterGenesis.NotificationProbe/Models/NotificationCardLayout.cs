using System.Windows;

namespace AlterGenesis.NotificationProbe.Models;

public enum NotificationCardSize
{
    Large,
    Small,
}

internal sealed record NotificationCardLayout(
    double Width, double RowHeight, double ViewportHeight,
    Thickness ContentPadding, double HeadingHeight, double RowGap, double TitleGap)
{
    internal static NotificationCardLayout For(NotificationCardSize size) => size switch
    {
        NotificationCardSize.Small => new(376, 128, 512, new Thickness(16), 56, 8, 12),
        _ => new(560, 152, 600, new Thickness(24, 20, 24, 20), 68, 12, 16),
    };

    internal void Apply(ResourceDictionary resources)
    {
        resources["Card.RowHeight"] = RowHeight;
        resources["Card.MaxWindowHeight"] = ViewportHeight + 2;
        resources["Card.ViewportHeight"] = ViewportHeight;
        resources["Card.ContentPadding"] = ContentPadding;
        resources["Card.HeadingHeight"] = HeadingHeight;
        resources["Card.RowGap"] = new GridLength(RowGap);
        resources["Card.TitleGap"] = new GridLength(TitleGap);
    }
}
