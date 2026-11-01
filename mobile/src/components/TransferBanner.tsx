import { Pressable, StyleSheet, Text, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';

import { t } from '@/i18n';
import { useTransfers } from '@/store/transfers';
import { useColors } from '@/theme';
import { Icon, ProgressBar } from './ui';

/** Floating progress for uploads (#upload) and downloads (#84). */
export function TransferBanner() {
  const items = useTransfers((s) => s.items);
  const c = useColors();
  const insets = useSafeAreaInsets();
  if (items.length === 0) return null;
  return (
    <View style={[styles.wrap, { top: insets.top + 6 }]} pointerEvents="box-none">
      {items.map((x) => (
        <View key={x.id} style={[styles.card, { backgroundColor: c.surface, borderColor: c.border }]}>
          <View style={styles.row}>
            <Icon name={x.kind === 'upload' ? 'cloud-upload-outline' : 'cloud-download-outline'} size={20} color={c.primary} />
            <Text numberOfLines={1} style={{ flex: 1, color: c.text, marginHorizontal: 8, fontSize: 13 }}>
              {x.label || x.name}
            </Text>
            <Text style={{ color: c.textMuted, fontSize: 12 }}>{x.progress >= 0 ? t('upload.progress', { percent: Math.round(x.progress * 100) }) : ''}</Text>
            {x.cancel ? (
              <Pressable onPress={x.cancel} hitSlop={10} style={{ marginLeft: 8 }} accessibilityLabel={t('upload.cancel')}>
                <Icon name="close-circle" size={20} color={c.textMuted} />
              </Pressable>
            ) : null}
          </View>
          <View style={{ marginTop: 6 }}>
            <ProgressBar value={x.progress >= 0 ? x.progress : 0} />
          </View>
        </View>
      ))}
    </View>
  );
}

const styles = StyleSheet.create({
  wrap: { position: 'absolute', left: 12, right: 12, zIndex: 50, gap: 6 },
  card: {
    borderRadius: 12, borderWidth: StyleSheet.hairlineWidth, padding: 10, elevation: 5,
    shadowColor: '#000', shadowOpacity: 0.2, shadowRadius: 6, shadowOffset: { width: 0, height: 2 },
  },
  row: { flexDirection: 'row', alignItems: 'center' },
});
