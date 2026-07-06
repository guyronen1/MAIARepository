using Maia.Core.Enums;

namespace Maia.Core.Results;

public sealed record FixCatalogueEntry(
    string SuggestedAction,
    FixCategory Category,
    double ConfidenceBoost,
    bool AutoHeal);
