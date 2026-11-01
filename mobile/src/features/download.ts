import { Directory, File, Paths } from 'expo-file-system';
import * as MediaLibrary from 'expo-media-library';
import * as Sharing from 'expo-sharing';
import { Platform } from 'react-native';

import { absoluteUrl, errorMessage, USER_AGENT } from '@/api/client';
import { driveApi } from '@/api/endpoints';
import { t } from '@/i18n';
import { toast } from '@/store/toast';
import { newTransferId, useTransfers } from '@/store/transfers';
import { isGalleryMedia } from '@/utils/format';
import type { MediaType } from '@/api/types';

function safeName(name: string): string {
  const cleaned = name.replace(/[\\/:*?"<>|\u0000-\u001f]/g, '_').trim();
  return cleaned || 'tep-tai-xuong';
}

/** Downloads a file version into the app cache through the signed content URL (no auth header needed). */
async function downloadToCache(fileId: string, onName?: (n: string) => void): Promise<{ file: File; mimeType: string; name: string }> {
  const info = await driveApi.downloadUrl(fileId, false);
  const name = safeName(info.fileName);
  onName?.(name);
  const dir = new Directory(Paths.cache, 'downloads', newTransferId());
  dir.create({ intermediates: true, idempotent: true });
  const dest = new File(dir, name);
  const transferId = newTransferId();
  const transfers = useTransfers.getState();
  const controller = new AbortController();
  transfers.start({
    id: transferId, kind: 'download', name, progress: 0, label: t('download.downloading', { name, percent: 0 }),
    cancel: () => controller.abort(),
  });
  try {
    const task = File.createDownloadTask(absoluteUrl(info.url)!, dest, {
      headers: { 'User-Agent': USER_AGENT },
      sessionType: 'foreground',
      signal: controller.signal,
      onProgress: ({ bytesWritten, totalBytes }) => {
        const total = totalBytes > 0 ? totalBytes : info.sizeBytes;
        const p = total > 0 ? bytesWritten / total : -1;
        useTransfers.getState().update(transferId, {
          progress: p,
          label: t('download.downloading', { name, percent: p >= 0 ? Math.round(p * 100) : '…' }),
        });
      },
    });
    const file = await task.downloadAsync();
    if (!file) throw new Error(t('common.error'));
    return { file, mimeType: info.mimeType, name };
  } finally {
    useTransfers.getState().finish(transferId);
  }
}

async function saveToGallery(file: File): Promise<boolean> {
  const perm = await MediaLibrary.requestPermissionsAsync(true, Platform.OS === 'android' ? ['photo', 'video'] : undefined);
  if (!perm.granted) return false;
  await MediaLibrary.Asset.create(file.uri);
  return true;
}

async function share(file: File, mimeType: string, name: string) {
  if (!(await Sharing.isAvailableAsync())) {
    toast.error(t('download.shareUnavailable'));
    return;
  }
  await Sharing.shareAsync(file.uri, { mimeType, dialogTitle: name });
}

/**
 * #84 Tải xuống về thiết bị: ảnh/video → Thư viện ảnh (Gallery); tệp khác → bảng chia sẻ hệ thống
 * (Lưu vào Tệp/Files, Google Drive, Zalo...).
 */
export async function downloadFile(item: { id: string; name: string; mediaType?: MediaType | null; extension?: string | null; mimeType?: string | null }) {
  try {
    const { file, mimeType, name } = await downloadToCache(item.id);
    if (isGalleryMedia({ ...item, mimeType: item.mimeType ?? mimeType })) {
      try {
        if (await saveToGallery(file)) {
          toast.success(t('download.savedGallery', { name }));
          return;
        }
        toast.info(t('download.permissionDenied'));
      } catch {
        // Unsupported format for the gallery (e.g. some video containers) → share sheet instead.
      }
    }
    await share(file, mimeType, name);
  } catch (e) {
    if ((e as Error)?.name === 'AbortError') return;
    toast.error(t('download.failed', { error: errorMessage(e) }));
  }
}

/** Download to cache then always open the share sheet ("Lưu/Chia sẻ tới ứng dụng khác"). */
export async function downloadAndShare(item: { id: string; name: string }) {
  try {
    const { file, mimeType, name } = await downloadToCache(item.id);
    await share(file, mimeType, name);
  } catch (e) {
    if ((e as Error)?.name === 'AbortError') return;
    toast.error(t('download.failed', { error: errorMessage(e) }));
  }
}
