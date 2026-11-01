import { describe, expect, it } from 'vitest';
import { cashChange, commandTotal, isMoney, queuedOwnerHeaders, scopeFor, sufficientCash, validateSaleCommand } from './offline';
import type { SaleCommand } from './retail-types';

const id = '11111111-1111-4111-8111-111111111111';
const command = (): SaleCommand => ({ branchId: id, shiftId: id, priceTier: 'Retail', clientCreatedAt: '2026-11-01T01:02:03.000Z', lines: [{ productId: id, quantity: 3, unitPrice: .1, priceVersion: 1 }], cashTendered: 1 });

describe('durable sale intent boundary', () => {
  it('requires the stable organization identity and partitions different owners', () => {
    expect(() => scopeFor({ id: 'user-a' })).toThrow('Identitas organisasi');
    expect(scopeFor({ id: 'user-a', organizationId: 'org-a' })).not.toBe(scopeFor({ id: 'user-b', organizationId: 'org-a' }));
    expect(scopeFor({ id: 'user-a', organizationId: 'org-a' })).not.toBe(scopeFor({ id: 'user-a', organizationId: 'org-b' }));
  });
  it('binds mutation headers to the persisted owner and rejects malformed partitions', () => {
    const actor = '22222222-2222-4222-8222-222222222222';
    expect(queuedOwnerHeaders(`${id}:${actor}`, 'operation-key')).toEqual({ 'Idempotency-Key': 'operation-key', 'X-Retail-Actor': actor, 'X-Retail-Organization': id });
    expect(() => queuedOwnerHeaders(`${id}:${actor}:extra`, 'operation-key')).toThrow('pemilik');
  });
  it('adds integer cents instead of accumulating binary fractional totals', () => {
    const sale = command();
    expect(commandTotal(sale)).toBe(.3);
    expect(() => validateSaleCommand(sale)).not.toThrow();
  });
  it('compares and returns fractional cash using integer cents', () => {
    const sale = command();
    sale.lines = [{ ...sale.lines[0], quantity: 1, unitPrice: .1 }, { ...sale.lines[0], productId: '22222222-2222-4222-8222-222222222222', quantity: 1, unitPrice: .2 }];
    expect(commandTotal(sale)).toBe(.3);
    expect(sufficientCash(.3, commandTotal(sale))).toBe(true);
    expect(sufficientCash(.29, commandTotal(sale))).toBe(false);
    expect(cashChange(.3, .2)).toBe(.1);
    expect(isMoney(.30000000000000004)).toBe(false);
  });
  it('rejects duplicate products and invalid quantities before any browser persistence', () => {
    const sale = command(); sale.lines.push({ ...sale.lines[0] });
    expect(() => validateSaleCommand(sale)).toThrow('produk berbeda');
    sale.lines.pop(); sale.lines[0].quantity = .5;
    expect(() => validateSaleCommand(sale)).toThrow('Jumlah');
  });
  it('rejects stale-shaped prices and underfunded or nonfinite cash before persistence', () => {
    const sale = command(); sale.lines[0].priceVersion = 0;
    expect(() => validateSaleCommand(sale)).toThrow('versi');
    sale.lines[0].priceVersion = 1; sale.lines[0].unitPrice = .123;
    expect(() => validateSaleCommand(sale)).toThrow('harga');
    sale.lines[0].unitPrice = .1; sale.cashTendered = .29;
    expect(() => validateSaleCommand(sale)).toThrow('tunai');
    sale.cashTendered = Infinity;
    expect(() => validateSaleCommand(sale)).toThrow('tunai');
  });
});
