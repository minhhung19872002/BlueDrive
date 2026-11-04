namespace BlueDrive.Domain.Enums;

// Storage and permission model (docs/migration-permissions.md). Stored as snake_case strings like the other enums here.

public enum SpaceStatus { Active, Archived, Deleted }

public enum SecurableKind { Space, Drive, Library, Folder, File, Shortcut }

public enum PrincipalKind { User, Group, OrgUnit, OrgUnitTree, SpaceGroup, Everyone, Guest, Link, Role }

public enum SpaceGroupKind { Owners, Members, Contributors, Approvers, Visitors, Custom }

public enum AclSource { Direct, SpaceDefault, Link, Inherited }

public enum AccessRequestStatus { Pending, Approved, Rejected, Expired }

/// <summary>Who a sharing link works for (spec 5.3): people who already have access, anyone signed in, listed people, anyone.</summary>
public enum ShareLinkScope { Existing, Organization, Specific, Anyone }

/// <summary>How far sharing may go outside the organization (spec 5.4), from strictest to widest; the strictest tier wins.</summary>
public enum ExternalSharing { Off, ExistingGuests, NewAndExistingGuests, Anyone }

/// <summary>Who decides access requests in a Space: whoever manages the item's permissions, or only the Space's owners.</summary>
public enum AccessRequestDeciders { ItemManagers, SpaceOwners }
