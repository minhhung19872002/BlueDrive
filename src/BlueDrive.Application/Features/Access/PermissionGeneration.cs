namespace BlueDrive.Application.Features.Access;

/// <summary>
/// A process-wide counter of changes that can alter someone's rights (ACLs, memberships, the tree, users, roles, labels, Spaces, links).
/// The persistence layer bumps it when this process writes one of those tables (after the statement, and again after the commit of a
/// transaction that did). The permission engine keeps whole decisions in memory stamped with the counter: an unchanged counter means
/// nothing relevant was written since, so the decision is answered without a database round trip (spec 4.5: <c>Has()</c> &lt; 1 ms).
/// Writes made by another process (the Worker) are seen when the decision's short lifetime ends.
/// </summary>
public static class PermissionGeneration
{
    private static long _value;

    public static long Current => Interlocked.Read(ref _value);

    public static void Bump() => Interlocked.Increment(ref _value);

    /// <summary>Tables whose writes can change a permission decision.</summary>
    public static readonly IReadOnlySet<string> Tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "securables", "acl_entries", "acl_scopes", "scope_grants", "principals", "principal_memberships", "membership_versions", "permission_levels",
        "folders", "files", "workspaces", "workspace_members", "workspace_group_members", "space_groups", "space_group_members", "sensitivity_labels",
        "users", "user_roles", "roles", "role_permissions", "user_groups", "user_group_members", "group_nestings", "organizations", "guests",
        "share_links", "share_link_members", "library_settings", "resource_permissions", "shares", "system_settings",
    };
}
