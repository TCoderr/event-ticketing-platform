/** Sunucu yanıtı + alındığı an (monoton saat). Geri sayım `serverTime` ile bu andan kurulur. */
export interface Stamped<T> {
  data: T;
  receivedAt: number;
}

export function stamp<T>(data: T): Stamped<T> {
  return { data, receivedAt: performance.now() };
}
