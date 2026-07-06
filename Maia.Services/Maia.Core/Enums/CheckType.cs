namespace Maia.Core.Enums;

public enum CheckType
{
    ColumnRange      = 0,  // Database: column value is outside [MinValue, MaxValue]
    ErrorKeyword     = 1,  // FileSystem: flag log lines containing TargetField text
    StatusCode       = 2,  // ApiEndpoint: response HTTP status != ExpectedValue
    ResponseContains = 3,  // ApiEndpoint: response body contains TargetField text
    ValueEquals      = 4,  // Database: column value exactly equals ExpectedValue → flag as error
    FileContent      = 5,  // FileContent: match files by TargetField (filename pattern), extract + test a value
    SqlQuery         = 6   // Database: run operator-written SQL/EXEC (SourceTable); every returned row is a failure
}
