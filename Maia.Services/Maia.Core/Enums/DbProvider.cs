namespace Maia.Core.Enums;

/// <summary>
/// Database engine behind a named connection string. Declared in config next to the
/// connection string as <c>ConnectionStrings:{Name}_ProviderName</c> (the standard
/// .NET convention); absent = <see cref="SqlServer"/>, so every existing connection
/// keeps its behaviour. Decides how a Database scan source's rules are executed.
/// </summary>
public enum DbProvider
{
    SqlServer, // Microsoft.Data.SqlClient, T-SQL
    Oracle     // Oracle.ManagedDataAccess.Core, Oracle SQL; Data Source may be a TNS alias
}
