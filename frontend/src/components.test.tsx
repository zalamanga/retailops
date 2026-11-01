import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Field, Form, Modal, Pagination } from './components';
import { ApiError, clearCsrf, write } from './api';

afterEach(() => {
  clearCsrf();
  vi.unstubAllGlobals();
});

describe('field accessible names', () => {
  it('labels a select by the field text without adding its option labels', () => {
    render(<Field label="Cabang"><select name="branchId"><option value="">Pilih cabang</option><option value="north">Cabang Utara</option></select></Field>);
    const select = screen.getByLabelText('Cabang', { exact: true });
    expect(select).toHaveAccessibleName('Cabang');
    expect(screen.getByRole('combobox', { name: 'Cabang' })).toBe(select);
  });

  it('preserves explicit aria-label and aria-labelledby provided by a control', () => {
    render(<>
      <Field label="Label pembungkus"><input aria-label="Nama kontrol eksplisit" /></Field>
      <span id="external-field-name">Nama dari label luar</span>
      <Field label="Label pembungkus lain"><input aria-labelledby="external-field-name" /></Field>
    </>);
    expect(screen.getByRole('textbox', { name: 'Nama kontrol eksplisit' })).not.toHaveAttribute('aria-labelledby');
    expect(screen.getByRole('textbox', { name: 'Nama dari label luar' })).toHaveAttribute('aria-labelledby', 'external-field-name');
  });
});

describe('dialog keyboard focus', () => {
  it('returns focus to the opening button after a dialog cancellation', async () => {
    const prototype = HTMLDialogElement.prototype;
    const originalOpen = prototype.showModal;
    const originalClose = prototype.close;
    prototype.showModal = function () {
      this.setAttribute('open', '');
      this.querySelector<HTMLInputElement>('input')?.focus();
    };
    prototype.close = function () {
      this.removeAttribute('open');
    };
    function DialogExample() {
      const [open, setOpen] = useState(false);
      return (
        <>
          <button type="button" onClick={() => setOpen(true)}>
            Add team member
          </button>
          {open && (
            <Modal title="New team member" onClose={() => setOpen(false)}>
              <Field label="Full name">
                <input autoFocus />
              </Field>
            </Modal>
          )}
        </>
      );
    }
    const view = render(<DialogExample />);
    try {
      const person = userEvent.setup();
      const opener = screen.getByRole('button', { name: 'Add team member' });
      await person.click(opener);
      expect(screen.getByRole('textbox', { name: 'Full name' })).toHaveFocus();
      fireEvent(screen.getByRole('dialog'), new Event('cancel', { cancelable: true }));
      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
      expect(opener).toHaveFocus();
    } finally {
      view.unmount();
      prototype.showModal = originalOpen;
      prototype.close = originalClose;
    }
  });
});

describe('form failure recovery', () => {
  it('renders an API conflict with null field errors and lets the user correct and retry it', async () => {
    const reply = (body: unknown, status = 200) => ({
      ok: status >= 200 && status < 300,
      status,
      json: async () => body,
    });
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(reply({ token: 'request-token' }))
      .mockResolvedValueOnce(
        reply(
          {
            title: 'Insufficient stock',
            detail: 'Only 80 units are available in this warehouse.',
            status: 409,
            errors: null,
            traceId: 'stock-conflict',
          },
          409,
        ),
      )
      .mockResolvedValueOnce(reply({ movementId: 'c645293c-ebdf-4511-a9ee-f1ba75054dc5' }));
    vi.stubGlobal('fetch', fetchMock);
    const saved = vi.fn();
    const person = userEvent.setup();
    render(
      <Form
        submitLabel="Record adjustment"
        submit={async (data) => {
          await write('/inventory/adjustments', {
            productId: '4d39bc5a-5631-4aa2-a385-1a0e16177867',
            warehouseId: 'cc013af0-aedf-4d37-953c-5951718434e2',
            quantity: Number(data.get('quantity')),
            reason: String(data.get('reason')),
          });
          saved();
        }}
      >
        <Field label="Quantity change">
          <input name="quantity" type="number" required step={1} defaultValue={-9999} />
        </Field>
        <Field label="Reason">
          <input name="reason" required defaultValue="Physical stock reconciliation" />
        </Field>
      </Form>,
    );
    await person.click(screen.getByRole('button', { name: 'Record adjustment' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Only 80 units are available in this warehouse.',
    );
    expect(screen.getByRole('spinbutton', { name: 'Quantity change' })).toHaveValue(-9999);
    expect(screen.getByRole('textbox', { name: 'Reason' })).toHaveValue(
      'Physical stock reconciliation',
    );
    expect(screen.getByRole('button', { name: 'Record adjustment' })).toBeEnabled();
    await person.clear(screen.getByRole('spinbutton', { name: 'Quantity change' }));
    await person.type(screen.getByRole('spinbutton', { name: 'Quantity change' }), '-3');
    await person.click(screen.getByRole('button', { name: 'Record adjustment' }));
    await waitFor(() => expect(saved).toHaveBeenCalledOnce());
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(JSON.parse(String((fetchMock.mock.calls[2][1] as RequestInit).body))).toMatchObject({
      quantity: -3,
      reason: 'Physical stock reconciliation',
    });
  });
  it('associates server field errors with the matching control', async () => {
    const user = userEvent.setup();
    render(
      <Form
        submit={async () => {
          throw new ApiError('Correct the fields.', 400, { sku: ['Choose a unique SKU.'] });
        }}
      >
        <Field label="SKU">
          <input name="sku" />
        </Field>
      </Form>,
    );
    await user.click(screen.getByRole('button', { name: 'Save changes' }));
    expect(await screen.findByRole('textbox', { name: 'SKU' })).toHaveAttribute(
      'aria-invalid',
      'true',
    );
    expect(screen.getByRole('textbox', { name: 'SKU' })).toHaveAccessibleDescription(
      'Choose a unique SKU.',
    );
  });
  it('keeps entered values and enables retry after a failed mutation', async () => {
    const user = userEvent.setup();
    const submit = vi
      .fn()
      .mockRejectedValueOnce(new Error('A concurrent delivery changed this order.'))
      .mockResolvedValueOnce(undefined);
    render(
      <Form submit={submit} submitLabel="Record receipt">
        <Field label="Delivery notes">
          <input name="notes" />
        </Field>
      </Form>,
    );
    await user.type(screen.getByRole('textbox', { name: 'Delivery notes' }), 'Gate check complete');
    await user.click(screen.getByRole('button', { name: 'Record receipt' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'A concurrent delivery changed this order.',
    );
    expect(screen.getByRole('textbox')).toHaveValue('Gate check complete');
    expect(screen.getByRole('button', { name: 'Record receipt' })).toBeEnabled();
    await user.click(screen.getByRole('button', { name: 'Record receipt' }));
    expect(submit).toHaveBeenCalledTimes(2);
    expect(submit.mock.calls[1][0].get('notes')).toBe('Gate check complete');
  });
});
describe('server pagination', () => {
  it('prevents moving beyond the final page and preserves previous navigation', async () => {
    const onPage = vi.fn();
    const user = userEvent.setup();
    render(
      <Pagination
        result={{ items: [], total: 23, page: 2, pageSize: 20 }}
        page={2}
        onPage={onPage}
      />,
    );
    expect(screen.getByText('21–23 of 23 records')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Previous' }));
    expect(onPage).toHaveBeenCalledWith(1);
  });
});
