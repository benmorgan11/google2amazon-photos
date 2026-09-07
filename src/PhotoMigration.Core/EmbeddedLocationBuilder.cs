namespace PhotoMigration.Core;

public static class EmbeddedLocationBuilder
{
    public static EmbeddedLocationBuildResult Build(
        IEnumerable<EmbeddedGpsCandidate> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var orderedValues = values
            .OrderBy(value => value.SourceValue.GroupName, StringComparer.Ordinal)
            .ThenBy(value => value.SourceValue.TagName, StringComparer.Ordinal)
            .ThenBy(value => value.SourceValue.RawValue, StringComparer.Ordinal)
            .ThenBy(value => value.SemanticField)
            .ThenBy(value => value.ReferenceValue?.GroupName, StringComparer.Ordinal)
            .ThenBy(value => value.ReferenceValue?.TagName, StringComparer.Ordinal)
            .ThenBy(value => value.ReferenceValue?.RawValue, StringComparer.Ordinal)
            .ToList();

        var candidates = new List<EmbeddedLocationCandidate>();
        var issues = new List<EmbeddedLocationIssue>();

        foreach (var group in orderedValues.GroupBy(
                     value => value.SourceValue.GroupName,
                     StringComparer.Ordinal))
        {
            foreach (var combinedCoordinates in group
                         .Where(IsCombinedCoordinate)
                         .GroupBy(
                             value => value.SourceValue,
                             ReferenceEqualityComparer.Instance))
            {
                BuildLocations(
                    group.Key,
                    combinedCoordinates,
                    candidates,
                    issues);
            }

            var individualValues = group
                .Where(value => !IsCombinedCoordinate(value))
                .ToList();
            if (individualValues.Count > 0)
            {
                BuildLocations(
                    group.Key,
                    individualValues,
                    candidates,
                    issues);
            }
        }

        return new EmbeddedLocationBuildResult(
            candidates.AsReadOnly(),
            issues
                .OrderBy(issue => issue.GroupName, StringComparer.Ordinal)
                .ThenBy(issue => issue.Kind)
                .ToList()
                .AsReadOnly());
    }

    private static void BuildLocations(
        string groupName,
        IEnumerable<EmbeddedGpsCandidate> values,
        ICollection<EmbeddedLocationCandidate> candidates,
        ICollection<EmbeddedLocationIssue> issues)
    {
        var latitudes = ValuesFor(values, EmbeddedMetadataField.GpsLatitude);
        var longitudes = ValuesFor(values, EmbeddedMetadataField.GpsLongitude);
        var altitudes = ValuesFor(values, EmbeddedMetadataField.GpsAltitude);
        var hasCoordinatePair = latitudes.Count > 0 && longitudes.Count > 0;

        if (latitudes.Count > 0 && longitudes.Count == 0)
        {
            issues.Add(new EmbeddedLocationIssue(
                EmbeddedLocationIssueKind.LatitudeWithoutLongitude,
                groupName,
                latitudes,
                "The metadata group has latitude values but no longitude values."));
        }

        if (longitudes.Count > 0 && latitudes.Count == 0)
        {
            issues.Add(new EmbeddedLocationIssue(
                EmbeddedLocationIssueKind.LongitudeWithoutLatitude,
                groupName,
                longitudes,
                "The metadata group has longitude values but no latitude values."));
        }

        if (altitudes.Count > 0 && !hasCoordinatePair)
        {
            issues.Add(new EmbeddedLocationIssue(
                EmbeddedLocationIssueKind.AltitudeWithoutCoordinatePair,
                groupName,
                altitudes,
                "The metadata group has altitude values but no complete coordinate pair."));
        }

        if (!hasCoordinatePair)
        {
            return;
        }

        foreach (var latitude in latitudes)
        {
            foreach (var longitude in longitudes)
            {
                if (altitudes.Count == 0)
                {
                    candidates.Add(new EmbeddedLocationCandidate(
                        groupName,
                        latitude,
                        longitude,
                        null));
                    continue;
                }

                foreach (var altitude in altitudes)
                {
                    candidates.Add(new EmbeddedLocationCandidate(
                        groupName,
                        latitude,
                        longitude,
                        altitude));
                }
            }
        }
    }

    private static IReadOnlyList<EmbeddedGpsCandidate> ValuesFor(
        IEnumerable<EmbeddedGpsCandidate> values,
        EmbeddedMetadataField field) =>
        values
            .Where(value => value.SemanticField == field)
            .ToList()
            .AsReadOnly();

    private static bool IsCombinedCoordinate(EmbeddedGpsCandidate value) =>
        value.SourceValue.Field == EmbeddedMetadataField.GpsCoordinates;
}
