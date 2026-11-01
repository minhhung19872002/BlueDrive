import type { ComponentProps } from 'react';
import type MaterialCommunityIcons from '@expo/vector-icons/MaterialCommunityIcons';

import type { DriveItem, MediaType } from '@/api/types';
import { t } from '@/i18n';

export function formatBytes(bytes: number | null | undefined): string {
  if (bytes == null || !Number.isFinite(bytes)) return '';
  if (bytes < 1024) return `${bytes} B`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let v = bytes / 1024;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  return `${v.toLocaleString('vi-VN', { maximumFractionDigits: v < 10 ? 1 : 0 })} ${units[i]}`;
}

/** Server dates are UTC ISO; tolerate a missing "Z". */
export function parseDate(iso: string | null | undefined): Date | null {
  if (!iso) return null;
  const s = /[zZ]|[+-]\d\d:?\d\d$/.test(iso) ? iso : `${iso}Z`;
  const d = new Date(s);
  return Number.isNaN(d.getTime()) ? null : d;
}

const pad = (n: number) => String(n).padStart(2, '0');

/** Local time as DD/MM/YYYY HH:mm. */
export function formatDate(iso: string | null | undefined): string {
  const d = parseDate(iso);
  if (!d) return '';
  return `${pad(d.getDate())}/${pad(d.getMonth() + 1)}/${d.getFullYear()} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

export function formatDuration(seconds: number | null | undefined): string {
  if (!seconds || seconds < 0) return '';
  const s = Math.round(seconds);
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  return h > 0 ? `${h}:${pad(m)}:${pad(sec)}` : `${m}:${pad(sec)}`;
}

type IconName = ComponentProps<typeof MaterialCommunityIcons>['name'];

export function mediaIcon(mediaType: MediaType | null | undefined, ext?: string | null): IconName {
  switch (mediaType) {
    case 'image':
    case 'infographic':
      return 'file-image';
    case 'video':
      return 'file-video';
    case 'audio':
      return 'file-music';
    case 'pdf':
      return 'file-pdf-box';
    case 'document':
      return ext?.toLowerCase() === 'txt' ? 'file-document-outline' : 'file-word';
    case 'spreadsheet':
      return 'file-excel';
    case 'presentation':
      return 'file-powerpoint';
    case 'archive':
      return 'folder-zip';
    case 'epub':
      return 'book-open-variant';
    case 'design':
      return 'palette';
    default:
      return 'file-outline';
  }
}

export function mediaColor(mediaType: MediaType | null | undefined): string {
  switch (mediaType) {
    case 'image':
    case 'infographic':
      return '#8e24aa';
    case 'video':
      return '#d81b60';
    case 'audio':
      return '#00897b';
    case 'pdf':
      return '#e53935';
    case 'document':
      return '#1e88e5';
    case 'spreadsheet':
      return '#43a047';
    case 'presentation':
      return '#fb8c00';
    case 'archive':
      return '#6d4c41';
    default:
      return '#78909c';
  }
}

export function mediaTypeLabel(mediaType: MediaType | null | undefined): string {
  return t(`mediaType.${mediaType ?? 'other'}` as 'mediaType.other');
}

/** What the in-app viewer can do with a file. */
export type PreviewKind = 'image' | 'video' | 'audio' | 'office' | 'text' | 'none';

const TEXT_EXT = new Set(['txt', 'md', 'csv', 'log', 'json', 'xml', 'yml', 'yaml', 'ini', 'conf', 'srt', 'vtt', 'tsv']);
const OFFICE_EXT = new Set(['doc', 'docx', 'xls', 'xlsx', 'ppt', 'pptx', 'pdf', 'odt', 'ods', 'odp', 'rtf', 'epub', 'csv', 'txt']);
const IMAGE_EXT = new Set(['jpg', 'jpeg', 'png', 'gif', 'webp', 'bmp', 'heic', 'heif']);

export function previewKind(item: { mediaType?: MediaType | null; extension?: string | null; mimeType?: string | null }): PreviewKind {
  const ext = (item.extension ?? '').toLowerCase().replace(/^\./, '');
  const mime = (item.mimeType ?? '').toLowerCase();
  if (item.mediaType === 'image' || item.mediaType === 'infographic' || IMAGE_EXT.has(ext)) return ext === 'svg' || mime.includes('svg') ? 'none' : 'image';
  if (item.mediaType === 'video' || mime.startsWith('video/')) return 'video';
  if (item.mediaType === 'audio' || mime.startsWith('audio/')) return 'audio';
  if (TEXT_EXT.has(ext) && ext !== 'csv') return 'text';
  if (OFFICE_EXT.has(ext)) return 'office';
  if (mime.startsWith('text/')) return 'text';
  return 'none';
}

export function isGalleryMedia(item: { mediaType?: MediaType | null; extension?: string | null; mimeType?: string | null }): boolean {
  const k = previewKind(item);
  return k === 'image' || k === 'video';
}

export function itemSubtitle(item: DriveItem): string {
  const date = formatDate(item.updatedAt ?? item.createdAt);
  if (item.type === 'folder') return [t('mediaType.folder'), date].filter(Boolean).join(' • ');
  return [formatBytes(item.sizeBytes), date].filter(Boolean).join(' • ');
}
