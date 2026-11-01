import { useQuery } from '@tanstack/react-query';
import { Pressable, RefreshControl, SectionList, StyleSheet, Text, View } from 'react-native';

import { errorMessage } from '@/api/client';
import { driveApi } from '@/api/endpoints';
import type { Workspace } from '@/api/types';
import { EmptyState, ErrorState, Icon, LoadingView, ProgressBar, SectionHeader } from '@/components/ui';
import { useRefresh } from '@/hooks/useRefresh';
import { t } from '@/i18n';
import { useColors } from '@/theme';
import { formatBytes } from '@/utils/format';
import { openFolder } from '@/utils/navigation';

function WorkspaceRow({ w }: { w: Workspace }) {
  const c = useColors();
  const pct = w.quotaBytes > 0 ? w.usedBytes / w.quotaBytes : 0;
  return (
    <Pressable
      onPress={() => openFolder(w.id, null, w.name)}
      style={({ pressed }) => [styles.row, { backgroundColor: pressed ? c.surfaceAlt : c.surface, borderBottomColor: c.border }]}
    >
      <View style={[styles.avatar, { backgroundColor: c.surfaceAlt }]}>
        <Icon name={w.type === 'organization' ? 'office-building' : 'account-group'} color={c.primary} />
      </View>
      <View style={{ flex: 1, marginLeft: 12 }}>
        <View style={{ flexDirection: 'row', alignItems: 'center' }}>
          <Text numberOfLines={1} style={{ color: c.text, fontSize: 15, fontWeight: '600', flexShrink: 1 }}>
            {w.name}
          </Text>
          {w.isLocked ? (
            <View style={[styles.locked, { borderColor: c.warning }]}>
              <Icon name="lock" size={11} color={c.warning} />
              <Text style={{ color: c.warning, fontSize: 10, marginLeft: 2 }}>{t('groups.locked')}</Text>
            </View>
          ) : null}
        </View>
        <Text numberOfLines={1} style={{ color: c.textMuted, fontSize: 12, marginTop: 2 }}>
          {[w.organizationName, t('groups.members', { count: w.memberCount })].filter(Boolean).join(' • ')}
        </Text>
        {w.quotaBytes > 0 ? (
          <View style={{ marginTop: 6 }}>
            <ProgressBar value={pct} height={4} />
            <Text style={{ color: c.textMuted, fontSize: 11, marginTop: 2 }}>
              {t('groups.usage', { used: formatBytes(w.usedBytes), total: formatBytes(w.quotaBytes) })}
            </Text>
          </View>
        ) : null}
      </View>
      <Icon name="chevron-right" color={c.textMuted} />
    </Pressable>
  );
}

/** #74 Nhóm: danh sách nhóm tài nguyên / đơn vị → duyệt tệp của nhóm. */
export default function GroupsScreen() {
  const c = useColors();
  const groups = useQuery({ queryKey: ['workspaces', 'group'], queryFn: () => driveApi.workspaces('group') });
  const orgs = useQuery({ queryKey: ['workspaces', 'organization'], queryFn: () => driveApi.workspaces('organization') });
  const { refreshing, onRefresh } = useRefresh(groups.refetch, orgs.refetch);

  if (groups.isLoading || orgs.isLoading) return <LoadingView />;
  if (groups.isError && orgs.isError) return <ErrorState message={errorMessage(groups.error)} onRetry={onRefresh} />;

  const sections = [
    { title: t('groups.group'), data: groups.data ?? [] },
    { title: t('groups.organization'), data: orgs.data ?? [] },
  ].filter((s) => s.data.length > 0);

  return (
    <SectionList
      style={{ backgroundColor: c.background }}
      sections={sections}
      keyExtractor={(w) => w.id}
      renderItem={({ item }) => <WorkspaceRow w={item} />}
      renderSectionHeader={({ section }) => <SectionHeader title={section.title} />}
      stickySectionHeadersEnabled={false}
      ListEmptyComponent={<EmptyState icon="account-group-outline" title={t('groups.empty')} />}
      refreshControl={<RefreshControl refreshing={refreshing} onRefresh={onRefresh} colors={[c.primary]} tintColor={c.primary} />}
      contentContainerStyle={{ paddingBottom: 24 }}
    />
  );
}

const styles = StyleSheet.create({
  row: { flexDirection: 'row', alignItems: 'center', paddingHorizontal: 14, paddingVertical: 12, borderBottomWidth: StyleSheet.hairlineWidth },
  avatar: { width: 44, height: 44, borderRadius: 22, alignItems: 'center', justifyContent: 'center' },
  locked: { flexDirection: 'row', alignItems: 'center', borderWidth: 1, borderRadius: 4, paddingHorizontal: 4, marginLeft: 6 },
});
