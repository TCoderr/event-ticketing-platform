import jsQR from 'jsqr';
import { useCallback, useEffect, useRef, useState } from 'react';
import { Icon } from './Icon';

type CameraState =
  | { kind: 'off' }
  | { kind: 'starting' }
  | { kind: 'on' }
  | { kind: 'unsupported' }
  | { kind: 'denied' }
  | { kind: 'no-camera' }
  | { kind: 'failed' };

const MESSAGES: Record<Exclude<CameraState['kind'], 'on'>, string> = {
  off: 'Kamera kapalı',
  starting: 'Kamera açılıyor… Tarayıcı izin isteyebilir.',
  unsupported: 'Bu tarayıcı kamera erişimini desteklemiyor. Kodu aşağıdan elle gir.',
  denied: 'Kamera izni verilmedi. Tarayıcının site ayarlarından kameraya izin ver ya da kodu aşağıdan elle gir.',
  'no-camera': 'Bu cihazda kullanılabilir kamera bulunamadı. Kodu aşağıdan elle gir.',
  failed: 'Kamera açılamadı (başka bir uygulama kullanıyor olabilir). Kodu aşağıdan elle gir.',
};

const SCAN_INTERVAL_MS = 180;
const MAX_FRAME = 640;

interface CameraScannerProps {
  /** Çözülen QR içeriği. Aynı kod arka arkaya okunursa yeniden bildirilmez. */
  onScan: (text: string) => void;
  /** Sonuç ekrandayken tarama durur (aynı bilet iki kez gönderilmesin). */
  paused: boolean;
}

/**
 * Kamera ile QR tarama: getUserMedia + jsQR, tamamen tarayıcıda. Görüntü hiçbir yere gönderilmez.
 * İzin reddi ve desteklenmeyen tarayıcı açık metinle bildirilir; elle giriş her zaman kullanılabilir alternatiftir.
 */
export function CameraScanner({ onScan, paused }: CameraScannerProps) {
  const [state, setState] = useState<CameraState>({ kind: 'off' });
  const videoRef = useRef<HTMLVideoElement>(null);
  const streamRef = useRef<MediaStream | null>(null);
  const lastRef = useRef<{ text: string; at: number } | null>(null);
  const pausedRef = useRef(paused);
  const onScanRef = useRef(onScan);

  useEffect(() => {
    pausedRef.current = paused;
    onScanRef.current = onScan;
  }, [paused, onScan]);

  const stop = useCallback(() => {
    streamRef.current?.getTracks().forEach((track) => track.stop());
    streamRef.current = null;
    if (videoRef.current) videoRef.current.srcObject = null;
  }, []);

  useEffect(() => stop, [stop]);

  const start = async () => {
    if (!navigator.mediaDevices?.getUserMedia) {
      setState({ kind: 'unsupported' });
      return;
    }
    setState({ kind: 'starting' });
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: { ideal: 'environment' } }, audio: false });
      streamRef.current = stream;
      setState({ kind: 'on' });
    } catch (error) {
      const name = error instanceof Error ? error.name : '';
      if (name === 'NotAllowedError' || name === 'SecurityError') setState({ kind: 'denied' });
      else if (name === 'NotFoundError' || name === 'OverconstrainedError') setState({ kind: 'no-camera' });
      else if (name === 'NotSupportedError' || name === 'TypeError') setState({ kind: 'unsupported' });
      else setState({ kind: 'failed' });
    }
  };

  const on = state.kind === 'on';
  useEffect(() => {
    if (!on) return;
    const video = videoRef.current;
    const stream = streamRef.current;
    if (!video || !stream) return;
    video.srcObject = stream;
    void video.play().catch(() => undefined);

    const canvas = document.createElement('canvas');
    const context = canvas.getContext('2d', { willReadFrequently: true });
    const timer = setInterval(() => {
      if (pausedRef.current || !context || video.readyState < 2 || video.videoWidth === 0) return;
      const scale = Math.min(1, MAX_FRAME / Math.max(video.videoWidth, video.videoHeight));
      canvas.width = Math.round(video.videoWidth * scale);
      canvas.height = Math.round(video.videoHeight * scale);
      context.drawImage(video, 0, 0, canvas.width, canvas.height);
      const image = context.getImageData(0, 0, canvas.width, canvas.height);
      const code = jsQR(image.data, image.width, image.height, { inversionAttempts: 'dontInvert' });
      const text = code?.data.trim();
      if (!text) return;
      const now = Date.now();
      // Aynı kod kadrajda kalırsa 4 sn boyunca yeniden gönderilmez.
      if (lastRef.current && lastRef.current.text === text && now - lastRef.current.at < 4000) return;
      lastRef.current = { text, at: now };
      onScanRef.current(text);
    }, SCAN_INTERVAL_MS);
    return () => clearInterval(timer);
  }, [on]);

  const turnOff = () => {
    stop();
    setState({ kind: 'off' });
  };

  return (
    <div className="scan">
      <div className="vf">
        {/* Canlı kamera önizlemesi: ses yok, altyazı gerektiren içerik değil. */}
        {on && <video ref={videoRef} muted playsInline aria-label="Kamera önizlemesi" data-testid="camera-video" />}
        {!on && <span role="status" data-testid="camera-status">{MESSAGES[state.kind]}</span>}
      </div>
      {on && <p className="g-hint" role="status" data-testid="camera-status">{paused ? 'Tarama duraklatıldı: sonuç ekranda.' : 'Kamera açık. Biletteki QR kodu kadraja getir.'}</p>}
      {on ? (
        <button type="button" className="g-btn" onClick={turnOff}>Kamerayı kapat</button>
      ) : (
        <button type="button" className="g-btn" onClick={() => void start()} disabled={state.kind === 'starting'}>
          <Icon name="scan" />{state.kind === 'off' || state.kind === 'starting' ? 'Kamerayı aç' : 'Kamerayı yeniden dene'}
        </button>
      )}
    </div>
  );
}
