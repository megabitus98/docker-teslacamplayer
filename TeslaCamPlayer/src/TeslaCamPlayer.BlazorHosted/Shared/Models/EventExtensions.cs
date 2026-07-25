using System.Globalization;

namespace TeslaCamPlayer.BlazorHosted.Shared.Models;

public static class EventExtensions
{
    public static string GetStreetAndCity(this Event evt)
    {
        if (evt == null) return null;

        var street = (evt.Street ?? string.Empty).Trim();
        var city = (evt.City ?? string.Empty).Trim();

        if (!string.IsNullOrWhiteSpace(street) && !string.IsNullOrWhiteSpace(city))
            return $"{street}, {city}";

        if (!string.IsNullOrWhiteSpace(street))
            return street;

        if (!string.IsNullOrWhiteSpace(city))
            return city;

        return null;
    }

    /// <summary>
    /// The camera tile that triggered this event: Fisheye/Narrow fold into Front,
    /// Cabin/Unknown have no tile. Null when there is no event.
    /// </summary>
    public static Cameras? TriggerTileCamera(this Event evt) => evt?.Camera switch
    {
        Cameras.Front or Cameras.Fisheye or Cameras.Narrow => Cameras.Front,
        Cameras.Back => Cameras.Back,
        Cameras.LeftRepeater => Cameras.LeftRepeater,
        Cameras.RightRepeater => Cameras.RightRepeater,
        Cameras.LeftBPillar => Cameras.LeftBPillar,
        Cameras.RightBPillar => Cameras.RightBPillar,
        _ => null
    };

    public static string GetLocationDescription(this Event evt)
    {
        if (evt == null) return null;

        var streetAndCity = evt.GetStreetAndCity();
        var latStr = (evt.EstLat ?? string.Empty).Trim();
        var lonStr = (evt.EstLon ?? string.Empty).Trim();

        string coords = null;
        if (!string.IsNullOrWhiteSpace(latStr) && !string.IsNullOrWhiteSpace(lonStr))
        {
            if (double.TryParse(latStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var lat) &&
                double.TryParse(lonStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var lon))
            {
                coords = $"{lat:0.#####}, {lon:0.#####}";
            }
            else
            {
                coords = $"{latStr}, {lonStr}";
            }
        }

        if (!string.IsNullOrWhiteSpace(streetAndCity) && !string.IsNullOrWhiteSpace(coords))
            return $"{streetAndCity} ({coords})";

        if (!string.IsNullOrWhiteSpace(streetAndCity))
            return streetAndCity;

        if (!string.IsNullOrWhiteSpace(coords))
            return coords;

        return null;
    }
}
