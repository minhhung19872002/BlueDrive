import type { DriveItem, FileDetail } from '@/api/types';

/** Converts a file detail into the list item shape used by the action sheet. */
export function detailToItem(f: FileDetail): DriveItem {
  return {
    id: f.id, type: 'file', name: f.name, workspaceId: f.workspaceId, parentId: f.folderId, sizeBytes: f.sizeBytes, mediaType: f.mediaType,
    extension: f.extension, mimeType: f.mimeType, createdAt: f.createdAt, updatedAt: f.updatedAt, createdBy: f.createdBy, createdByName: f.createdByName,
    isStarred: f.isStarred, hasThumbnail: f.hasThumbnail, approvalStatus: f.approvalStatus,
    versionNo: f.versions.find((v) => v.isCurrent)?.versionNo ?? null, title: f.title, tags: f.tags, workspaceName: f.workspaceName, thumbnailUrl: f.thumbnailUrl,
  };
}
