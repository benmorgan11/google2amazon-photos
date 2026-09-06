namespace PhotoMigration.Core;

public sealed class TakeoutSidecarParseException : Exception
{
    public TakeoutSidecarParseException(
        string sidecarPath,
        string reason,
        Exception? innerException = null)
        : base($"Could not parse sidecar '{sidecarPath}': {reason}", innerException)
    {
        SidecarPath = sidecarPath;
    }

    public string SidecarPath { get; }
}
