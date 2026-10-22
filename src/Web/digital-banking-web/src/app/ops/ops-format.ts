/** Fixed identity colors per province; the CSS tokens hold the validated steps for each theme. */
export const PROVINCE_COLORS: Record<string, string> = {
  Punjab: 'var(--series-1)',
  Sindh: 'var(--series-2)',
  'Khyber Pakhtunkhwa': 'var(--series-3)',
  Balochistan: 'var(--series-4)'
};

export const PROVINCE_ORDER = Object.keys(PROVINCE_COLORS);

export const PROVINCE_SHORT: Record<string, string> = {
  Punjab: 'Punjab',
  Sindh: 'Sindh',
  'Khyber Pakhtunkhwa': 'KP',
  Balochistan: 'Balochistan'
};

export const STATUS_COLORS = {
  good: 'var(--good)',
  warning: 'var(--warning)',
  serious: 'var(--serious)',
  critical: 'var(--critical)'
};

export function compact(value: number | null | undefined, digits = 1): string {
  const number = value ?? 0;
  const abs = Math.abs(number);
  if (abs >= 1e9) return `${trim(number / 1e9, digits)}B`;
  if (abs >= 1e6) return `${trim(number / 1e6, digits)}M`;
  if (abs >= 1e3) return `${trim(number / 1e3, digits)}K`;
  return trim(number, abs < 10 && number % 1 !== 0 ? 1 : 0);
}

export function pkr(value: number | null | undefined): string {
  return `Rs ${compact(value, 2)}`;
}

export function grouped(value: number | null | undefined): string {
  return (value ?? 0).toLocaleString('en-PK');
}

export function duration(seconds: number | null | undefined): string {
  const total = Math.max(0, Math.round(seconds ?? 0));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  if (h > 0) return `${h}h ${m}m`;
  if (m > 0) return `${m}m ${s}s`;
  return `${s}s`;
}

export function clock(utc: string): string {
  return new Date(utc).toLocaleTimeString('en-GB', { hour12: false });
}

export function niceMax(value: number): number {
  if (value <= 0) return 1;
  const power = Math.pow(10, Math.floor(Math.log10(value)));
  const scaled = value / power;
  const step = scaled <= 1 ? 1 : scaled <= 2 ? 2 : scaled <= 2.5 ? 2.5 : scaled <= 5 ? 5 : 10;
  return step * power;
}

function trim(value: number, digits: number): string {
  return Number(value.toFixed(digits)).toString();
}
