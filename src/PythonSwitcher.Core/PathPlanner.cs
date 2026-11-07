namespace PythonSwitcher.Core;

public enum PathScope
{
    Benutzer,
    System,
}

/// <summary>Ergebnis einer Planung: die neuen PATH-Listen und was dabei aufgefallen ist.</summary>
public sealed class PathPlan
{
    public required List<string> User { get; init; }
    public required List<string> System { get; init; }
    public bool UserChanged { get; init; }
    public bool SystemChanged { get; init; }

    /// <summary>
    /// Einträge im System-PATH, die vor der gewünschten Installation gelesen werden,
    /// aber nicht verschoben wurden (weil Änderungen am System-PATH nicht erlaubt waren).
    /// </summary>
    public IReadOnlyList<string> SystemConflicts { get; init; } = [];
}

/// <summary>
/// Reine Planungslogik ohne Zugriff auf Registry oder Dateisystem (bis auf die übergebene Dateiprüfung).
/// Windows liest erst den System-PATH, dann den Benutzer-PATH; der erste Treffer gewinnt.
/// </summary>
public sealed class PathPlanner
{
    private readonly ToolDefinition _tool;
    private readonly Func<string, bool> _fileExists;

    public PathPlanner(ToolDefinition tool, Func<string, bool> fileExists)
    {
        _tool = tool;
        _fileExists = fileExists;
    }

    /// <summary>Gehört der Eintrag zu einer anderen Installation desselben Werkzeugs?</summary>
    public bool IsCompetitor(string entry, string root)
    {
        if (PathUtil.IsUnder(entry, root))
            return false;
        if (_tool.IsAlwaysCompetitor(entry))
            return true;
        var dir = PathUtil.Expand(entry);
        if (_tool.Executables.Any(exe => _fileExists(Path.Combine(dir, exe))))
            return true;
        // Auch Unterordner einer anderen Installation (z. B. deren "Scripts") zählen als Konkurrenz.
        return _tool.FindRoot(dir, _fileExists) is not null;
    }

    /// <summary>Liefert den Eintrag, über den Windows das Hauptprogramm zuerst findet, und seinen Bereich.</summary>
    public (string Entry, PathScope Scope)? FindEffective(IReadOnlyList<string> user, IReadOnlyList<string> system)
    {
        foreach (var (list, scope) in new[] { (system, PathScope.System), (user, PathScope.Benutzer) })
        {
            foreach (var entry in list)
            {
                if (_tool.IsAlwaysCompetitor(entry) ||
                    _fileExists(Path.Combine(PathUtil.Expand(entry), _tool.MainExecutable)))
                    return (entry, scope);
            }
        }
        return null;
    }

    /// <summary>
    /// Plant das Aktivieren: die Einträge der Installation wandern geschlossen vor den ersten Konkurrenten.
    /// Alle anderen Einträge behalten ihre Reihenfolge. Fehlende Pflichteinträge werden ergänzt.
    /// </summary>
    public PathPlan PlanActivation(string root, IReadOnlyList<string> user, IReadOnlyList<string> system, bool allowSystem)
    {
        var newUser = user.ToList();
        var newSystem = system.ToList();
        bool userChanged = false, systemChanged = false;
        var conflicts = new List<string>();

        int firstOwnSys = newSystem.FindIndex(e => PathUtil.IsUnder(e, root));
        int firstCompSys = newSystem.FindIndex(e => IsCompetitor(e, root));

        if (firstOwnSys >= 0)
        {
            // Die Installation steht bereits im System-PATH: dort einordnen.
            if (firstCompSys >= 0 && firstCompSys < firstOwnSys)
            {
                if (allowSystem)
                {
                    newSystem = MoveBlockBefore(newSystem, root, firstCompSys, ExtraEntries(root, newSystem.Concat(newUser)));
                    systemChanged = true;
                }
                else
                {
                    conflicts.AddRange(newSystem.Take(firstOwnSys).Where(e => IsCompetitor(e, root)));
                }
            }
            return new PathPlan
            {
                User = newUser, System = newSystem,
                UserChanged = userChanged, SystemChanged = systemChanged,
                SystemConflicts = conflicts,
            };
        }

        // Normalfall: im Benutzer-PATH vor den ersten Konkurrenten setzen.
        var before = PathUtil.Join(newUser);
        newUser = PlaceInUser(newUser, root);
        userChanged = PathUtil.Join(newUser) != before;

        if (firstCompSys >= 0)
        {
            if (allowSystem)
            {
                // Ein Konkurrent im System-PATH gewinnt immer gegen den Benutzer-PATH.
                // Deshalb wandern die Einträge der Installation in den System-PATH vor diesen Konkurrenten.
                var block = newUser.Where(e => PathUtil.IsUnder(e, root)).Select(PathUtil.Expand).ToList();
                newUser = newUser.Where(e => !PathUtil.IsUnder(e, root)).ToList();
                newSystem.InsertRange(firstCompSys, block);
                userChanged = PathUtil.Join(newUser) != PathUtil.Join(user);
                systemChanged = true;
            }
            else
            {
                conflicts.AddRange(newSystem.Where(e => IsCompetitor(e, root)));
            }
        }

        return new PathPlan
        {
            User = newUser, System = newSystem,
            UserChanged = userChanged, SystemChanged = systemChanged,
            SystemConflicts = conflicts,
        };
    }

