import { useQuery } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { Modal, Pressable, ScrollView, StyleSheet, Text, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';

import { errorMessage } from '@/api/client';
import { driveApi } from '@/api/endpoints';
import type { ItemRef, Workspace } from '@/api/types';
import { t } from '@/i18n';
import { useAuth } from '@/store/auth';
import { useColors } from '@/theme';
import { collectSubtreeIds, FolderTree } from './FolderTree';
import { Button, ErrorState, Icon, LoadingView } from './ui';

export interface PickResult {
  workspaceId: string;
  folderId: string | null;
}

export function workspaceLabel(w: Pick<Workspace, 'type' | 'name' | 'ownerId'>, myId?: string) {
  return w.type === 'personal' && w.ownerId === myId ? t('picker.personal') : w.name;
}

interface Props {
  visible: boolean;
  title: string;
  initialWorkspaceId?: string;
  /** Folders being moved/copied: themselves and their sub-folders cannot be the target. */
  excludeItems?: ItemRef[];
  onClose: () => void;
  onPick: (r: PickResult) => Promise<void>;
}

/** #79/#80 picker: workspace switcher + folder tree (#82) of the selected workspace. */
export function FolderPickerModal(props: Props) {
  return (
    <Modal visible={props.visible} animationType="slide" onRequestClose={props.onClose} presentationStyle="fullScreen">
      {props.visible ? <PickerBody {...props} /> : null}
    </Modal>
  );
}

function PickerBody({ title, initialWorkspaceId, excludeItems, onClose, onPick }: Props) {
  const c = useColors();
  const user = useAuth((s) => s.user);
  const [chosenWs, setWorkspaceId] = useState<string | undefined>(initialWorkspaceId ?? user?.personalWorkspaceId ?? undefined);
  const [folderId, setFolderId] = useState<string | null>(null);
  const [folderName, setFolderName] = useState<string>('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const workspaces = useQuery({ queryKey: ['workspaces', 'all'], queryFn: () => driveApi.workspaces() });
  const sorted = useMemo(() => {
    const order = { personal: 0, group: 1, organization: 2 } as const;
    return [...(workspaces.data ?? [])].filter((w) => !w.isLocked).sort((a, b) => order[a.type] - order[b.type] || a.name.localeCompare(b.name, 'vi'));
  }, [workspaces.data]);
  // The item may live in a workspace the user cannot browse (e.g. a colleague's shared file) → default to "Tài liệu của tôi".
  const workspaceId =
    workspaces.data && chosenWs && !workspaces.data.some((w) => w.id === chosenWs) ? (user?.personalWorkspaceId ?? sorted[0]?.id) : chosenWs;

  const tree = useQuery({
    queryKey: ['folder-tree', workspaceId],
    queryFn: () => driveApi.folderTree(workspaceId!),
    enabled: !!workspaceId,
  });

  const disabled = useMemo(() => {
    const folderIds = (excludeItems ?? []).filter((i) => i.type === 'folder').map((i) => i.id);
    return folderIds.length && tree.data ? collectSubtreeIds(tree.data, folderIds) : new Set<string>();
  }, [excludeItems, tree.data]);

  const currentWs = sorted.find((w) => w.id === workspaceId);
  const targetName = folderId ? folderName : `${currentWs ? workspaceLabel(currentWs, user?.id) : ''} ${t('picker.root')}`;

  const confirm = async () => {
    if (!workspaceId) return;
    setBusy(true);
    setError(null);
    try {
      await onPick({ workspaceId, folderId });
      onClose();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <SafeAreaView style={{ flex: 1, backgroundColor: c.background }}>
        <View style={[styles.header, { borderBottomColor: c.border, backgroundColor: c.surface }]}>
          <Pressable onPress={onClose} hitSlop={12}>
            <Icon name="close" />
          </Pressable>
          <Text style={[styles.title, { color: c.text }]}>{title}</Text>
        </View>

        <Text style={[styles.caption, { color: c.textMuted }]}>{t('picker.workspace')}</Text>
        <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.chips} style={{ flexGrow: 0 }}>
          {sorted.map((w) => {
            const active = w.id === workspaceId;
            return (
              <Pressable
                key={w.id}
                onPress={() => {
                  setWorkspaceId(w.id);
                  setFolderId(null);
                }}
                style={[styles.chip, { backgroundColor: active ? c.primary : c.surface, borderColor: active ? c.primary : c.border }]}
              >
                <Icon
                  name={w.type === 'personal' ? 'account' : w.type === 'organization' ? 'office-building' : 'account-group'}
                  size={16}
                  color={active ? c.primaryText : c.textMuted}
                />
                <Text numberOfLines={1} style={{ color: active ? c.primaryText : c.text, marginLeft: 6, maxWidth: 180 }}>
                  {workspaceLabel(w, user?.id)}
                </Text>
              </Pressable>
            );
          })}
        </ScrollView>

        <Text style={[styles.caption, { color: c.textMuted }]}>{t('picker.chooseFolder')}</Text>
        <View style={{ flex: 1, backgroundColor: c.surface }}>
          {workspaces.isError ? (
            <ErrorState message={errorMessage(workspaces.error)} onRetry={() => workspaces.refetch()} />
          ) : tree.isLoading || workspaces.isLoading ? (
            <LoadingView />
          ) : tree.isError ? (
            <ErrorState message={errorMessage(tree.error)} onRetry={() => tree.refetch()} />
          ) : (
            <FolderTree
              key={workspaceId}
              roots={tree.data ?? []}
              selectedId={folderId}
              disabledIds={disabled}
              refreshing={tree.isRefetching}
              onRefresh={() => tree.refetch()}
              onSelect={(n) => {
                setFolderId(n.id);
                setFolderName(n.name);
              }}
              header={
                <Pressable
                  onPress={() => setFolderId(null)}
                  style={[styles.rootRow, { backgroundColor: folderId === null ? c.surfaceAlt : 'transparent', borderBottomColor: c.border }]}
                >
                  <Icon name="home-outline" color={c.primary} />
                  <Text style={{ color: c.text, marginLeft: 10, flex: 1, fontWeight: folderId === null ? '700' : '400' }}>{t('picker.root')}</Text>
                  {folderId === null ? <Icon name="check" color={c.primary} /> : null}
                </Pressable>
              }
            />
          )}
        </View>

        <View style={[styles.footer, { borderTopColor: c.border, backgroundColor: c.surface }]}>
          {error ? <Text style={{ color: c.danger, marginBottom: 8 }}>{error}</Text> : null}
          <Button title={t('picker.selectHere', { name: targetName.trim() })} onPress={confirm} loading={busy} disabled={!workspaceId} icon="check" />
        </View>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  header: { flexDirection: 'row', alignItems: 'center', paddingHorizontal: 16, paddingVertical: 14, borderBottomWidth: StyleSheet.hairlineWidth },
  title: { fontSize: 18, fontWeight: '700', marginLeft: 16 },
  caption: { fontSize: 12, fontWeight: '700', paddingHorizontal: 16, paddingTop: 12, paddingBottom: 6 },
  chips: { paddingHorizontal: 12, gap: 8 },
  chip: { flexDirection: 'row', alignItems: 'center', borderWidth: 1, borderRadius: 18, paddingHorizontal: 12, paddingVertical: 7 },
  rootRow: { flexDirection: 'row', alignItems: 'center', paddingHorizontal: 16, paddingVertical: 13, borderBottomWidth: StyleSheet.hairlineWidth },
  footer: { padding: 16, borderTopWidth: StyleSheet.hairlineWidth },
});
