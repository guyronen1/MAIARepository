namespace Maia.Core.Enums;

public enum FixActionType
{
    Manual,          // No auto-execution — operator must intervene
    ApiCall,         // HTTP call to a URL; supports {failureId} placeholder in payload
    StoredProcedure, // Execute a SQL stored procedure; payload = "SpName" or "ConnectionName|SpName"
    Script,          // Execute a shell/PowerShell script; payload = "executable [args]"
    SqlScript,       // Execute a raw SQL statement; payload = SQL text; {failureId} is replaced at runtime
    Composite,       // Chain of FixPolicyRuleSteps run in order; ActionPayload null on header
    CopyFile         // File copy with atomic move; payload = "SOURCE|DEST" (placeholders supported)
}
