import { useState } from 'react';
import { Pressable, Share, StyleSheet, Switch, Text, View } from 'react-native';

import { absoluteUrl, errorMessage } from '@/api/client';
import { driveApi } from '@/api/endpoints';
import type { DriveItem } from '@/api/types';
import { t } from '@/i18n';
import { toast } from '@/store/toast';
import { useColors } from '@/theme';
import { Dialog } from './Sheet';
import { Button } from './ui';

const EXPIRY_OPTIONS = [0, 1, 7, 30] as const;

function ShareLinkBody({ item, onClose }: { item: DriveItem; onClose: () => void }) {
  const c = useColors();
  const [allowDownload, setAllowDownload] = useState(true);
  const [days, setDays] = useState<number>(7);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const create = async () => {
    setBusy(true);
    setError(null);
    try {
      const expiresAt = days > 0 ? new Date(Date.now() + days * 86_400_000).toISOString() : null;
      const link = await driveApi.createShareLink(item.type, item.id, allowDownload, expiresAt);
      // App:PublicBaseUrl may be unset on the server → the URL is relative; prefix the server address.
      const url = absoluteUrl(link.url) ?? link.url;
      const message = t('share.message', { name: item.name, url });
      onClose();
      toast.success(t('share.created'));
      await Share.share({ message, title: item.name });
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      <Text numberOfLines={2} style={{ color: c.textMuted, marginBottom: 12 }}>
        {item.name}
      </Text>
      <View style={styles.row}>
        <Text style={{ color: c.text, fontSize: 15, flex: 1 }}>{t('share.allowDownload')}</Text>
        <Switch value={allowDownload} onValueChange={setAllowDownload} trackColor={{ true: c.primary }} />
      </View>
      <Text style={{ color: c.textMuted, fontSize: 13, marginBottom: 6 }}>{t('share.expires')}</Text>
      <View style={styles.chips}>
        {EXPIRY_OPTIONS.map((d) => (
          <Pressable
            key={d}
            onPress={() => setDays(d)}
            style={[styles.chip, { borderColor: days === d ? c.primary : c.border, backgroundColor: days === d ? c.primary : 'transparent' }]}
          >
            <Text style={{ color: days === d ? c.primaryText : c.text }}>{d === 0 ? t('share.never') : t('share.days', { n: d })}</Text>
          </Pressable>
        ))}
      </View>
      {error ? <Text style={{ color: c.danger, marginTop: 10 }}>{error}</Text> : null}
      <View style={styles.actions}>
        <Button title={t('common.cancel')} variant="ghost" onPress={onClose} />
        <Button title={t('share.create')} onPress={create} loading={busy} icon="share-variant" />
      </View>
    </>
  );
}

/** #53: tạo liên kết chia sẻ rồi mở bảng chia sẻ của hệ thống. */
export function ShareLinkDialog({ item, onClose }: { item: DriveItem | null; onClose: () => void }) {
  return (
    <Dialog visible={!!item} onClose={onClose} title={t('share.title')}>
      {item ? <ShareLinkBody key={item.id} item={item} onClose={onClose} /> : null}
    </Dialog>
  );
}

const styles = StyleSheet.create({
  row: { flexDirection: 'row', alignItems: 'center', marginBottom: 14 },
  chips: { flexDirection: 'row', flexWrap: 'wrap', gap: 8 },
  chip: { borderWidth: 1, borderRadius: 16, paddingHorizontal: 12, paddingVertical: 6 },
  actions: { flexDirection: 'row', justifyContent: 'flex-end', gap: 10, marginTop: 18 },
});
