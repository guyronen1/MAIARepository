using System.Security.Cryptography;
using System.Text;

namespace Maia.Core.Analysis;

/// <summary>
/// Stable, reproducible hash of a cluster's sample failure ids — recorded as
/// rule provenance (<c>SuggestedFromHash</c>) when an operator accepts a
/// suggestion. Same cluster membership ⇒ same hash, across code paths and
/// across v2 analyzer implementations, so v2 can match operator decisions back
/// to the cluster context that produced them.
///
/// Definition (pinned): sort the ids ascending, join comma-separated, SHA-256,
/// take the first 16 hex chars (lowercase).
/// </summary>
public static class ClusterHash
{
    public static string Of(IEnumerable<int> failureIds)
    {
        var joined = string.Join(",", failureIds.OrderBy(id => id));
        var bytes  = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
