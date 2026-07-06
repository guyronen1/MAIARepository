namespace Maia.Core.Enums;

public enum JobStatus
{
    Failed,
    Resolved,
    ManualRequired,
    /// <summary>
    /// Operator approved a recommendation whose fix is Manual (no automated
    /// step). The work must be performed off-system; once done, the operator
    /// closes the failure via the "Mark Resolved" action which transitions to
    /// <see cref="Resolved"/>. Distinguishes "system stuck, needs operator
    /// decision" (<see cref="ManualRequired"/>) from "operator decided, now
    /// performing the action".
    /// </summary>
    AwaitingManualAction
}
