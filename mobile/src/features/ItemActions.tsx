import { useQueryClient } from '@tanstack/react-query';
import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react';
import { Alert } from 'react-native';

import { errorMessage } from '@/api/client';
import { driveApi } from '@/api/endpoints';
import type { DriveItem } from '@/api/types';
import { FolderPickerModal } from '@/components/FolderPickerModal';
import { PromptDialog } from '@/components/PromptDialog';
import { ShareLinkDialog } from '@/components/ShareLinkDialog';
import { Sheet, SheetItem } from '@/components/Sheet';
import { t } from '@/i18n';
import { invalidateData } from '@/store/query';
import { toast } from '@/store/toast';
import { openFileDetail, openItem } from '@/utils/navigation';
import { downloadFile } from './download';

export interface ActionOptions {
  /** Hide "Chi tiết" when already on the detail screen. */
  hideDetail?: boolean;
  /** Called after the item was deleted or moved away (e.g. leave the detail screen). */
  onRemoved?: () => void;
}

interface Ctx {
  openActions: (item: DriveItem, opts?: ActionOptions) => void;
}

const ActionsContext = createContext<Ctx>({ openActions: () => undefined });

export const useItemActions = () => useContext(ActionsContext);

type Mode = 'copy' | 'move';

/** #76–#81, #53, #83, #84: action sheet shared by every list (long-press / ⋯). */
export function ItemActionsProvider({ children }: { children: ReactNode }) {
  const qc = useQueryClient();
  const [item, setItem] = useState<DriveItem | null>(null);
  const [opts, setOpts] = useState<ActionOptions>({});
  const [sheet, setSheet] = useState(false);
  const [renameItem, setRenameItem] = useState<DriveItem | null>(null);
  const [shareItem, setShareItem] = useState<DriveItem | null>(null);
  const [picker, setPicker] = useState<{ item: DriveItem; mode: Mode } | null>(null);

  const refreshAll = useCallback(() => invalidateData(qc), [qc]);

  const openActions = useCallback((it: DriveItem, o?: ActionOptions) => {
    setItem(it);
    setOpts(o ?? {});
    setSheet(true);
  }, []);

  const run = (fn: () => void) => {
    setSheet(false);
    // Let the sheet close before presenting another modal (iOS can't stack modals mid-animation).
    setTimeout(fn, 320);
  };

  const toggleStar = async (it: DriveItem) => {
    try {
      await driveApi.setStar(it.type, it.id, !it.isStarred);
      toast.success(it.isStarred ? t('actions.unstarred') : t('actions.starred'));
      await refreshAll();
    } catch (e) {
      toast.error(errorMessage(e));
    }
  };

  const confirmDelete = (it: DriveItem) => {
    Alert.alert(t('actions.deleteTitle'), t('actions.deleteMessage', { name: it.name }), [
      { text: t('common.cancel'), style: 'cancel' },
      {
        text: t('actions.delete'),
        style: 'destructive',
        onPress: async () => {
          try {
            await driveApi.remove([{ type: it.type, id: it.id }]);
            toast.success(t('actions.deleted'));
            opts.onRemoved?.();
            await refreshAll();
          } catch (e) {
            toast.error(errorMessage(e));
          }
        },
      },
    ]);
  };

  const value = useMemo(() => ({ openActions }), [openActions]);
  const it = item;

  return (
    <ActionsContext.Provider value={value}>
      {children}

      <Sheet visible={sheet && !!it} onClose={() => setSheet(false)} title={it?.name}>
        {it ? (
          <>
            <SheetItem icon={it.type === 'folder' ? 'folder-open-outline' : 'eye-outline'} label={t('actions.open')} onPress={() => run(() => openItem(it))} />
            {it.type === 'file' && !opts.hideDetail ? (
              <SheetItem icon="information-outline" label={t('actions.detail')} onPress={() => run(() => openFileDetail(it.id, it.name))} />
            ) : null}
            {it.type === 'file' ? <SheetItem icon="download-outline" label={t('actions.download')} onPress={() => run(() => downloadFile(it))} /> : null}
            <SheetItem icon="link-variant" label={t('actions.shareLink')} onPress={() => run(() => setShareItem(it))} />
            <SheetItem
              icon={it.isStarred ? 'star-off-outline' : 'star-outline'}
              label={it.isStarred ? t('actions.unstar') : t('actions.star')}
              onPress={() => run(() => toggleStar(it))}
            />
            <SheetItem icon="pencil-outline" label={t('actions.rename')} onPress={() => run(() => setRenameItem(it))} />
            <SheetItem icon="content-copy" label={t('actions.copy')} onPress={() => run(() => setPicker({ item: it, mode: 'copy' }))} />
            <SheetItem icon="folder-move-outline" label={t('actions.move')} onPress={() => run(() => setPicker({ item: it, mode: 'move' }))} />
            <SheetItem icon="delete-outline" label={t('actions.delete')} danger onPress={() => run(() => confirmDelete(it))} />
          </>
        ) : null}
      </Sheet>

      <PromptDialog
        visible={!!renameItem}
        title={t('actions.renameTitle')}
        label={t('actions.newName')}
        initialValue={renameItem?.name}
        onClose={() => setRenameItem(null)}
        onSubmit={async (name) => {
          if (!renameItem || name === renameItem.name) return;
          if (renameItem.type === 'file') await driveApi.renameFile(renameItem.id, name);
          else await driveApi.renameFolder(renameItem.id, name);
          toast.success(t('actions.renamed'));
          await refreshAll();
        }}
      />

      <ShareLinkDialog item={shareItem} onClose={() => setShareItem(null)} />

      <FolderPickerModal
        visible={!!picker}
        title={picker?.mode === 'move' ? t('actions.moveTitle') : t('actions.copyTitle')}
        initialWorkspaceId={picker?.item.workspaceId}
        excludeItems={picker ? [{ type: picker.item.type, id: picker.item.id }] : undefined}
        onClose={() => setPicker(null)}
        onPick={async ({ workspaceId, folderId }) => {
          if (!picker) return;
          const refs = [{ type: picker.item.type, id: picker.item.id }];
          if (picker.mode === 'copy') {
            const r = await driveApi.copy(refs, workspaceId, folderId);
            toast.success(t('actions.copied', { count: r.copied }));
          } else {
            const r = await driveApi.move(refs, workspaceId, folderId);
            toast.success(t('actions.moved', { count: r.moved }));
          }
          await refreshAll();
        }}
      />
    </ActionsContext.Provider>
  );
}
