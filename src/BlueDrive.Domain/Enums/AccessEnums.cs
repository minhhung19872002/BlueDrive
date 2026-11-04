namespace BlueDrive.Domain.Enums;

// Storage and permission model (docs/migration-permissions.md). Stored as snake_case strings like the other enums here.

public enum SpaceStatus { Active, Archived, Deleted }

public enum SecurableKind { Space, Drive, Library, Folder, File, Shortcut }

public enum PrincipalKind { User, Group, OrgUnit, OrgUnitTree, SpaceGroup, Everyone, Guest, Link, Role }

public enum SpaceGroupKind { Owners, Members, Contributors, Approvers, Visitors, Custom }

public enum AclSource { Direct, SpaceDefault, Link, Inherited }
