namespace NtfyBar.Core;

/// <summary>Topics grouped by app for menus and lists: apps by name, then user-added topics.</summary>
public static class TopicGroups
{
    public sealed record Group(string? Header, List<TopicConfig> Topics);

    public static List<Group> Build(IEnumerable<TopicConfig> topics)
    {
        var list = topics.ToList();
        var byApp = list.Where(t => t.AppName is not null)
            .GroupBy(t => t.AppName!)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new Group(g.Key, g.ToList()))
            .ToList();
        var rest = list.Where(t => t.AppName is null).ToList();
        if (rest.Count > 0) byApp.Add(new Group(byApp.Count > 0 ? "Other topics" : null, rest));
        return byApp;
    }
}
