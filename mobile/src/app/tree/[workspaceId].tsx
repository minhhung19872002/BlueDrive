import { useQuery } from '@tanstack/react-query';
import { router, Stack, useLocalSearchParams } from 'expo-router';
import { useMemo } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';

import { errorMessage } from '@/api/client';
import { driveApi } from '@/api/endpoints';
import { ancestorIds, FolderTree } from '@/components/FolderTree';
import { EmptyState, ErrorState, Icon, LoadingView } from '@/components/ui';
import { t } from '@/i18n';
import { useColors } from '@/theme';
import { openFolder } from '@/utils/navigation';

/** #82 Cây thư mục của một không gian: chạm vào thư mục để mở. */
export default function TreeScreen() {
  const c = useColors();
  const { workspaceId, folderId } = useLocalSearchParams<{ workspaceId: string; folderId?: string }>();
  const tree = useQuery({ queryKey: ['folder-tree', workspaceId], queryFn: () => driveApi.folderTree(workspaceId) });
  const info = useQuery({ queryKey: ['folder-info', workspaceId, null], queryFn: () => driveApi.folderInfo(workspaceId, null) });
  const expanded = useMemo(() => (tree.data && folderId ? ancestorIds(tree.data, folderId) : []), [tree.data, folderId]);

  if (tree.isLoading) return <LoadingView />;
  if (tree.isError) return <ErrorState message={errorMessage(tree.error)} onRetry={() => tree.refetch()} />;

  const go = (id: string | null, name: string) => {
    router.back();
    setTimeout(() => openFolder(workspaceId, id, name), 50);
  };

  return (
    <View style={{ flex: 1, backgroundColor: c.surface }}>
      <Stack.Screen options={{ title: info.data?.workspaceName ? `${t('titles.tree')} – ${info.data.workspaceName}` : t('titles.tree') }} />
      <FolderTree
        roots={tree.data ?? []}
        selectedId={folderId || null}
        initiallyExpanded={expanded}
        refreshing={tree.isRefetching}
        onRefresh={() => tree.refetch()}
        onSelect={(n) => go(n.id, n.name)}
        header={
          <>
            <Pressable onPress={() => go(null, info.data?.workspaceName ?? '')} style={[styles.root, { borderBottomColor: c.border }]}>
              <Icon name="home-outline" color={c.primary} />
              <Text style={{ color: c.text, marginLeft: 10, fontWeight: '600' }}>{info.data?.workspaceName ?? t('browser.root')}</Text>
            </Pressable>
            {tree.data && tree.data.length === 0 ? <EmptyState title={t('picker.noFolders')} /> : null}
          </>
        }
      />
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flexDirection: 'row', alignItems: 'center', padding: 16, borderBottomWidth: StyleSheet.hairlineWidth },
});
