import { useQuery } from '@tanstack/react-query';
import { router, Stack, useLocalSearchParams } from 'expo-router';
import type { ReactNode } from 'react';
import { Pressable, RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';

import { errorMessage } from '@/api/client';
import { driveApi } from '@/api/endpoints';
import { goToCrumb } from '@/components/FolderBrowser';
import { FilePreview } from '@/components/preview/FilePreview';
import { Button, ErrorState, Icon, LoadingView, SectionHeader } from '@/components/ui';
import { downloadFile } from '@/features/download';
import { useItemActions } from '@/features/ItemActions';
import { useRefresh } from '@/hooks/useRefresh';
import { t } from '@/i18n';
import { useColors } from '@/theme';
import { formatBytes, formatDate, formatDuration, mediaTypeLabel, previewKind } from '@/utils/format';
import { detailToItem } from '@/utils/items';
import { openFileViewer } from '@/utils/navigation';

function Row({ label, children }: { label: string; children: ReactNode }) {
  const c = useColors();
  return (
    <View style={[styles.row, { borderBottomColor: c.border }]}>
      <Text style={[styles.label, { color: c.textMuted }]}>{label}</Text>
      <View style={{ flex: 1 }}>{typeof children === 'string' ? <Text selectable style={{ color: c.text, fontSize: 14 }}>{children}</Text> : children}</View>
    </View>
  );
}

/** #83 Chi tiết tệp: thông tin + xem trước. */
export default function FileDetailScreen() {
  const c = useColors();
  const { id, name } = useLocalSearchParams<{ id: string; name?: string }>();
  const { openActions } = useItemActions();
  const file = useQuery({ queryKey: ['file', id], queryFn: () => driveApi.file(id, false) });
  const { refreshing, onRefresh } = useRefresh(file.refetch);
  const f = file.data;

  if (file.isLoading) return <LoadingView />;
  if (file.isError || !f) return <ErrorState message={errorMessage(file.error)} onRetry={() => file.refetch()} />;

  const current = f.versions.find((v) => v.isCurrent) ?? f.versions[0];
  const kind = previewKind(f);
  const previewHeight = kind === 'audio' ? 300 : kind === 'office' ? 460 : 280;
  const approvalColor = f.approvalStatus === 'approved' ? c.success : f.approvalStatus === 'rejected' ? c.danger : c.warning;

  return (
    <>
      <Stack.Screen
        options={{
          title: f.name ?? name ?? '',
          headerRight: () => (
            <Pressable hitSlop={10} onPress={() => openActions(detailToItem(f), { hideDetail: true, onRemoved: () => router.back() })}>
              <Icon name="dots-vertical" color={c.text} />
            </Pressable>
          ),
        }}
      />
      <ScrollView
        style={{ backgroundColor: c.background }}
        contentContainerStyle={{ paddingBottom: 32 }}
        refreshControl={<RefreshControl refreshing={refreshing} onRefresh={onRefresh} colors={[c.primary]} tintColor={c.primary} />}
        nestedScrollEnabled
      >
        <View style={{ height: previewHeight, backgroundColor: kind === 'image' || kind === 'video' ? '#000' : c.surface }}>
          <FilePreview file={f} compact onOpenFull={() => openFileViewer(f.id, f.name)} />
        </View>

        <View style={styles.actions}>
          <Button title={t('detail.openFull')} icon="fullscreen" onPress={() => openFileViewer(f.id, f.name)} style={{ flex: 1 }} />
          {f.access.canDownload && !f.contentBlocked ? (
            <Button title={t('actions.download')} icon="download" variant="secondary" onPress={() => downloadFile(f)} style={{ flex: 1 }} />
          ) : null}
        </View>

        {f.isDeleted ? <Text style={{ color: c.danger, paddingHorizontal: 16 }}>{t('detail.deleted')}</Text> : null}

        <SectionHeader title={t('detail.info')} />
        <View style={{ backgroundColor: c.surface }}>
          <Row label={t('detail.name')}>
            <View style={{ flexDirection: 'row', alignItems: 'center' }}>
              <Text selectable style={{ color: c.text, fontSize: 14, flexShrink: 1 }}>
                {f.name}
              </Text>
              {f.isStarred ? <Icon name="star" size={16} color={c.star} style={{ marginLeft: 6 }} /> : null}
            </View>
          </Row>
          {f.title ? <Row label={t('detail.title')}>{f.title}</Row> : null}
          {f.description ? <Row label={t('detail.description')}>{f.description}</Row> : null}
          {f.tags.length ? (
            <Row label={t('detail.tags')}>
              <View style={styles.tags}>
                {f.tags.map((tag) => (
                  <Text key={tag} style={[styles.tag, { backgroundColor: c.surfaceAlt, color: c.text }]}>
                    {tag}
                  </Text>
                ))}
              </View>
            </Row>
          ) : null}
          <Row label={t('detail.type')}>{`${f.mediaTypeName || mediaTypeLabel(f.mediaType)} (.${f.extension})`}</Row>
          <Row label={t('detail.size')}>{`${formatBytes(f.sizeBytes)} (${f.sizeBytes.toLocaleString('vi-VN')} byte)`}</Row>
          {f.width && f.height ? <Row label={t('detail.dimensions')}>{`${f.width} × ${f.height} px`}</Row> : null}
          {f.durationSeconds ? <Row label={t('detail.duration')}>{formatDuration(f.durationSeconds)}</Row> : null}
          <Row label={t('detail.owner')}>{f.createdByName ?? t('common.unknown')}</Row>
          <Row label={t('detail.createdAt')}>{formatDate(f.createdAt)}</Row>
          {f.updatedAt ? <Row label={t('detail.updatedAt')}>{`${formatDate(f.updatedAt)}${f.updatedByName ? ` – ${f.updatedByName}` : ''}`}</Row> : null}
          <Row label={t('detail.location')}>
            <View style={styles.crumbs}>
              {f.location.map((cr, i) => (
                <View key={`${cr.id ?? 'root'}-${i}`} style={{ flexDirection: 'row', alignItems: 'center' }}>
                  {i > 0 ? <Text style={{ color: c.textMuted }}> / </Text> : null}
                  <Pressable onPress={() => goToCrumb(f.workspaceId, cr)} hitSlop={4}>
                    <Text style={{ color: c.primary, fontSize: 14 }}>{cr.name}</Text>
                  </Pressable>
                </View>
              ))}
            </View>
          </Row>
          <Row label={t('detail.approval')}>
            <Text style={{ color: approvalColor, fontWeight: '600' }}>{t(`approval.${f.approvalStatus}`)}</Text>
          </Row>
          {f.rejectionReason ? <Row label={t('detail.rejection')}>{f.rejectionReason}</Row> : null}
          <Row label={t('detail.scan')}>{t(`scan.${f.scanStatus}`)}</Row>
          {f.catalogs.length ? <Row label={t('detail.catalogs')}>{f.catalogs.map((x) => `${x.catalogName}: ${x.name}`).join('\n')}</Row> : null}
          <Row label={t('detail.versions')}>{t('detail.versionsCount', { count: f.versions.length, current: current?.versionNo ?? 1 })}</Row>
          {f.checksumSha256 ? (
            <Row label={t('detail.checksum')}>
              <Text selectable style={{ color: c.textMuted, fontSize: 11, fontFamily: 'monospace' }}>
                {f.checksumSha256}
              </Text>
            </Row>
          ) : null}
        </View>

        {f.versions.length > 0 ? (
          <>
            <SectionHeader title={t('detail.versions')} />
            <View style={{ backgroundColor: c.surface }}>
              {f.versions.map((v) => (
                <View key={v.id} style={[styles.version, { borderBottomColor: c.border }]}>
                  <Icon name={v.isCurrent ? 'check-circle' : 'history'} size={18} color={v.isCurrent ? c.success : c.textMuted} />
                  <View style={{ marginLeft: 10, flex: 1 }}>
                    <Text style={{ color: c.text, fontSize: 14 }}>
                      {t('detail.versionItem', {
                        no: v.versionNo,
                        size: formatBytes(v.sizeBytes),
                        date: formatDate(v.createdAt),
                        by: v.createdByName ? ` • ${v.createdByName}` : '',
                      })}
                    </Text>
                    {v.changeNote ? <Text style={{ color: c.textMuted, fontSize: 12, marginTop: 2 }}>{v.changeNote}</Text> : null}
                  </View>
                </View>
              ))}
            </View>
          </>
        ) : null}
      </ScrollView>
    </>
  );
}

const styles = StyleSheet.create({
  actions: { flexDirection: 'row', gap: 10, padding: 16 },
  row: { flexDirection: 'row', paddingHorizontal: 16, paddingVertical: 11, borderBottomWidth: StyleSheet.hairlineWidth },
  label: { width: 120, fontSize: 13 },
  tags: { flexDirection: 'row', flexWrap: 'wrap', gap: 6 },
  tag: { borderRadius: 10, paddingHorizontal: 8, paddingVertical: 2, fontSize: 12, overflow: 'hidden' },
  crumbs: { flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center' },
  version: { flexDirection: 'row', alignItems: 'center', paddingHorizontal: 16, paddingVertical: 10, borderBottomWidth: StyleSheet.hairlineWidth },
});
