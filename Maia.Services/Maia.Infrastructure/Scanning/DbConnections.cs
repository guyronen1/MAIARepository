using System.Data.Common;
using Maia.Core.Enums;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Oracle.ManagedDataAccess.Client;

namespace Maia.Infrastructure.Scanning;

/// <summary>A named connection string resolved together with its database engine.</summary>
public sealed record ResolvedConnection(string Name, string ConnectionString, DbProvider Provider);

/// <summary>
/// Resolves a scan source's <c>ConnectionName</c> to a connection string plus its
/// <see cref="DbProvider"/>, and opens it with the matching driver. The provider is
/// declared in config right next to the connection string, using the standard .NET
/// companion-key convention:
/// <code>
/// "ConnectionStrings": {
///   "Billing":              "Data Source=BILLPRD;User Id=maia_ro;Password=…",
///   "Billing_ProviderName": "Oracle"
/// }
/// </code>
/// No companion key = SQL Server, so every existing connection is unchanged. The
/// companion keys are never connections themselves: <see cref="Names"/> skips them and
/// <see cref="TryResolve"/> refuses them.
/// </summary>
public static class DbConnections
{
    public const string ProviderSuffix = "_ProviderName";

    /// <summary>
    /// Pinned for every Oracle session MAIA opens, so the scan SQL can use type-agnostic
    /// <c>TO_CHAR(col)</c> the way the T-SQL path uses <c>CONVERT(…, 121)</c>: dates come
    /// back ISO (sortable, round-trippable as a watermark), numbers with a '.' decimal
    /// point whatever the server's locale, and a stored watermark string binds back
    /// against a DATE / TIMESTAMP / NUMBER column through the same formats.
    /// </summary>
    internal const string OracleSessionSetup =
        "ALTER SESSION SET " +
        "NLS_DATE_FORMAT = 'YYYY-MM-DD HH24:MI:SS' " +
        "NLS_TIMESTAMP_FORMAT = 'YYYY-MM-DD HH24:MI:SS.FF7' " +
        "NLS_TIMESTAMP_TZ_FORMAT = 'YYYY-MM-DD HH24:MI:SS.FF7 TZH:TZM' " +
        "NLS_NUMERIC_CHARACTERS = '.,'";

    /// <summary>
    /// Parses a <c>{Name}_ProviderName</c> value. Blank = SQL Server. Accepts the short
    /// names and the ADO.NET invariant names, case-insensitively. False = unrecognised.
    /// </summary>
    public static bool TryParseProvider(string? raw, out DbProvider provider)
    {
        provider = DbProvider.SqlServer;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        switch (raw.Trim().ToLowerInvariant())
        {
            case "sqlserver": case "mssql":
            case "system.data.sqlclient": case "microsoft.data.sqlclient":
                provider = DbProvider.SqlServer; return true;
            case "oracle":
            case "oracle.manageddataaccess.client": case "oracle.dataaccess.client":
                provider = DbProvider.Oracle; return true;
            default:
                return false;
        }
    }

    /// <summary>The raw <c>{Name}_ProviderName</c> setting (null when absent).</summary>
    public static string? ProviderSetting(IConfiguration config, string name)
        => config[$"ConnectionStrings:{name}{ProviderSuffix}"];

