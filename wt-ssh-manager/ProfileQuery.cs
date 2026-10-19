namespace WtSshManager;

public static class ProfileQuery
{
    public static List<SshProfile> Filter(
        IEnumerable<SshProfile> profiles,
        string? searchText,
        string? selectedGroup,
        bool favoritesOnly)
    {
        var search = searchText?.Trim() ?? string.Empty;
        var group = selectedGroup ?? string.Empty;

        return profiles
            .Where(profile =>
            {
                var matchesSearch = search.Length == 0 ||
                    profile.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                    profile.Host.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                    profile.Username.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                    profile.Group.Contains(search, StringComparison.CurrentCultureIgnoreCase);
                var matchesGroup = group.Length == 0 ||
                    string.Equals(profile.Group, group, StringComparison.OrdinalIgnoreCase);
                return matchesSearch && matchesGroup && (!favoritesOnly || profile.IsFavorite);
            })
            .OrderByDescending(profile => profile.IsFavorite)
            .ThenBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}

