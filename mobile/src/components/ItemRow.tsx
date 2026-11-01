import { Image } from 'expo-image';
import { memo, type ReactNode } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';

import { absoluteUrl } from '@/api/client';
import type { DriveItem } from '@/api/types';
import { t } from '@/i18n';
import { useColors } from '@/theme';
import { itemSubtitle, mediaColor, mediaIcon } from '@/utils/format';
import { Icon } from './ui';

export function ItemThumb({ item, size = 44 }: { item: Pick<DriveItem, 'type' | 'mediaType' | 'extension' | 'thumbnailUrl'>; size?: number }) {
  const c = useColors();
  if (item.type === 'folder') {
    return (
      <View style={[styles.thumb, { width: size, height: size, backgroundColor: c.surfaceAlt }]}>
        <Icon name="folder" size={size * 0.62} color={c.folder} />
      </View>
    );
  }
  const thumb = absoluteUrl(item.thumbnailUrl);
  if (thumb) {
    return (
      <View style={[styles.thumb, { width: size, height: size, backgroundColor: c.surfaceAlt }]}>
        <Image source={{ uri: thumb }} style={{ width: size, height: size }} contentFit="cover" transition={150} cachePolicy="memory-disk" />
        {item.mediaType === 'video' ? (
          <View style={styles.playBadge}>
            <Icon name="play" size={12} color="#fff" />
          </View>
        ) : null}
      </View>
    );
  }
  return (
    <View style={[styles.thumb, { width: size, height: size, backgroundColor: c.surfaceAlt }]}>
      <Icon name={mediaIcon(item.mediaType, item.extension)} size={size * 0.6} color={mediaColor(item.mediaType)} />
    </View>
  );
}

interface Props {
  item: DriveItem;
  onPress: (item: DriveItem) => void;
  onMenu: (item: DriveItem) => void;
  /** Replaces the default name text (e.g. search highlights). */
  title?: ReactNode;
  subtitle?: string;
}

export const ItemRow = memo(function ItemRow({ item, onPress, onMenu, title, subtitle }: Props) {
  const c = useColors();
  const pending = item.type === 'file' && item.approvalStatus && item.approvalStatus !== 'approved';
  return (
    <Pressable
      onPress={() => onPress(item)}
      onLongPress={() => onMenu(item)}
      delayLongPress={350}
      style={({ pressed }) => [styles.row, { backgroundColor: pressed ? c.surfaceAlt : c.surface, borderBottomColor: c.border }]}
      accessibilityLabel={item.name}
    >
      <ItemThumb item={item} />
      <View style={styles.body}>
        {title ?? (
          <Text numberOfLines={1} style={[styles.name, { color: c.text }]}>
            {item.name}
          </Text>
        )}
        <View style={styles.subRow}>
          {item.isStarred ? <Icon name="star" size={14} color={c.star} style={{ marginRight: 4 }} /> : null}
          {pending ? (
            <Text style={[styles.badge, { color: c.warning, borderColor: c.warning }]}>{t(`approval.${item.approvalStatus!}`)}</Text>
          ) : null}
          <Text numberOfLines={1} style={[styles.sub, { color: c.textMuted }]}>
            {subtitle ?? itemSubtitle(item)}
          </Text>
        </View>
      </View>
      <Pressable hitSlop={12} onPress={() => onMenu(item)} style={styles.menu} accessibilityLabel={t('actions.title')}>
        <Icon name="dots-vertical" color={c.textMuted} />
      </Pressable>
    </Pressable>
  );
});

const styles = StyleSheet.create({
  row: { flexDirection: 'row', alignItems: 'center', paddingHorizontal: 14, paddingVertical: 10, borderBottomWidth: StyleSheet.hairlineWidth },
  thumb: { borderRadius: 8, alignItems: 'center', justifyContent: 'center', overflow: 'hidden' },
  playBadge: { position: 'absolute', right: 2, bottom: 2, backgroundColor: 'rgba(0,0,0,0.55)', borderRadius: 8, padding: 1 },
  body: { flex: 1, marginLeft: 12 },
  name: { fontSize: 15, fontWeight: '500' },
  subRow: { flexDirection: 'row', alignItems: 'center', marginTop: 3 },
  sub: { fontSize: 12, flexShrink: 1 },
  badge: { fontSize: 10, borderWidth: 1, borderRadius: 4, paddingHorizontal: 4, marginRight: 6 },
  menu: { padding: 6, marginLeft: 4 },
});
