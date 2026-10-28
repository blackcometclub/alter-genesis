using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace AlterGenesis.NotificationProbe.Calendars;

public sealed record IcalCalendarSubscription(
    string Id,
    string Name,
    string ColorHex,
    string SecretUrl)
{
    public static IcalCalendarSubscription Create(string name, string colorHex, string secretUrl)
    {
        var normalizedUrl = IcalFeedClient.ValidateSecretUrl(secretUrl).AbsoluteUri;
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedUrl)));
        return new IcalCalendarSubscription(id, name.Trim(), colorHex.ToUpperInvariant(), normalizedUrl);
    }

    [JsonIgnore]
    public Brush ColorBrush
    {
        get
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ColorHex));
            brush.Freeze();
            return brush;
        }
    }
}

public sealed record IcalCalendarPreview(string Name, int EventCount);