    /// <summary>
    /// Plant das Eintragen einer neuen Installation, ohne sie zu aktivieren:
    /// direkt hinter die Einträge der aktiven Installation im Benutzer-PATH.
    /// Gibt es keine aktive Installation im Benutzer-PATH, wird die neue aktiv (vor den ersten Konkurrenten).
    /// </summary>
    public PathPlan PlanAddInactive(string root, string? activeRoot, IReadOnlyList<string> user, IReadOnlyList<string> system)
    {
        var newUser = user.Where(e => !PathUtil.IsUnder(e, root)).ToList();
        var block = MissingRequired(root, newUser.Concat(system), keepOrderFrom: user.Where(e => PathUtil.IsUnder(e, root)));

        int lastActive = activeRoot is null ? -1 : newUser.FindLastIndex(e => PathUtil.IsUnder(e, activeRoot));
        if (lastActive >= 0)
        {
            newUser.InsertRange(lastActive + 1, block);
        }
        else
        {
            newUser = user.ToList();
            newUser = PlaceInUser(newUser, root);
        }

        return new PathPlan
        {
            User = newUser, System = system.ToList(),
            UserChanged = PathUtil.Join(newUser) != PathUtil.Join(user),
            SystemChanged = false,
        };
    }

    /// <summary>Plant das Herausnehmen aller Einträge einer Installation.</summary>
    public PathPlan PlanRemoval(string root, IReadOnlyList<string> user, IReadOnlyList<string> system, bool allowSystem)
    {
        var newUser = user.Where(e => !PathUtil.IsUnder(e, root)).ToList();
        var newSystem = system.ToList();
        var conflicts = new List<string>();
        bool systemChanged = false;
        if (system.Any(e => PathUtil.IsUnder(e, root)))
        {
            if (allowSystem)
            {
                newSystem = system.Where(e => !PathUtil.IsUnder(e, root)).ToList();
                systemChanged = true;
            }
            else
            {
                conflicts.AddRange(system.Where(e => PathUtil.IsUnder(e, root)));
            }
        }
        return new PathPlan
        {
            User = newUser, System = newSystem,
            UserChanged = newUser.Count != user.Count,
            SystemChanged = systemChanged,
            SystemConflicts = conflicts,
        };
    }

    private List<string> PlaceInUser(List<string> user, string root)
    {
        int firstOwn = user.FindIndex(e => PathUtil.IsUnder(e, root));
        int firstComp = user.FindIndex(e => IsCompetitor(e, root));
        var extra = ExtraEntries(root, user);

        if (firstOwn >= 0 && (firstComp < 0 || firstOwn < firstComp))
        {
            // Steht schon vorn: nur fehlende Pflichteinträge ergänzen, und zwar am Block.
            if (extra.Count == 0)
                return user;
            return MoveBlockBefore(user, root, firstOwn, extra);
        }

        if (firstComp >= 0)
            return MoveBlockBefore(user, root, firstComp, extra);

        // Kein Konkurrent und noch nicht eingetragen: hinten anfügen.
        var result = user.ToList();
        result.AddRange(extra);
        return result;
    }

    /// <summary>
    /// Nimmt alle Einträge unter root heraus und setzt sie geschlossen vor den Eintrag, der vorher an
    /// Position "anchorIndex" stand. Zusätzliche Einträge werden passend in den Block einsortiert.
    /// </summary>
    private List<string> MoveBlockBefore(List<string> list, string root, int anchorIndex, List<string> extra)
    {
        var anchor = list[anchorIndex];
        var own = list.Where(e => PathUtil.IsUnder(e, root)).ToList();
        var rest = list.Where(e => !PathUtil.IsUnder(e, root)).ToList();
        var block = OrderBlock(root, own, extra);

        int insertAt;
        if (PathUtil.IsUnder(anchor, root))
        {
            // Anker gehört selbst zur Installation: Position des ersten fremden Eintrags danach.
            int ownBefore = list.Take(anchorIndex).Count(e => PathUtil.IsUnder(e, root));
            insertAt = anchorIndex - ownBefore;
        }
        else
        {
            insertAt = rest.IndexOf(anchor);
        }
        rest.InsertRange(insertAt, block);
        return rest;
    }

    /// <summary>Pflichteinträge stehen in ihrer festgelegten Reihenfolge vorn, weitere eigene Einträge dahinter.</summary>
    private List<string> OrderBlock(string root, List<string> own, List<string> extra)
    {
        var required = _tool.RequiredEntries(root);
        var block = new List<string>();
        foreach (var req in required)
        {
            var existing = own.FirstOrDefault(e => PathUtil.SameDirectory(e, req))
                           ?? extra.FirstOrDefault(e => PathUtil.SameDirectory(e, req));
            if (existing != null)
                block.Add(existing);
        }
        block.AddRange(own.Where(e => !block.Contains(e)));
        return block;
    }

    private List<string> ExtraEntries(string root, IEnumerable<string> existing)
    {
        var list = existing.ToList();
        return _tool.RequiredEntries(root)
            .Where(req => !list.Any(e => PathUtil.SameDirectory(e, req)))
            .ToList();
    }

    private List<string> MissingRequired(string root, IEnumerable<string> elsewhere, IEnumerable<string> keepOrderFrom)
    {
        var own = keepOrderFrom.ToList();
        var extra = ExtraEntries(root, own.Concat(elsewhere));
        return OrderBlock(root, own, extra);
    }
}
