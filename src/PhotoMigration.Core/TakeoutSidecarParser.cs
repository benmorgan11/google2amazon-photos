using System.Globalization;
using System.Text.Json;

namespace PhotoMigration.Core;

public static class TakeoutSidecarParser
{
    public static TakeoutSidecarMetadata Parse(string sidecarPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sidecarPath);

        try
        {
            using var stream = new FileStream(
                sidecarPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            using var document = JsonDocument.Parse(stream);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Invalid(sidecarPath, "The JSON root must be an object.");
            }

            var root = document.RootElement;
            var coordinates = ReadCoordinates(root, sidecarPath);

            return new TakeoutSidecarMetadata(
                ReadOptionalString(root, "title", sidecarPath),
                ReadOptionalString(root, "description", sidecarPath),
                ReadTimestamp(root, "creationTime", sidecarPath),
                ReadTimestamp(root, "photoTakenTime", sidecarPath),
                coordinates.Latitude,
                coordinates.Longitude,
                coordinates.Altitude,
                ReadOptionalString(root, "url", sidecarPath));
        }
        catch (JsonException exception)
        {
            throw Invalid(sidecarPath, "The file does not contain valid JSON.", exception);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            throw Invalid(sidecarPath, "The file could not be read.", exception);
        }
    }

    private static string? ReadOptionalString(
        JsonElement parent,
        string propertyName,
        string sidecarPath)
    {
        if (!parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid(sidecarPath, $"Property '{propertyName}' must be a string or null.");
        }

        return value.GetString();
    }

    private static DateTimeOffset? ReadTimestamp(
        JsonElement root,
        string propertyName,
        string sidecarPath)
    {
        if (!root.TryGetProperty(propertyName, out var container)
            || container.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (container.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(sidecarPath, $"Property '{propertyName}' must be an object or null.");
        }

        if (!container.TryGetProperty("timestamp", out var timestamp)
            || timestamp.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        long unixSeconds;

        if (timestamp.ValueKind == JsonValueKind.String)
        {
            var text = timestamp.GetString();
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out unixSeconds))
            {
                throw Invalid(sidecarPath, $"Property '{propertyName}.timestamp' is not a valid Unix timestamp.");
            }
        }
        else if (timestamp.ValueKind == JsonValueKind.Number)
        {
            if (!timestamp.TryGetInt64(out unixSeconds))
            {
                throw Invalid(sidecarPath, $"Property '{propertyName}.timestamp' is not a valid Unix timestamp.");
            }
        }
        else
        {
            throw Invalid(sidecarPath, $"Property '{propertyName}.timestamp' must be a string, number, or null.");
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw Invalid(sidecarPath, $"Property '{propertyName}.timestamp' is outside the supported range.", exception);
        }
    }

    private static Coordinates ReadCoordinates(JsonElement root, string sidecarPath)
    {
        if (!root.TryGetProperty("geoData", out var geoData)
            || geoData.ValueKind == JsonValueKind.Null)
        {
            return default;
        }

        if (geoData.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(sidecarPath, "Property 'geoData' must be an object or null.");
        }

        return new Coordinates(
            ReadCoordinate(geoData, "latitude", -90, 90, sidecarPath),
            ReadCoordinate(geoData, "longitude", -180, 180, sidecarPath),
            ReadCoordinate(geoData, "altitude", null, null, sidecarPath));
    }

    private static double? ReadCoordinate(
        JsonElement geoData,
        string propertyName,
        double? minimum,
        double? maximum,
        string sidecarPath)
    {
        if (!geoData.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number)
            || !double.IsFinite(number)
            || (minimum.HasValue && number < minimum.Value)
            || (maximum.HasValue && number > maximum.Value))
        {
            throw Invalid(sidecarPath, $"Property 'geoData.{propertyName}' is not a valid coordinate.");
        }

        return number;
    }

    private static TakeoutSidecarParseException Invalid(
        string sidecarPath,
        string reason,
        Exception? innerException = null)
    {
        return new TakeoutSidecarParseException(sidecarPath, reason, innerException);
    }

    private readonly record struct Coordinates(double? Latitude, double? Longitude, double? Altitude);
}
