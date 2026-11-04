using MetaBrain.Core.Security;

namespace MetaBrain.Core.Domain;

/// <summary>A recall perspective, independent of memory kind, evidence and authority.</summary>
public enum CognitiveFacet
{
    World = 1,
    Experience = 2,
    Opinion = 3,
    Habit = 4,
    Intention = 5,
    ActionEffect = 6
}

/// <summary>
/// One classification of an exact workspace passage. The anchor remains bound
/// to its original bytes when stale. Labels do not grant access or assert truth;
/// callers must enforce the workspace's existing authorization and anchor rules.
/// This is an in-memory contract, not a durable annotation or review record.
/// </summary>
public sealed record CognitivePassage(WorkspaceAnchorInfo Anchor, IReadOnlyList<CognitiveFacet> Facets);

public sealed record CognitiveFacetDefinition(CognitiveFacet Facet, string Meaning, string Safeguard);

/// <summary>The owner-reviewed vocabulary; associations with kinds are illustrative, not conversions.</summary>
public static class CognitiveTaxonomy
{
    public static IReadOnlyList<CognitiveFacetDefinition> Definitions { get; } = Array.AsReadOnly(new[]
    {
        new CognitiveFacetDefinition(CognitiveFacet.World, "Claims, observations and questions about the world", "A reported claim is not a verified fact."),
        new CognitiveFacetDefinition(CognitiveFacet.Experience, "Attributed events and attempts in context", "An assistant or tool report is not automatically owner experience."),
        new CognitiveFacetDefinition(CognitiveFacet.Opinion, "Attributed stances and choices", "An AI suggestion is not an owner belief."),
        new CognitiveFacetDefinition(CognitiveFacet.Habit, "Declared practices or patterns needing review", "One mention does not establish a habit."),
        new CognitiveFacetDefinition(CognitiveFacet.Intention, "Attributed intended future actions", "Mention or acknowledgement is not completion."),
        new CognitiveFacetDefinition(CognitiveFacet.ActionEffect, "Actions, conditions and reported or observed outcomes", "Correlation or reported outcomes do not establish causality.")
    });
}
