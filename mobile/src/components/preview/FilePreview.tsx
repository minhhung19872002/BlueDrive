import { useQuery } from '@tanstack/react-query';
import { Image } from 'expo-image';
import { Pressable, ScrollView, StyleSheet, Text, View } from 'react-native';

import { absoluteUrl, errorMessage } from '@/api/client';
import { driveApi, fetchSignedText } from '@/api/endpoints';
import type { FileDetail } from '@/api/types';
import { downloadFile } from '@/features/download';
import { t } from '@/i18n';
import { useColors } from '@/theme';
import { mediaColor, mediaIcon, previewKind } from '@/utils/format';
import { Button, ErrorState, Icon, LoadingView } from '../ui';
import { AudioPlayerView, VideoPlayerView } from './MediaPlayers';
import { OfficeViewer } from './OfficeViewer';
import { WatermarkOverlay } from './Watermark';
import { ZoomableImage } from './ZoomableImage';

function useInlineUrl(fileId: string, enabled: boolean) {
  // Signed URLs are short-lived: always fetch a fresh one when the viewer mounts.
  return useQuery({ queryKey: ['inline-url', fileId], queryFn: () => driveApi.downloadUrl(fileId, true), enabled, staleTime: 0, gcTime: 0 });
}

function TextPreview({ fileId }: { fileId: string }) {
  const c = useColors();
  const url = useInlineUrl(fileId, true);
  const text = useQuery({
    queryKey: ['text', fileId, url.data?.url],
    queryFn: () => fetchSignedText(url.data!.url),
    enabled: !!url.data,
    gcTime: 0,
  });
  if (url.isLoading || text.isLoading) return <LoadingView />;
  if (url.isError || text.isError) return <ErrorState message={errorMessage(url.error ?? text.error)} onRetry={() => url.refetch()} />;
  return (
    <ScrollView style={{ flex: 1, backgroundColor: c.surface }} contentContainerStyle={{ padding: 14 }}>
      {text.data?.truncated ? <Text style={{ color: c.warning, marginBottom: 8 }}>{t('viewer.textTooLarge')}</Text> : null}
      <Text selectable style={{ color: c.text, fontFamily: 'monospace', fontSize: 13, lineHeight: 19 }}>
        {text.data?.text}
      </Text>
    </ScrollView>
  );
}

function NoPreview({ file }: { file: FileDetail }) {
  const c = useColors();
  return (
    <View style={styles.center}>
      <Icon name={mediaIcon(file.mediaType, file.extension)} size={96} color={mediaColor(file.mediaType)} />
      <Text style={{ color: c.text, fontWeight: '600', marginTop: 12, textAlign: 'center' }}>{file.name}</Text>
      <Text style={{ color: c.textMuted, marginTop: 6, textAlign: 'center' }}>{t('detail.noPreview')}</Text>
      {file.access.canDownload ? (
        <Button title={t('actions.download')} icon="download" onPress={() => downloadFile(file)} style={{ marginTop: 16 }} />
      ) : null}
    </View>
  );
}

/**
 * Viewer body by type (#76): ảnh (zoom), video, âm thanh, PDF/Office (ONLYOFFICE), văn bản, còn lại: biểu tượng + tải xuống.
 * `compact` = embedded in the detail screen (#83): images are a tappable preview instead of a zoom surface.
 */
export function FilePreview(props: { file: FileDetail; compact?: boolean; onOpenFull?: () => void }) {
  if (!props.file.watermark) return <PreviewBody {...props} />;
  return (
    <View style={{ flex: 1 }}>
      <PreviewBody {...props} />
      <WatermarkOverlay />
    </View>
  );
}

function PreviewBody({ file, compact, onOpenFull }: { file: FileDetail; compact?: boolean; onOpenFull?: () => void }) {
  const c = useColors();
  const kind = previewKind(file);
  const needsUrl = kind === 'image' || kind === 'video' || kind === 'audio';
  const url = useInlineUrl(file.id, needsUrl && !file.contentBlocked && !(compact && kind === 'image'));

  if (file.contentBlocked)
    return <ErrorState message={t(file.scanStatus === 'infected' ? 'scan.infected' : 'scan.pendingBlocked')} />;

  if (kind === 'image' && compact) {
    const src = absoluteUrl(file.previewUrl ?? file.thumbnailUrl);
    return (
      <Pressable onPress={onOpenFull} style={{ flex: 1, backgroundColor: '#000' }}>
        {src ? (
          <Image source={{ uri: src }} style={{ flex: 1 }} contentFit="contain" transition={150} />
        ) : (
          <View style={styles.center}>
            <Icon name="image" size={80} color="#888" />
          </View>
        )}
        <View style={styles.zoomHint}>
          <Icon name="magnify-plus-outline" size={16} color="#fff" />
        </View>
      </Pressable>
    );
  }

  if (needsUrl) {
    if (url.isLoading) return <LoadingView />;
    if (url.isError) return <ErrorState message={errorMessage(url.error)} onRetry={() => url.refetch()} />;
    const uri = absoluteUrl(url.data!.url)!;
    if (kind === 'image') return <ZoomableImage uri={uri} fallbackUri={absoluteUrl(file.previewUrl)} />;
    if (kind === 'video') return <VideoPlayerView uri={uri} autoPlay={!compact} />;
    return (
      <View style={{ flex: 1, justifyContent: 'center', backgroundColor: c.background }}>
        <AudioPlayerView uri={uri} title={file.title || file.name} />
      </View>
    );
  }
  if (kind === 'office') return <OfficeViewer fileId={file.id} onDownload={file.access.canDownload ? () => downloadFile(file) : undefined} />;
  if (kind === 'text') return <TextPreview fileId={file.id} />;
  return <NoPreview file={file} />;
}

const styles = StyleSheet.create({
  center: { flex: 1, alignItems: 'center', justifyContent: 'center', padding: 24 },
  zoomHint: { position: 'absolute', right: 10, bottom: 10, backgroundColor: 'rgba(0,0,0,0.5)', borderRadius: 14, padding: 6 },
});
