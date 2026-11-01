import { useQuery } from '@tanstack/react-query';
import { router } from 'expo-router';
import { useState } from 'react';
import { Pressable, ScrollView, StyleSheet, Text, View } from 'react-native';

import { driveApi } from '@/api/endpoints';
import type { Breadcrumb, DriveItem } from '@/api/types';
import { AddFab } from '@/features/AddMenu';
import { usePagedList } from '@/hooks/usePagedList';
import { useRefresh } from '@/hooks/useRefresh';
import { t } from '@/i18n';
import { useAuth } from '@/store/auth';
import { useColors } from '@/theme';
import { ItemList } from './ItemList';
import { Sheet, SheetItem } from './Sheet';
import { Icon } from './ui';

const SORTS = [
  { key: 'name', label: 'browser.sortName' },
  { key: '-name', label: 'browser.sortNameDesc' },
  { key: '-updatedAt', label: 'browser.sortUpdated' },
  { key: '-createdAt', label: 'browser.sortCreated' },
  { key: '-size', label: 'browser.sortSize' },
] as const;

let lastSort = 'name';

/** Navigate to a breadcrumb entry (#82): pop back to it if it is in the history, otherwise open it. */
export function goToCrumb(workspaceId: string, crumb: Breadcrumb) {
  const personal = useAuth.getState().user?.personalWorkspaceId;
  if (!crumb.id && workspaceId === personal) {
    router.dismissTo('/files');
    return;
  }
  router.dismissTo({ pathname: '/folder/[workspaceId]', params: { workspaceId, folderId: crumb.id ?? '', name: crumb.name } });
}

/** Folder contents with breadcrumb, sort, tree shortcut, pull-to-refresh, infinite scroll and upload FAB (#71, #74, #82). */
export function FolderBrowser({ workspaceId, folderId, inTabs }: { workspaceId: string; folderId: string | null; inTabs?: boolean }) {
  const c = useColors();
  const [sort, setSort] = useState(lastSort);
  const [sortOpen, setSortOpen] = useState(false);

  const info = useQuery({ queryKey: ['folder-info', workspaceId, folderId], queryFn: () => driveApi.folderInfo(workspaceId, folderId) });
  const list = usePagedList<DriveItem>(['items', workspaceId, folderId, sort], (page) => driveApi.items(workspaceId, folderId, page, sort));
  const { refreshing, onRefresh } = useRefresh(list.refetch, info.refetch);

  const crumbs = info.data?.breadcrumb ?? [];
  const access = info.data?.access;

  const header = (
    <View style={{ backgroundColor: c.surface, borderBottomColor: c.border, borderBottomWidth: StyleSheet.hairlineWidth }}>
      <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.crumbs}>
        {crumbs.map((cr, i) => {
          const last = i === crumbs.length - 1;
          return (
            <View key={`${cr.id ?? 'root'}-${i}`} style={styles.crumbWrap}>
              {i > 0 ? <Icon name="chevron-right" size={16} color={c.textMuted} /> : <Icon name="home-outline" size={16} color={c.textMuted} />}
              <Pressable disabled={last} onPress={() => goToCrumb(workspaceId, cr)} hitSlop={6}>
                <Text numberOfLines={1} style={[styles.crumb, { color: last ? c.text : c.primary, fontWeight: last ? '700' : '500' }]}>
                  {cr.name}
                </Text>
              </Pressable>
            </View>
          );
        })}
      </ScrollView>
      <View style={styles.toolbar}>
        <Text style={{ color: c.textMuted, flex: 1, fontSize: 13 }}>{list.isSuccess ? t('browser.items', { count: list.total }) : ''}</Text>
        <Pressable onPress={() => router.push({ pathname: '/tree/[workspaceId]', params: { workspaceId, folderId: folderId ?? '' } })} style={styles.tool} hitSlop={6}>
          <Icon name="file-tree-outline" size={18} color={c.primary} />
          <Text style={{ color: c.primary, marginLeft: 4, fontSize: 13 }}>{t('browser.tree')}</Text>
        </Pressable>
        <Pressable onPress={() => setSortOpen(true)} style={styles.tool} hitSlop={6}>
          <Icon name="sort" size={18} color={c.primary} />
          <Text style={{ color: c.primary, marginLeft: 4, fontSize: 13 }}>{t('browser.sort')}</Text>
        </Pressable>
      </View>
      {access?.workspaceLocked ? (
        <Text style={[styles.notice, { color: c.warning }]}>{t('browser.locked')}</Text>
      ) : access && !access.canUpload && !access.canEdit ? (
        <Text style={[styles.notice, { color: c.textMuted }]}>{t('browser.readOnly')}</Text>
      ) : null}
    </View>
  );

  return (
    <View style={{ flex: 1, backgroundColor: c.background }}>
      <ItemList
        data={list.items}
        getItem={(x) => x}
        isLoading={list.isLoading}
        isError={list.isError}
        error={list.error}
        refreshing={refreshing}
        onRefresh={onRefresh}
        onEndReached={list.loadMore}
        loadingMore={list.isFetchingNextPage}
        emptyTitle={t('browser.emptyFolder')}
        emptyHint={access?.canUpload ? t('browser.emptyFolderHint') : undefined}
        header={header}
      />
      <AddFab workspaceId={workspaceId} folderId={folderId} canUpload={!!access?.canUpload} inTabs={inTabs} />
      <Sheet visible={sortOpen} onClose={() => setSortOpen(false)} title={t('browser.sort')}>
        {SORTS.map((s) => (
          <SheetItem
            key={s.key}
            icon={sort === s.key ? 'radiobox-marked' : 'radiobox-blank'}
            label={t(s.label)}
            onPress={() => {
              lastSort = s.key;
              setSort(s.key);
              setSortOpen(false);
            }}
          />
        ))}
      </Sheet>
    </View>
  );
}

const styles = StyleSheet.create({
  crumbs: { paddingHorizontal: 12, paddingTop: 10, paddingBottom: 4, alignItems: 'center' },
  crumbWrap: { flexDirection: 'row', alignItems: 'center' },
  crumb: { fontSize: 14, marginHorizontal: 4, maxWidth: 200 },
  toolbar: { flexDirection: 'row', alignItems: 'center', paddingHorizontal: 14, paddingVertical: 8 },
  tool: { flexDirection: 'row', alignItems: 'center', marginLeft: 16 },
  notice: { paddingHorizontal: 14, paddingBottom: 8, fontSize: 12 },
});
