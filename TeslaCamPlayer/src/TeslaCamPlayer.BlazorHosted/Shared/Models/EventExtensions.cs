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
