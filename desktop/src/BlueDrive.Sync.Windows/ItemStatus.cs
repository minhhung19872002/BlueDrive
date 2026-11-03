using Windows.Storage;
using Windows.Storage.Provider;

namespace BlueDrive.Sync.Windows;

/// <summary>
/// BlueDrive's own state of an item in Explorer (spec 7.2, <c>StorageProviderItemProperties</c>): a text with an icon next to the sync
/// status — co-editing, conflict, sync error. Works for the sync roots registered by <see cref="SyncRootRegistrar"/> (property 1 declared).
/// </summary>
public static class ItemStatus
{
    public const int PropertyId = 1;

    /// <summary>Sets (or with null text clears) the state of the file or folder at <paramref name="path"/>.</summary>
    public static async Task SetAsync(string path, string? text, string iconResource)
    {
        IStorageItem item = Directory.Exists(path)
            ? await StorageFolder.GetFolderFromPathAsync(path)
            : await StorageFile.GetFileFromPathAsync(path);
        var props = text is null
            ? Array.Empty<StorageProviderItemProperty>()
            : [new StorageProviderItemProperty { Id = PropertyId, Value = text, IconResource = iconResource }];
        await StorageProviderItemProperties.SetAsync(item, props);
    }
}
