import { Debit, Payee, Rule } from '../model';
import { buildOffer } from '../payeeOffer';

const payee = (id: string, name = id): Payee => ({ id, name, initials: name.slice(0, 2).toUpperCase(), iban: '' });
const debit = (payeeId: string, amountEUR: number, timestamp: string, n = 1): Debit => ({
  id: `${payeeId}-${n}-${timestamp}`,
  payeeId,
  amountEUR,
  timestamp,
  hasTime: false,
  paymentType: 'Card payment',
  reference: '',
});

describe('buildOffer', () => {
  it('lists payees without a rule that were not there before the attempt', () => {
    const rows = buildOffer(
      [payee('a'), payee('b'), payee('c')],
      [debit('a', 5, '2026-10-01'), debit('b', 6, '2026-10-02'), debit('c', 7, '2026-10-03')],
      [{ payeeId: 'a', classification: 'bad' } as Rule],
      new Set(['b']),
    );
    expect(rows.map(r => r.payee.id)).toEqual(['c']);
  });

  it('lists a payee id that comes twice once', () => {
    const rows = buildOffer([payee('a'), payee('a')], [debit('a', 5, '2026-10-01')], [], new Set());
    expect(rows).toHaveLength(1);
  });

  it('leaves out a payee with no debit', () => {
    expect(buildOffer([payee('a')], [], [], new Set())).toEqual([]);
  });

  it('counts the charges, says "up to" only when the amounts differ and sorts the newest first', () => {
    const rows = buildOffer(
      [payee('a', 'Alpha'), payee('b', 'Beta')],
      [
        debit('a', 9.99, '2026-09-01', 1),
        debit('a', 9.99, '2026-09-08', 2),
        debit('b', 10, '2026-10-01', 1),
        debit('b', 34.21, '2026-08-01', 2),
      ],
      [],
      new Set(),
    );
    expect(rows.map(r => r.payee.id)).toEqual(['b', 'a']);
    expect(rows[0]).toMatchObject({ charges: 2, amountEUR: 34.21, varies: true });
    expect(rows[1]).toMatchObject({ charges: 2, amountEUR: 9.99, varies: false });
  });
});
