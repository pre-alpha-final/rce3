namespace TabletUI.Components;

public sealed record PanelDefinition(string Id, string Title, Type ComponentType, bool Collapsible = false);

public static class PanelCatalog
{
    public static IReadOnlyList<PanelDefinition> All { get; } = Array.AsReadOnly<PanelDefinition>(
    [
        new("youtube", "YouTube", typeof(YoutubePanel)),
        new("debug", "Debug", typeof(DebugPanel), Collapsible: true)
    ]);
}
