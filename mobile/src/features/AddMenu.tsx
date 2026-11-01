import { useQueryClient } from '@tanstack/react-query';
import * as DocumentPicker from 'expo-document-picker';
import * as ImagePicker from 'expo-image-picker';
import { useState } from 'react';
import { Pressable, StyleSheet } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';

import { errorMessage } from '@/api/client';
import { driveApi } from '@/api/endpoints';
import { PromptDialog } from '@/components/PromptDialog';
import { Sheet, SheetItem } from '@/components/Sheet';
import { Icon } from '@/components/ui';
import { t } from '@/i18n';
import { invalidateData } from '@/store/query';
import { toast } from '@/store/toast';
import { useColors } from '@/theme';
import { uploadFiles, type UploadSource } from './upload';

function fromImagePicker(assets: ImagePicker.ImagePickerAsset[]): UploadSource[] {
  return assets.map((a) => ({ uri: a.uri, name: a.fileName, mimeType: a.mimeType ?? (a.type === 'video' ? 'video/mp4' : 'image/jpeg'), size: a.fileSize }));
}

/** FAB in folder views: Thư viện / Máy ảnh / Tệp / Thư mục mới. */
export function AddFab({
  workspaceId, folderId, canUpload, inTabs,
}: { workspaceId: string; folderId: string | null; canUpload: boolean; inTabs?: boolean }) {
  const c = useColors();
  const insets = useSafeAreaInsets();
  const qc = useQueryClient();
  const [open, setOpen] = useState(false);
  const [newFolder, setNewFolder] = useState(false);

  const after = (fn: () => void | Promise<void>) => {
    setOpen(false);
    setTimeout(() => {
      Promise.resolve(fn()).catch((e) => toast.error(errorMessage(e)));
    }, 350);
  };

  const pickLibrary = async () => {
    const perm = await ImagePicker.requestMediaLibraryPermissionsAsync();
    if (!perm.granted) {
      toast.error(t('upload.permissionLibrary'));
      return;
    }
    const res = await ImagePicker.launchImageLibraryAsync({ mediaTypes: ['images', 'videos'], allowsMultipleSelection: true, quality: 1, selectionLimit: 30 });
    if (!res.canceled) await uploadFiles(fromImagePicker(res.assets), workspaceId, folderId);
  };

  const pickCamera = async () => {
    const perm = await ImagePicker.requestCameraPermissionsAsync();
    if (!perm.granted) {
      toast.error(t('upload.permissionCamera'));
      return;
    }
    const res = await ImagePicker.launchCameraAsync({ mediaTypes: ['images', 'videos'], quality: 0.9 });
    if (!res.canceled) await uploadFiles(fromImagePicker(res.assets), workspaceId, folderId);
  };

  const pickFiles = async () => {
    const res = await DocumentPicker.getDocumentAsync({ multiple: true, copyToCacheDirectory: true, type: '*/*' });
    if (!res.canceled) {
      await uploadFiles(
        res.assets.map((a) => ({ uri: a.uri, name: a.name, mimeType: a.mimeType, size: a.size })),
        workspaceId,
        folderId,
      );
    }
  };

  if (!canUpload) return null;

  return (
    <>
      <Pressable
        accessibilityLabel={t('upload.title')}
        onPress={() => setOpen(true)}
        style={({ pressed }) => [styles.fab, { backgroundColor: c.primary, bottom: 20 + (inTabs ? 0 : insets.bottom), opacity: pressed ? 0.85 : 1 }]}
      >
        <Icon name="plus" size={30} color={c.primaryText} />
      </Pressable>
      <Sheet visible={open} onClose={() => setOpen(false)} title={t('upload.title')}>
        <SheetItem icon="image-multiple-outline" label={t('upload.library')} onPress={() => after(pickLibrary)} />
        <SheetItem icon="camera-outline" label={t('upload.camera')} onPress={() => after(pickCamera)} />
        <SheetItem icon="file-upload-outline" label={t('upload.file')} onPress={() => after(pickFiles)} />
        <SheetItem icon="folder-plus-outline" label={t('upload.newFolder')} onPress={() => after(() => setNewFolder(true))} />
      </Sheet>
      <PromptDialog
        visible={newFolder}
        title={t('upload.newFolderTitle')}
        label={t('upload.folderName')}
        onClose={() => setNewFolder(false)}
        onSubmit={async (name) => {
          await driveApi.createFolder(workspaceId, folderId, name);
          toast.success(t('upload.folderCreated'));
          await invalidateData(qc);
        }}
      />
    </>
  );
}

const styles = StyleSheet.create({
  fab: {
    position: 'absolute', right: 20, width: 58, height: 58, borderRadius: 29, alignItems: 'center', justifyContent: 'center',
    elevation: 6, shadowColor: '#000', shadowOpacity: 0.3, shadowRadius: 6, shadowOffset: { width: 0, height: 3 },
  },
});
