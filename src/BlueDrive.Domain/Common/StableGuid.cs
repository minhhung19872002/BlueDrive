using System.Security.Cryptography;
using System.Text;

namespace BlueDrive.Domain.Common;

/// <summary>
/// Name-based GUIDs (RFC 9562 version 5): the same inputs always give the same id, so rows derived from other rows (the Library of a
/// workspace, the Space groups, the "unit and sub-units" principal) keep their id when the permission model is rebuilt.
/// </summary>
public static class StableGuid
{
    private static readonly Guid Namespace = new("6f1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d");

    public static Guid From(Guid owner, string purpose)
    {
        var name = Encoding.UTF8.GetBytes($"{owner:N}:{purpose}");
        var ns = Namespace.ToByteArray(bigEndian: true);
        var hash = SHA1.HashData([.. ns, .. name]);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // version 5
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC variant
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>"Mọi người trong đơn vị" (spec 2: everyone_in_tenant).</summary>
    public static readonly Guid Everyone = new("0193f000-0000-7000-8000-0000000000e1");

    /// <summary>The single tenant (docs/migration-permissions.md, assumption 1).</summary>
    public static readonly Guid Tenant = new("0193f000-0000-7000-8000-0000000000a1");
}
