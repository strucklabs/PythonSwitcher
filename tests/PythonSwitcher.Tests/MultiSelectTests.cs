using PythonSwitcher.Core;
using PythonSwitcher.Core.VersionCheck;

namespace PythonSwitcher.Tests;

/// <summary>Mehrfachauswahl im Versionscheck: gemeinsamer Trockenlauf und zusammengefasste Befehle.</summary>
public class MultiSelectTests
{
    private const string Py310 = @"C:\Py310";
    private const string Py313 = @"C:\Py313";

    private static CheckItem Pip(string root, string pkg, string installed, string? latest, string hint = "")
    {
        var python = ToolDefinition.Python.MainExecutablePath(root);
        string? cmd = latest is not null && VersionCompare.Compare(installed, latest) < 0 ? PipScan.UpgradeCommand(python, [pkg]) : null;
        return new CheckItem(CheckCategory.PipPaket, root, pkg, installed, latest,
            cmd is null ? CheckStatus.Aktuell : CheckStatus.UpdateVerfuegbar, hint, null, root, cmd,
            new CheckTarget(TargetKind.PipPackage, ToolKind.Python, root, Package: pkg));
    }

    private static readonly CheckItem Git = new(CheckCategory.Git, "Git", "Git", "2.53.0", "2.56.0", CheckStatus.UpdateVerfuegbar, "",
        null, @"C:\Program Files\Git", "winget upgrade --id Git.Git", new CheckTarget(TargetKind.Runtime, ToolKind.Git, @"C:\Program Files\Git"));

    [Fact]
    public void Packages_of_one_python_can_be_tested_together()
    {
        Assert.True(VersionChecker.CanDryRunTogether([Pip(Py310, "rich", "13.0", "14.1"), Pip(Py310, "tqdm", "4.66", "4.67")]));
    }

    [Fact]
    public void Mixed_selections_cannot_be_tested_together()
    {
        Assert.False(VersionChecker.CanDryRunTogether([Pip(Py310, "rich", "13.0", "14.1"), Pip(Py313, "tqdm", "4.66", "4.67")]));
        Assert.False(VersionChecker.CanDryRunTogether([Pip(Py310, "rich", "13.0", "14.1"), Pip(Py310, "tqdm", "4.67", "4.67")]));
        Assert.False(VersionChecker.CanDryRunTogether([Pip(Py310, "rich", "13.0", "14.1"), Git]));
        Assert.False(VersionChecker.CanDryRunTogether([Pip(Py310, "uvx", "1.0", "1.1", "Not updatable with pip: …")]));
        Assert.False(VersionChecker.CanDryRunTogether([]));
    }

    [Fact]
    public void Commands_are_combined_per_python()
    {
        var cmds = VersionChecker.CombineCommands([
            Pip(Py310, "rich", "13.0", "14.1"), Pip(Py310, "tqdm", "4.66", "4.67"),
            Pip(Py313, "httpx", "0.27", "0.28"), Pip(Py310, "numpy", "1.26.4", "1.26.4"), Git,
        ]);
        Assert.Equal(3, cmds.Count);
        Assert.Equal(PipScan.UpgradeCommand(ToolDefinition.Python.MainExecutablePath(Py310), ["rich", "tqdm"]), cmds[0]);
        Assert.Equal(PipScan.UpgradeCommand(ToolDefinition.Python.MainExecutablePath(Py313), ["httpx"]), cmds[1]);
        Assert.Equal("winget upgrade --id Git.Git", cmds[2]);
    }

    [Fact]
    public void Single_entry_keeps_its_own_command()
    {
        var cmds = VersionChecker.CombineCommands([Git]);
        Assert.Equal(["winget upgrade --id Git.Git"], cmds);
    }
}
