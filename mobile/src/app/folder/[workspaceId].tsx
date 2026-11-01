import { useQuery } from '@tanstack/react-query';
import { Stack, useLocalSearchParams } from 'expo-router';

import { driveApi } from '@/api/endpoints';
import { FolderBrowser } from '@/components/FolderBrowser';

/** Duyệt thư mục bất kỳ (cá nhân #71, nhóm #74, thư mục được chia sẻ #72). */
export default function FolderScreen() {
  const { workspaceId, folderId, name } = useLocalSearchParams<{ workspaceId: string; folderId?: string; name?: string }>();
  const fid = folderId ? folderId : null;
  const info = useQuery({ queryKey: ['folder-info', workspaceId, fid], queryFn: () => driveApi.folderInfo(workspaceId, fid) });
  const title = info.data?.name ?? name ?? '';
  return (
    <>
      <Stack.Screen options={{ title }} />
      <FolderBrowser key={`${workspaceId}-${fid ?? 'root'}`} workspaceId={workspaceId} folderId={fid} />
    </>
  );
}
