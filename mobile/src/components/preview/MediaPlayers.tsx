import { useAudioPlayer, useAudioPlayerStatus } from 'expo-audio';
import { useEvent } from 'expo';
import { useVideoPlayer, VideoView } from 'expo-video';
import { Pressable, StyleSheet, Text, View } from 'react-native';

import { t } from '@/i18n';
import { useColors } from '@/theme';
import { formatDuration } from '@/utils/format';
import { Icon } from '../ui';

/** Video playback from the signed (Range-enabled) URL (#76). */
export function VideoPlayerView({ uri, autoPlay = true }: { uri: string; autoPlay?: boolean }) {
  const player = useVideoPlayer(uri, (p) => {
    if (autoPlay) p.play();
  });
  const { status, error } = useEvent(player, 'statusChange', { status: player.status, error: undefined });
  return (
    <View style={styles.fill}>
      <VideoView player={player} style={styles.fill} nativeControls contentFit="contain" fullscreenOptions={{ enable: true }} allowsPictureInPicture />
      {status === 'error' ? (
        <View style={styles.errorBox}>
          <Text style={{ color: '#fff', textAlign: 'center' }}>
            {t('viewer.videoError')}
            {error?.message ? `\n${error.message}` : ''}
          </Text>
        </View>
      ) : null}
    </View>
  );
}

/** Audio playback with play/pause, seek ±15 s and progress (#76). */
export function AudioPlayerView({ uri, title }: { uri: string; title: string }) {
  const c = useColors();
  const player = useAudioPlayer({ uri });
  const status = useAudioPlayerStatus(player);
  const progress = status.duration > 0 ? status.currentTime / status.duration : 0;
  const seek = (delta: number) => player.seekTo(Math.max(0, Math.min(status.duration || 0, status.currentTime + delta)));
  return (
    <View style={[styles.audio, { backgroundColor: c.surface, borderColor: c.border }]}>
      <Icon name="music-circle" size={72} color={c.primary} />
      <Text numberOfLines={2} style={{ color: c.text, fontSize: 16, fontWeight: '600', marginTop: 10, textAlign: 'center' }}>
        {title}
      </Text>
      <View style={[styles.track, { backgroundColor: c.surfaceAlt }]}>
        <View style={{ width: `${progress * 100}%`, height: '100%', backgroundColor: c.primary }} />
      </View>
      <Text style={{ color: c.textMuted, fontSize: 12 }}>
        {formatDuration(status.currentTime) || '0:00'} / {formatDuration(status.duration) || '--:--'}
      </Text>
      <View style={styles.controls}>
        <Pressable onPress={() => seek(-15)} hitSlop={10}>
          <Icon name="rewind-15" size={34} color={c.text} />
        </Pressable>
        <Pressable
          onPress={() => (status.playing ? player.pause() : player.play())}
          style={[styles.play, { backgroundColor: c.primary }]}
          accessibilityLabel={status.playing ? t('viewer.pause') : t('viewer.play')}
        >
          <Icon name={status.playing ? 'pause' : 'play'} size={36} color={c.primaryText} />
        </Pressable>
        <Pressable onPress={() => seek(15)} hitSlop={10}>
          <Icon name="fast-forward-15" size={34} color={c.text} />
        </Pressable>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  fill: { flex: 1, width: '100%', backgroundColor: '#000' },
  errorBox: { ...StyleSheet.absoluteFill, alignItems: 'center', justifyContent: 'center', padding: 24 },
  audio: { margin: 16, borderRadius: 16, borderWidth: StyleSheet.hairlineWidth, padding: 24, alignItems: 'center' },
  track: { alignSelf: 'stretch', height: 6, borderRadius: 3, overflow: 'hidden', marginTop: 20, marginBottom: 8 },
  controls: { flexDirection: 'row', alignItems: 'center', gap: 28, marginTop: 16 },
  play: { width: 64, height: 64, borderRadius: 32, alignItems: 'center', justifyContent: 'center' },
});
