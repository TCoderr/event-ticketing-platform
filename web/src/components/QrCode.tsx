import { useMemo } from 'react';
import { createQrMatrix, QUIET_ZONE_MODULES, qrToSvgPath } from '../lib/qr';

/** Bilet QR'ı: tarayıcıda, yerelde üretilen SVG. Token hiçbir harici servise gönderilmez. */
export function QrCode({ token, label }: { token: string; label: string }) {
  const { path, side } = useMemo(() => {
    const matrix = createQrMatrix(token);
    return { path: qrToSvgPath(matrix), side: matrix.size + QUIET_ZONE_MODULES * 2 };
  }, [token]);
  return (
    <svg
      className="qr"
      role="img"
      aria-label={label}
      viewBox={`${-QUIET_ZONE_MODULES} ${-QUIET_ZONE_MODULES} ${side} ${side}`}
      shapeRendering="crispEdges"
      data-testid="ticket-qr"
    >
      <rect x={-QUIET_ZONE_MODULES} y={-QUIET_ZONE_MODULES} width={side} height={side} fill="#fff" />
      <path d={path} fill="#000" />
    </svg>
  );
}
