import { FolderBrowser } from '@/components/FolderBrowser';
import { ErrorState } from '@/components/ui';
import { t } from '@/i18n';
import { useAuth } from '@/store/auth';

/** #71 Tài liệu của tôi: gốc không gian cá nhân. */
export default function MyFilesScreen() {
  const wsId = useAuth((s) => s.user?.personalWorkspaceId);
  if (!wsId) return <ErrorState message={t('common.error')} />;
  return <FolderBrowser workspaceId={wsId} folderId={null} inTabs />;
}
