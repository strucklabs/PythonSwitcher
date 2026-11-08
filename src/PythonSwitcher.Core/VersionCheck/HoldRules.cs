namespace PythonSwitcher.Core.VersionCheck;

/// <summary>Festhalten für alle Eintragsarten: welcher Schlüssel gehört zu einer Zeile, und wie wirkt das Festhalten?</summary>
public static class HoldRules
{
    /// <summary>Schlüssel (Art, Ort, Name) einer Zeile; null, wenn die Zeile nichts Installiertes darstellt.</summary>
    public static (string Kind, string Root, string Package)? KeyFor(CheckItem item) => item.Target switch
    {
        { Kind: TargetKind.PipPackage, Root: { } root, Package: { } pkg } => (HeldPackage.KindPip, root, pkg),
        { Kind: TargetKind.Runtime, Root: { } root, Tool: { } tool } when item.Installed is not null
            => (HeldPackage.KindRuntime, root, tool.ToString()),
        { Kind: TargetKind.Tool, CatalogName: { } name } when item.Installed is not null
            => (HeldPackage.KindTool, item.Detail ?? "", name),
        _ => null,
    };

    public static HeldPackage? Find(CheckItem item, IEnumerable<HeldPackage> holds) =>
        KeyFor(item) is { } k ? holds.FirstOrDefault(h => h.Matches(k.Kind, k.Root, k.Package)) : null;

    /// <summary>
    /// Wendet das Festhalten auf Python-/Git-Installationen und Werkzeuge an (pip-Pakete behandelt PipScan selbst):
    /// ein verfügbares Update wird zum Hinweis ohne Befehl, sonst bekommt die Zeile eine Notiz.
    /// </summary>
    public static CheckItem Apply(CheckItem item, IEnumerable<HeldPackage> holds)
    {
        if (item.Target?.Kind is not (TargetKind.Runtime or TargetKind.Tool) || Find(item, holds) is not { } hold)
            return item;
        if (item.Status == CheckStatus.UpdateVerfuegbar)
            return item with { Status = CheckStatus.Hinweis, Hint = hold.Describe(), UpdateCommand = null };
        return item with { Note = item.Note is null ? hold.Describe() : hold.Describe() + "\n" + item.Note };
    }
}
