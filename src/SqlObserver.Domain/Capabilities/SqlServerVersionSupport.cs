namespace SqlObserver.Domain.Capabilities;

/// <summary>Explicit engine coverage; intervening versions never use a nearby query variant.</summary>
public static class SqlServerVersionSupport
{
    public static bool IsSupportedMajor(int major) => major is 13 or 14 or 15 or 16 or 17;

    public static bool UsesViewServerState(int major) => major is 13 or 14 or 15;
}
