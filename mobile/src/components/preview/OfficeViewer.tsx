import { useQuery } from '@tanstack/react-query';
import { useEffect, useMemo, useState } from 'react';
import { ActivityIndicator, StyleSheet, Text, View } from 'react-native';
import { WebView, type WebViewMessageEvent } from 'react-native-webview';

import { errorMessage, serverUrl } from '@/api/client';
import { driveApi } from '@/api/endpoints';
import type { OfficeConfig } from '@/api/types';
import { t } from '@/i18n';
import { useColors } from '@/theme';
import { Button, ErrorState } from '../ui';

/** JSON for embedding in an inline <script> (avoid closing the script tag). */
const toScriptJson = (v: unknown) => JSON.stringify(v).replace(/</g, '\\u003c').replace(/\u2028/g, '\\u2028').replace(/\u2029/g, '\\u2029');

function buildHtml(cfg: OfficeConfig): string {
  // The server signs the config with type 'mobile' (?mobile=true); it is passed unchanged.
  return `<!doctype html>
<html><head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no" />
<style>html,body{margin:0;padding:0;height:100%;overflow:hidden;background:#fff}#editor{width:100%;height:100%}</style>
</head><body>
<div id="editor"></div>
<script>
  function post(m){ try { window.ReactNativeWebView.postMessage(JSON.stringify(m)); } catch(e){} }
  window.onerror = function(msg){ post({ type: 'error', message: String(msg) }); };
  var cfg = ${toScriptJson(cfg.config)};
  cfg.events = {
    onAppReady: function(){ post({ type: 'ready' }); },
    onDocumentReady: function(){ post({ type: 'ready' }); },
    onError: function(e){ post({ type: 'error', code: e && e.data && e.data.errorCode, message: e && e.data && e.data.errorDescription }); }
  };
  var s = document.createElement('script');
  s.src = ${toScriptJson(cfg.apiScriptUrl)};
  s.onload = function(){
    if (!window.DocsAPI) { post({ type: 'script-error' }); return; }
    try { new DocsAPI.DocEditor('editor', cfg); } catch(e) { post({ type: 'error', message: String(e) }); }
  };
  s.onerror = function(){ post({ type: 'script-error' }); };
  document.head.appendChild(s);
</script>
</body></html>`;
}

/** #76: PDF/Word/Excel/PowerPoint via ONLYOFFICE (view mode) inside a WebView. */
export function OfficeViewer({ fileId, onDownload }: { fileId: string; onDownload?: () => void }) {
  const c = useColors();
  const cfg = useQuery({ queryKey: ['office-config', fileId], queryFn: () => driveApi.officeConfig(fileId), staleTime: 0, gcTime: 0 });
  const [ready, setReady] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const html = useMemo(() => (cfg.data ? buildHtml(cfg.data) : ''), [cfg.data]);

  // Never keep the loading overlay forever if the editor does not report readiness.
  useEffect(() => {
    if (!cfg.data || ready) return;
    const id = setTimeout(() => setReady(true), 20_000);
    return () => clearTimeout(id);
  }, [cfg.data, ready]);

  const onMessage = (e: WebViewMessageEvent) => {
    let msg: { type?: string; code?: number; message?: string };
    try {
      msg = JSON.parse(e.nativeEvent.data);
    } catch {
      return;
    }
    if (msg.type === 'ready') setReady(true);
    else if (msg.type === 'script-error') setError(t('viewer.officeUnavailable'));
    else if (msg.type === 'error') {
      setError(t('viewer.officeError', { error: msg.message ?? String(msg.code ?? '') }));
    }
  };

  if (cfg.isLoading) {
    return (
      <View style={styles.center}>
        <ActivityIndicator color={c.primary} size="large" />
        <Text style={{ color: c.textMuted, marginTop: 10 }}>{t('viewer.loadingOffice')}</Text>
      </View>
    );
  }
  if (cfg.isError || error) {
    return (
      <View style={{ flex: 1, justifyContent: 'center' }}>
        <ErrorState message={error ?? errorMessage(cfg.error)} onRetry={() => { setError(null); setReady(false); cfg.refetch(); }} />
        {onDownload ? <Button title={t('actions.download')} icon="download" variant="secondary" onPress={onDownload} style={{ alignSelf: 'center' }} /> : null}
      </View>
    );
  }
  return (
    <View style={{ flex: 1 }}>
      <WebView
        originWhitelist={['*']}
        source={{ html, baseUrl: serverUrl() }}
        onMessage={onMessage}
        javaScriptEnabled
        domStorageEnabled
        thirdPartyCookiesEnabled
        mixedContentMode="compatibility"
        setSupportMultipleWindows={false}
        allowsInlineMediaPlayback
        style={{ flex: 1, backgroundColor: '#fff' }}
        onError={(ev) => setError(t('viewer.officeError', { error: ev.nativeEvent.description }))}
      />
      {!ready ? (
        <View style={[StyleSheet.absoluteFill, styles.center, { backgroundColor: c.background }]} pointerEvents="none">
          <ActivityIndicator color={c.primary} size="large" />
          <Text style={{ color: c.textMuted, marginTop: 10 }}>{t('viewer.loadingOffice')}</Text>
        </View>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  center: { flex: 1, alignItems: 'center', justifyContent: 'center' },
});
