using PythonSwitcher.Core;

namespace PythonSwitcher.Tests;

/// <summary>Prüft die Umsortier-Logik an nachgebauten PATH-Listen. Registry und echter PATH werden nicht berührt.</summary>
public class PathPlannerTests
{
    private const string P = @"C:\Users\Test\AppData\Local\Programs\Python";
    private const string WinApps = @"C:\Users\Test\AppData\Local\Microsoft\WindowsApps";

    /// <summary>Simuliertes Dateisystem: Menge vorhandener Dateien.</summary>
    private static Func<string, bool> Files(params string[] files)
    {
        var set = new HashSet<string>(files.Select(f => f.ToLowerInvariant()));
        return f => set.Contains(Path.GetFullPath(f).ToLowerInvariant());
    }

    private static readonly string[] PythonFiles =
    [
        $@"{P}\Python310\python.exe", $@"{P}\Python310\Scripts\pip.exe",
        $@"{P}\Python312\python.exe", $@"{P}\Python312\Scripts\pip.exe",
        $@"{P}\Python313\python.exe", $@"{P}\Python313\Scripts\pip.exe",
        $@"{P}\Python31\python.exe",
        @"D:\Neu\Python314\python.exe", @"D:\Neu\Python314\Scripts\pip.exe",
        $@"{P}\Launcher\py.exe",
    ];

    /// <summary>Nachbau eines typischen Benutzer-PATH mit drei Python-Versionen.</summary>
    private static List<string> TypicalUser() =>
    [
        $@"{P}\Python310\Scripts\",
        $@"{P}\Python310\",
        $@"{P}\Python313\Scripts\",
        $@"{P}\Python313\",
        $@"{P}\Python312\Scripts\",
        $@"{P}\Python312\",
        $@"{P}\Launcher\",
        WinApps,
        @"%USERPROFILE%\.dotnet\tools",
    ];

