namespace PeerSync.Telemetry;

public static class AzureDiagnosticSettings
{
    // A connection string identifies the isolated ingestion resource; it is not a credential.
    // The workspace is capped and this value may be overridden by deployment configuration.
    public const string DefaultConnectionString =
        "InstrumentationKey=593d70d8-21d3-4745-b975-c76f4c6d5bb2;" +
        "IngestionEndpoint=https://westeurope-5.in.applicationinsights.azure.com/;" +
        "LiveEndpoint=https://westeurope.livediagnostics.monitor.azure.com/;" +
        "ApplicationId=67efdd17-65b4-4f0e-918c-3abeb7579f72";

    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("PEERSYNC_AZURE_MONITOR_CONNECTION_STRING")
        ?? DefaultConnectionString;
}