    /// <summary>Configured connection keys: non-blank value, companion keys excluded.</summary>
    public static IEnumerable<string> Names(IConfiguration config) =>
        config.GetSection("ConnectionStrings").GetChildren()
            .Where(c => !string.IsNullOrWhiteSpace(c.Value)
                     && !c.Key.EndsWith(ProviderSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Key);

    /// <summary>
    /// Resolves <paramref name="name"/>. Returns null with <paramref name="error"/> set
    /// when the connection string is missing or its provider value isn't recognised.
    /// </summary>
    public static ResolvedConnection? TryResolve(IConfiguration config, string name, out string? error)
    {
        error = null;
        var connStr = name.EndsWith(ProviderSuffix, StringComparison.OrdinalIgnoreCase)
            ? null
            : config.GetConnectionString(name);
        if (string.IsNullOrWhiteSpace(connStr))
        {
            error = $"Connection string '{name}' not found in configuration.";
            return null;
        }

        var raw = ProviderSetting(config, name);
        if (!TryParseProvider(raw, out var provider))
        {
            error = $"Connection '{name}' has an unknown {name}{ProviderSuffix} '{raw}'. Use 'SqlServer' or 'Oracle'.";
            return null;
        }
        return new ResolvedConnection(name, connStr, provider);
    }

    /// <summary><see cref="TryResolve"/> that throws <see cref="InvalidOperationException"/>.</summary>
    public static ResolvedConnection Resolve(IConfiguration config, string name)
        => TryResolve(config, name, out var error) ?? throw new InvalidOperationException(error);

    /// <summary>Opens the connection with the provider's driver (Oracle: plus the session setup).</summary>
    public static async Task<DbConnection> OpenAsync(ResolvedConnection c, CancellationToken ct)
        => await OpenAsync(c.Provider, c.ConnectionString, ct);

    public static async Task<DbConnection> OpenAsync(DbProvider provider, string connectionString, CancellationToken ct)
    {
        DbConnection conn = provider == DbProvider.Oracle
            ? new OracleConnection(connectionString)
            : new SqlConnection(connectionString);
        try
        {
            await conn.OpenAsync(ct);
            if (provider == DbProvider.Oracle)
            {
                await using var setup = conn.CreateCommand();
                setup.CommandText = OracleSessionSetup;
                await setup.ExecuteNonQueryAsync(ct);
            }
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// A command on <paramref name="conn"/>. Oracle commands bind BY NAME — ODP.NET's
    /// default is positional, which would silently mis-bind a reused or reordered
    /// parameter.
    /// </summary>
    public static DbCommand CreateCommand(DbConnection conn, string sql, int? timeoutSeconds = null)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (timeoutSeconds is { } t) cmd.CommandTimeout = t;
        if (cmd is OracleCommand oc) oc.BindByName = true;
        return cmd;
    }

    /// <summary>
    /// <c>reader.GetValue(i)</c> (DBNull → null) that survives Oracle NUMBERs beyond
    /// .NET decimal's 28-29 digits (e.g. an unconstrained NUMBER holding 1/3), which
    /// ODP.NET refuses to convert — those come back as the provider value's text.
    /// </summary>
    public static object? ReadValue(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        try { return reader.GetValue(ordinal); }
        catch (Exception ex) when (ex is InvalidCastException or OverflowException)
        {
            return reader.GetProviderSpecificValue(ordinal)?.ToString();
        }
    }

    /// <summary>
    /// Points ODP.NET at the folder holding <c>tnsnames.ora</c> so a TNS alias in
    /// <c>Data Source</c> resolves. ODP.NET Core does not read the registry /
    /// ORACLE_HOME like the unmanaged client, and under IIS the working directory is
    /// system32, so the location is set explicitly. First hit wins:
    /// <c>Oracle:TnsAdmin</c> config → <c>TNS_ADMIN</c> env var →
    /// <c>%ORACLE_HOME%\network\admin</c> (when it contains tnsnames.ora).
    /// Returns the folder used, or null when none was found (EZConnect
    /// <c>host:port/service</c> data sources still work without one).
    /// </summary>
    public static string? ConfigureOracle(IConfiguration config)
    {
        var tnsAdmin = config["Oracle:TnsAdmin"];
        if (string.IsNullOrWhiteSpace(tnsAdmin))
            tnsAdmin = Environment.GetEnvironmentVariable("TNS_ADMIN");
        if (string.IsNullOrWhiteSpace(tnsAdmin))
        {
            var home = Environment.GetEnvironmentVariable("ORACLE_HOME");
            var candidate = string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, "network", "admin");
            if (candidate is not null && File.Exists(Path.Combine(candidate, "tnsnames.ora")))
                tnsAdmin = candidate;
        }
        if (string.IsNullOrWhiteSpace(tnsAdmin)) return null;

        tnsAdmin = tnsAdmin.Trim();
        OracleConfiguration.TnsAdmin = tnsAdmin;
        return tnsAdmin;
    }
}
