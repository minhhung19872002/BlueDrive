import { useQuery } from '@tanstack/react-query';
import { router } from 'expo-router';
import { Pressable, RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';

import { errorMessage } from '@/api/client';
import { driveApi } from '@/api/endpoints';
import { ItemRow } from '@/components/ItemRow';
import { Card, EmptyState, ErrorState, Icon, ProgressBar, SectionHeader, type IconName } from '@/components/ui';
import { useItemActions } from '@/features/ItemActions';
import { useRefresh } from '@/hooks/useRefresh';
import { t } from '@/i18n';
import { useAuth } from '@/store/auth';
import { useColors } from '@/theme';
import { formatBytes } from '@/utils/format';
import { openItem } from '@/utils/navigation';

function Stat({ icon, label, value, onPress }: { icon: IconName; label: string; value?: number; onPress?: () => void }) {
  const c = useColors();
  return (
    <Pressable onPress={onPress} style={({ pressed }) => [styles.stat, { backgroundColor: c.surface, borderColor: c.border, opacity: pressed ? 0.8 : 1 }]}>
      <Icon name={icon} color={c.primary} />
      <Text style={[styles.statValue, { color: c.text }]}>{value ?? '–'}</Text>
      <Text numberOfLines={1} style={{ color: c.textMuted, fontSize: 12 }}>
        {label}
      </Text>
    </Pressable>
  );
}

/** Trang chủ: dung lượng (GET /dashboards/personal) + tài liệu gần đây. */
export default function HomeScreen() {
  const c = useColors();
  const user = useAuth((s) => s.user);
  const { openActions } = useItemActions();
  const dash = useQuery({ queryKey: ['dashboard'], queryFn: driveApi.dashboard });
  const recent = useQuery({ queryKey: ['recent', 10], queryFn: () => driveApi.recent(10) });
  const { refreshing, onRefresh } = useRefresh(dash.refetch, recent.refetch);

  const d = dash.data;
  const unlimited = d ? d.quotaBytes <= 0 : false;
  const pct = d && d.quotaBytes > 0 ? d.usedBytes / d.quotaBytes : 0;
  const barColor = pct > 0.9 ? c.danger : pct > 0.75 ? c.warning : c.primary;

  return (
    <ScrollView
      style={{ backgroundColor: c.background }}
      contentContainerStyle={{ paddingBottom: 24 }}
      refreshControl={<RefreshControl refreshing={refreshing} onRefresh={onRefresh} colors={[c.primary]} tintColor={c.primary} />}
    >
      <Text style={[styles.greeting, { color: c.text }]}>{t('home.greeting', { name: user?.fullName ?? '' })}</Text>
      {user?.organizationName ? <Text style={{ color: c.textMuted, paddingHorizontal: 16 }}>{user.organizationName}</Text> : null}

      <Card style={{ margin: 16 }}>
        <View style={{ flexDirection: 'row', alignItems: 'center', marginBottom: 10 }}>
          <Icon name="database-outline" color={c.primary} />
          <Text style={{ color: c.text, fontWeight: '600', marginLeft: 8, flex: 1 }}>{t('home.quota')}</Text>
          {d && !unlimited ? <Text style={{ color: barColor, fontWeight: '700' }}>{Math.round(pct * 100)}%</Text> : null}
        </View>
        {dash.isError ? (
          <Text style={{ color: c.danger }}>{errorMessage(dash.error)}</Text>
        ) : (
          <>
            <ProgressBar value={unlimited ? 0 : pct} color={barColor} height={10} />
            <Text style={{ color: c.textMuted, marginTop: 8, fontSize: 13 }}>
              {d
                ? unlimited
                  ? t('home.quotaUnlimited', { used: formatBytes(d.usedBytes) })
                  : t('home.quotaUsed', { used: formatBytes(d.usedBytes), total: formatBytes(d.quotaBytes) })
                : t('common.loading')}
            </Text>
          </>
        )}
      </Card>

      <View style={styles.stats}>
        <Stat icon="file-document-multiple-outline" label={t('home.myFiles')} value={d?.myFiles} onPress={() => router.navigate('/files')} />
        <Stat icon="account-multiple-outline" label={t('home.sharedWithMe')} value={d?.sharedWithMe} onPress={() => router.navigate('/shared')} />
        <Stat icon="star-outline" label={t('home.starred')} value={d?.starred} onPress={() => router.push('/starred')} />
      </View>

      <SectionHeader
        title={t('home.recent')}
        right={
          <Pressable onPress={() => router.push('/recent')} hitSlop={8}>
            <Text style={{ color: c.primary, fontWeight: '600' }}>{t('home.seeAll')}</Text>
          </Pressable>
        }
      />
      {recent.isError ? (
        <ErrorState message={errorMessage(recent.error)} onRetry={() => recent.refetch()} />
      ) : recent.data && recent.data.length === 0 ? (
        <EmptyState icon="history" title={t('home.noRecent')} />
      ) : (
        (recent.data ?? []).map((it) => <ItemRow key={it.id} item={it} onPress={openItem} onMenu={(x) => openActions(x)} />)
      )}
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  greeting: { fontSize: 22, fontWeight: '700', paddingHorizontal: 16, paddingTop: 16 },
  stats: { flexDirection: 'row', gap: 10, paddingHorizontal: 16 },
  stat: { flex: 1, borderRadius: 12, borderWidth: StyleSheet.hairlineWidth, padding: 12, alignItems: 'flex-start' },
  statValue: { fontSize: 20, fontWeight: '700', marginTop: 6 },
});
