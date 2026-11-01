import { describe, expect, it } from 'vitest';
import { canMaintain, canOperate, date, passwordMessage, validLines } from './format';

describe('purchase order validation', () => {
  it('rejects duplicate product lines rather than double-counting them', () => {
    expect(
      validLines([
        { productId: 'same', quantity: 3, unitCost: 1500 },
        { productId: 'same', quantity: 2, unitCost: 1500 },
      ]),
    ).toContain('only once');
  });
  it('accepts zero cost and integer units but rejects fractional units and unsupported precision', () => {
    expect(validLines([{ productId: 'one', quantity: 1, unitCost: 0 }])).toBe('');
    expect(validLines([{ productId: 'one', quantity: 1.5, unitCost: 12 }])).not.toBe('');
    expect(validLines([{ productId: 'one', quantity: 1, unitCost: 12.345 }])).not.toBe('');
    expect(validLines([{ productId: 'one', quantity: 1, unitCost: 12.34 }])).toBe('');
  });
  it('requires between one and fifty complete lines', () => {
    expect(validLines([])).not.toBe('');
    expect(
      validLines(
        Array.from({ length: 51 }, (_, index) => ({
          productId: String(index),
          quantity: 1,
          unitCost: 0,
        })),
      ),
    ).not.toBe('');
  });
});
describe('access and credential guidance', () => {
  it('distinguishes catalog maintenance from warehouse operations', () => {
    expect(canMaintain('Operator')).toBe(false);
    expect(canOperate('Operator')).toBe(true);
    expect(canMaintain('Manager')).toBe(true);
    expect(canOperate('Viewer')).toBe(false);
  });
  it('requires all four password rules without trimming intentional password characters', () => {
    expect(passwordMessage('Small1')).not.toBe('');
    expect(passwordMessage('longpassword123')).not.toBe('');
    expect(passwordMessage('LONGPASSWORD123')).not.toBe('');
    expect(passwordMessage('LongPasswordWithoutNumber')).not.toBe('');
    expect(passwordMessage('LongPassword123')).toBe('');
    expect(passwordMessage('Aa1' + 'a'.repeat(125))).toBe('');
    expect(passwordMessage('Aa1' + 'a'.repeat(126))).not.toBe('');
  });
  it('handles missing delivery dates explicitly', () => {
    expect(date(null)).toBe('Not set');
  });
});
