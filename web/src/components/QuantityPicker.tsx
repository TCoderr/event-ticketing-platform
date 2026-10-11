import { formatMoney } from '../lib/format';
import type { PublicCategory } from '../lib/types';
import { Icon } from './Icon';
import { Status } from './ui';

export type Quantities = Readonly<Record<string, number>>;

export const totalQuantity = (quantities: Quantities) => Object.values(quantities).reduce((sum, n) => sum + n, 0);

export interface QuantityChange {
  quantities: Record<string, number>;
  message: string | null;
}

/** Kategori adedini bir artırır/azaltır. Toplam `max`'ı ve kategorinin kalan adedini (ipucu) aşamaz. */
export function changeQuantity(quantities: Quantities, category: PublicCategory, delta: 1 | -1, max: number): QuantityChange {
  const current = quantities[category.id] ?? 0;
  if (delta < 0) {
    return { quantities: { ...quantities, [category.id]: Math.max(0, current - 1) }, message: null };
  }
  if (totalQuantity(quantities) >= max) {
    return { quantities: { ...quantities }, message: `Tek siparişte en fazla ${max} bilet alınabilir.` };
  }
  if (current >= category.available) {
    return { quantities: { ...quantities }, message: `${category.name} kategorisinde yalnız ${category.available} bilet kaldı.` };
  }
  return { quantities: { ...quantities, [category.id]: current + 1 }, message: null };
}

interface QuantityPickerProps {
  categories: readonly PublicCategory[];
  quantities: Quantities;
  max: number;
  onChange: (category: PublicCategory, delta: 1 | -1) => void;
  disabled?: boolean;
}

/** Genel giriş: kategori başına adet seçici. */
export function QuantityPicker({ categories, quantities, max, onChange, disabled = false }: QuantityPickerProps) {
  const total = totalQuantity(quantities);
  return (
    <ul className="cats">
      {categories.map((category) => {
        const quantity = quantities[category.id] ?? 0;
        const soldOut = category.available <= 0 && quantity === 0;
        const cannotDecrease = disabled || quantity === 0;
        const cannotIncrease = disabled || total >= max || quantity >= category.available;
        return (
          <li key={category.id}>
            <div>
              <h2 className="c-name">{category.name}</h2>
              <p className="c-left">
                {soldOut ? (
                  <Status tone="mute" icon="cross">Tükendi</Status>
                ) : category.available <= 10 ? (
                  <Status tone="warn" icon="warn">Son {category.available} bilet</Status>
                ) : (
                  <span className="t-mute">Yaklaşık {category.available} bilet kaldı</span>
                )}
              </p>
            </div>
            <div className="c-price">{formatMoney(category.priceMinor)}</div>
            <div className="c-ctl">
              {soldOut ? (
                <span className="t-mute">Seçilemez</span>
              ) : (
                <div className="stepper" role="group" aria-label={`${category.name} adedi`}>
                  <button
                    type="button"
                    aria-label={`${category.name}: bir azalt`}
                    aria-disabled={cannotDecrease}
                    onClick={() => !cannotDecrease && onChange(category, -1)}
                  >
                    <Icon name="minus" />
                  </button>
                  <output aria-live="polite" aria-label={`${category.name} adedi`}>{quantity}</output>
                  <button
                    type="button"
                    aria-label={`${category.name}: bir artır`}
                    aria-disabled={cannotIncrease}
                    onClick={() => !disabled && onChange(category, 1)}
                  >
                    <Icon name="plus" />
                  </button>
                </div>
              )}
            </div>
          </li>
        );
      })}
    </ul>
  );
}
