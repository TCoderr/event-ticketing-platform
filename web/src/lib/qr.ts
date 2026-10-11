import qrcode from 'qrcode-generator';

/** QR modül matrisi. Yerelde üretilir; token hiçbir harici servise gönderilmez. */
export interface QrMatrix {
  readonly size: number;
  isDark(row: number, column: number): boolean;
}

/** Bilet token'ı için QR üretir. İçerik yalnız token'dır: ad, e-posta veya başka kişisel veri eklenmez. */
export function createQrMatrix(text: string): QrMatrix {
  const qr = qrcode(0, 'M');
  qr.addData(text, 'Byte');
  qr.make();
  const size = qr.getModuleCount();
  return { size, isDark: (row, column) => qr.isDark(row, column) };
}

export const QUIET_ZONE_MODULES = 4;

/** SVG `path` verisi: her koyu modül 1×1 birim kare. Çevredeki sessiz bölge viewBox ile verilir. */
export function qrToSvgPath(matrix: QrMatrix): string {
  const parts: string[] = [];
  for (let row = 0; row < matrix.size; row++) {
    let runStart = -1;
    for (let column = 0; column <= matrix.size; column++) {
      const dark = column < matrix.size && matrix.isDark(row, column);
      if (dark && runStart < 0) runStart = column;
      if (!dark && runStart >= 0) {
        parts.push(`M${runStart} ${row}h${column - runStart}v1h-${column - runStart}z`);
        runStart = -1;
      }
    }
  }
  return parts.join('');
}

/** Matrisi RGBA piksellere çevirir (testte gerçek bir çözücüye vermek için). */
export function rasterizeQr(matrix: QrMatrix, scale = 4): { data: Uint8ClampedArray; width: number; height: number } {
  const side = (matrix.size + QUIET_ZONE_MODULES * 2) * scale;
  const data = new Uint8ClampedArray(side * side * 4).fill(255);
  for (let row = 0; row < matrix.size; row++) {
    for (let column = 0; column < matrix.size; column++) {
      if (!matrix.isDark(row, column)) continue;
      for (let dy = 0; dy < scale; dy++) {
        for (let dx = 0; dx < scale; dx++) {
          const x = (column + QUIET_ZONE_MODULES) * scale + dx;
          const y = (row + QUIET_ZONE_MODULES) * scale + dy;
          const offset = (y * side + x) * 4;
          data[offset] = 0;
          data[offset + 1] = 0;
          data[offset + 2] = 0;
        }
      }
    }
  }
  return { data, width: side, height: side };
}
