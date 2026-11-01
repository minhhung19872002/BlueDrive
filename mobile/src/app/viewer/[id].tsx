import { useQuery } from '@tanstack/react-query';
import { router, Stack, useLocalSearchParams } from 'expo-router';
import { Pressable, View } from 'react-native';

import { errorMessage } from '@/api/client';
import { driveApi } from '@/api/endpoints';
import { FilePreview } from '@/components/preview/FilePreview';
import { ErrorState, Icon, LoadingView } from '@/components/ui';
import { useItemActions } from '@/features/ItemActions';
import { useColors } from '@/theme';
import { detailToItem } from '@/utils/items';
import { openFileDetail } from '@/utils/navigation';

/** #76 Mở tài liệu: ảnh (zoom), video, âm thanh, PDF/Office qua ONLYOFFICE, văn bản. Ghi nhận vào "Gần đây". */
export default function ViewerScreen() {
  const c = useColors();
  const { id, name } = useLocalSearchParams<{ id: string; name?: string }>();
  const { openActions } = useItemActions();
  // recordView=true → the server adds this file to "Gần đây" (#73).
  const file = useQuery({ queryKey: ['file', id, 'view'], queryFn: () => driveApi.file(id, true), staleTime: 0 });
  const f = file.data;

  return (
    <View style={{ flex: 1, backgroundColor: f && (f.mediaType === 'image' || f.mediaType === 'video') ? '#000' : c.background }}>
      <Stack.Screen
        options={{
          title: f?.name ?? name ?? '',
          headerRight: () =>
            f ? (
              <View style={{ flexDirection: 'row', gap: 18 }}>
                <Pressable hitSlop={10} onPress={() => openFileDetail(f.id, f.name)}>
                  <Icon name="information-outline" color={c.text} />
                </Pressable>
                <Pressable hitSlop={10} onPress={() => openActions(detailToItem(f), { onRemoved: () => router.back() })}>
                  <Icon name="dots-vertical" color={c.text} />
                </Pressable>
              </View>
            ) : null,
        }}
      />
      {file.isLoading ? (
        <LoadingView />
      ) : file.isError ? (
        <ErrorState message={errorMessage(file.error)} onRetry={() => file.refetch()} />
      ) : f ? (
        <FilePreview file={f} />
      ) : null}
    </View>
  );
}
