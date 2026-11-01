export const money = (value: number) =>
  new Intl.NumberFormat('id-ID', {
    style: 'currency',
    currency: 'IDR',
    maximumFractionDigits: 2,
    minimumFractionDigits: 0,
  }).format(value);
export const number = (value: number) => new Intl.NumberFormat('en').format(value);
export function date(value: string | null | undefined, time = false) {
  if (!value) return 'Not set';
  const parsed = new Date(value.length === 10 ? `${value}T12:00:00` : value);
  if (Number.isNaN(parsed.getTime())) return value;
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: 'medium',
    ...(time ? { timeStyle: 'short' as const } : {}),
  }).format(parsed);
}
export const label = (value: string) => value.replace(/([a-z])([A-Z])/g, '$1 $2');
export function canMaintain(role: string) {
  return role === 'Admin' || role === 'Manager';
}
export function canOperate(role: string) {
  return role !== 'Viewer';
}
export function passwordMessage(value: string) {
  return value.length >= 12 &&
    value.length <= 128 &&
    /[a-z]/.test(value) &&
    /[A-Z]/.test(value) &&
    /\d/.test(value)
    ? ''
    : 'Use 12-128 characters with uppercase, lowercase, and a number.';
}
export function validLines(lines: { productId: string; quantity: number; unitCost: number }[]) {
  if (!lines.length || lines.length > 50) return 'An order needs between 1 and 50 lines.';
  if (new Set(lines.map((l) => l.productId)).size !== lines.length)
    return 'Each product can appear only once. Combine its quantities in one line.';
  if (
    lines.some(
      (l) =>
        !l.productId ||
        !Number.isInteger(l.quantity) ||
        l.quantity <= 0 ||
        l.unitCost < 0 ||
        !Number.isFinite(l.unitCost) ||
        Math.abs(l.unitCost * 100 - Math.round(l.unitCost * 100)) > 0.0001,
    )
  )
    return 'Choose a product, a positive whole quantity, and a nonnegative cost with up to two decimal places for every line.';
  return '';
}