    private static readonly List<string> TypicalSystem = [@"%SystemRoot%\system32", @"%SystemRoot%", @"C:\Program Files\dotnet\"];

    private static PathPlanner PythonPlanner() => new(ToolDefinition.Python, Files(PythonFiles));

    [Fact]
    public void Activate_moves_both_entries_of_chosen_python_to_front_and_keeps_the_rest_in_order()
    {
        var user = TypicalUser();
        var plan = PythonPlanner().PlanActivation($@"{P}\Python312", user, TypicalSystem, allowSystem: false);

        Assert.True(plan.UserChanged);
        Assert.False(plan.SystemChanged);
        Assert.Equal(
        [
            $@"{P}\Python312\Scripts\",
            $@"{P}\Python312\",
            $@"{P}\Python310\Scripts\",
            $@"{P}\Python310\",
            $@"{P}\Python313\Scripts\",
            $@"{P}\Python313\",
            $@"{P}\Launcher\",
            WinApps,
            @"%USERPROFILE%\.dotnet\tools",
        ], plan.User);
    }

    [Fact]
    public void Activate_keeps_every_entry_exactly_once()
    {
        var user = TypicalUser();
        foreach (var v in new[] { "Python310", "Python312", "Python313" })
        {
            var plan = PythonPlanner().PlanActivation($@"{P}\{v}", user, TypicalSystem, allowSystem: false);
            Assert.Equal(user.OrderBy(x => x), plan.User.OrderBy(x => x));
        }
    }

    [Fact]
    public void Activate_of_already_active_version_changes_nothing()
    {
        var user = TypicalUser();
        var plan = PythonPlanner().PlanActivation($@"{P}\Python310", user, TypicalSystem, allowSystem: false);
        Assert.False(plan.UserChanged);
        Assert.Equal(user, plan.User);
    }

    [Fact]
    public void Activated_version_is_what_windows_finds_first()
    {
        var planner = PythonPlanner();
        var plan = planner.PlanActivation($@"{P}\Python313", TypicalUser(), TypicalSystem, allowSystem: false);
        var effective = planner.FindEffective(plan.User, TypicalSystem);
        Assert.NotNull(effective);
        Assert.True(PathUtil.IsUnder(effective.Value.Entry, $@"{P}\Python313"));
    }

    [Fact]
    public void Version_not_yet_in_path_gets_both_entries_before_first_competitor()
    {
        var plan = PythonPlanner().PlanActivation(@"D:\Neu\Python314", TypicalUser(), TypicalSystem, allowSystem: false);
        Assert.Equal(@"D:\Neu\Python314\Scripts", plan.User[0]);
        Assert.Equal(@"D:\Neu\Python314", plan.User[1]);
        Assert.Equal(TypicalUser().Count + 2, plan.User.Count);
    }

    [Fact]
    public void Store_redirect_always_counts_as_competitor()
    {
        // Nur die Store-Weiterleitung im PATH: die neue Version muss davor stehen.
        List<string> user = [@"C:\Tools", WinApps];
        var plan = PythonPlanner().PlanActivation(@"D:\Neu\Python314", user, TypicalSystem, allowSystem: false);
        Assert.Equal([@"C:\Tools", @"D:\Neu\Python314\Scripts", @"D:\Neu\Python314", WinApps], plan.User);
    }

    [Fact]
    public void Similar_folder_names_are_not_mixed_up()
    {
        // Python31 darf nicht als Teil von Python310 gelten und umgekehrt.
        List<string> user = [$@"{P}\Python310\Scripts", $@"{P}\Python310", $@"{P}\Python31"];
        var plan = PythonPlanner().PlanActivation($@"{P}\Python31", user, TypicalSystem, allowSystem: false);
        Assert.Equal($@"{P}\Python31\Scripts", plan.User[0]);
        Assert.Equal($@"{P}\Python31", plan.User[1]);
        Assert.Equal($@"{P}\Python310\Scripts", plan.User[2]);
        Assert.Equal($@"{P}\Python310", plan.User[3]);
    }

    [Fact]
    public void Entries_with_variables_keep_their_spelling()
    {
        var user = TypicalUser();
        var plan = PythonPlanner().PlanActivation($@"{P}\Python313", user, TypicalSystem, allowSystem: false);
        Assert.Contains(@"%USERPROFILE%\.dotnet\tools", plan.User);
    }

    [Fact]
    public void Add_inactive_places_new_version_right_behind_active_one()
    {
        var plan = PythonPlanner().PlanAddInactive(@"D:\Neu\Python314", $@"{P}\Python310", TypicalUser(), TypicalSystem);
        Assert.Equal($@"{P}\Python310\Scripts\", plan.User[0]);
        Assert.Equal($@"{P}\Python310\", plan.User[1]);
        Assert.Equal(@"D:\Neu\Python314\Scripts", plan.User[2]);
        Assert.Equal(@"D:\Neu\Python314", plan.User[3]);
        // Aktiv bleibt die bisherige Version.
        var eff = PythonPlanner().FindEffective(plan.User, TypicalSystem);
        Assert.True(PathUtil.IsUnder(eff!.Value.Entry, $@"{P}\Python310"));
    }

    [Fact]
    public void Removal_takes_out_only_entries_of_that_installation()
    {
        var plan = PythonPlanner().PlanRemoval($@"{P}\Python313", TypicalUser(), TypicalSystem, allowSystem: false);
        Assert.DoesNotContain(plan.User, e => PathUtil.IsUnder(e, $@"{P}\Python313"));
        Assert.Equal(TypicalUser().Count - 2, plan.User.Count);
        Assert.False(plan.SystemChanged);
    }

    // ---------- Git: System-PATH gewinnt gegen Benutzer-PATH ----------

    private const string SysGit = @"C:\Program Files\Git";
    private const string PortGit = @"C:\Users\Test\AppData\Local\PythonSwitcher\git\2.56.0.4";

    private static PathPlanner GitPlanner() => new(ToolDefinition.Git, Files(
        $@"{SysGit}\cmd\git.exe", $@"{SysGit}\mingw64\bin\git.exe",
        $@"{PortGit}\cmd\git.exe"));

    private static List<string> GitSystem() => [@"%SystemRoot%\system32", @"C:\Program Files\dotnet\", $@"{SysGit}\cmd"];

    [Fact]
    public void Git_without_system_permission_reports_conflict_and_leaves_system_alone()
    {
        var plan = GitPlanner().PlanActivation(PortGit, [@"C:\Tools"], GitSystem(), allowSystem: false);
        Assert.False(plan.SystemChanged);
        Assert.Equal(GitSystem(), plan.System);
        Assert.Contains($@"{SysGit}\cmd", plan.SystemConflicts);
    }

    [Fact]
    public void Git_with_system_permission_goes_into_system_path_before_the_competitor()
    {
        var planner = GitPlanner();
        var plan = planner.PlanActivation(PortGit, [@"C:\Tools", $@"{PortGit}\cmd"], GitSystem(), allowSystem: true);
        Assert.True(plan.SystemChanged);
        Assert.Equal([@"%SystemRoot%\system32", @"C:\Program Files\dotnet\", $@"{PortGit}\cmd", $@"{SysGit}\cmd"], plan.System);
        Assert.Equal([@"C:\Tools"], plan.User);
        var eff = planner.FindEffective(plan.User, plan.System);
        Assert.True(PathUtil.IsUnder(eff!.Value.Entry, PortGit));
    }

    [Fact]
    public void Git_switching_back_moves_installed_git_before_portable_in_system_path()
    {
        List<string> system = [@"%SystemRoot%\system32", $@"{PortGit}\cmd", $@"{SysGit}\cmd"];
        var plan = GitPlanner().PlanActivation(SysGit, [], system, allowSystem: true);
        Assert.Equal([@"%SystemRoot%\system32", $@"{SysGit}\cmd", $@"{PortGit}\cmd"], plan.System);
        Assert.False(plan.UserChanged);
    }

    [Fact]
    public void Git_already_first_needs_no_change()
    {
        var plan = GitPlanner().PlanActivation(SysGit, [], GitSystem(), allowSystem: true);
        Assert.False(plan.SystemChanged);
        Assert.False(plan.UserChanged);
    }

    [Fact]
    public void System_path_is_searched_before_user_path()
    {
        var eff = GitPlanner().FindEffective([$@"{PortGit}\cmd"], GitSystem());
        Assert.Equal(PathScope.System, eff!.Value.Scope);
        Assert.True(PathUtil.IsUnder(eff.Value.Entry, SysGit));
    }
}
