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
}
