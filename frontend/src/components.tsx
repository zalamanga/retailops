import {
  cloneElement,
  createContext,
  isValidElement,
  useContext,
  useEffect,
  useId,
  useRef,
  useState,
  type FormEvent,
  type ReactElement,
  type ReactNode,
} from 'react';
import { useQuery } from '@tanstack/react-query';
import { api, ApiError } from './api';
import { label } from './format';
import type { PageResult, User } from './types';

export const SessionContext = createContext<User | null>(null);
const ValidationContext = createContext<Record<string, string[]>>({});
export function useUser() {
  const user = useContext(SessionContext);
  if (!user) throw new Error('An active session is required.');
  return user;
}
export function useResource<T>(key: unknown[], path: string) {
  return useQuery<T>({ queryKey: key, queryFn: () => api<T>(path) });
}
export function Icon({ name }: { name: string }) {
  const paths: Record<string, ReactNode> = {
    overview: (
      <>
        <path d="M3 3h7v7H3zM14 3h7v7h-7zM3 14h7v7H3zM14 14h7v7h-7z" />
      </>
    ),
    orders: (
      <>
        <path d="M7 3h10v3h3v15H4V6h3zM7 3v4h10V3M8 12h8M8 16h5" />
      </>
    ),
    inventory: (
      <>
        <path d="M3 7l9-4 9 4-9 4zM3 7v10l9 4 9-4V7M12 11v10M7 5l10 4" />
      </>
    ),
    products: (
      <>
        <path d="M3 3h8l10 10-8 8L3 11z" />
        <circle cx="7.5" cy="7.5" r="1" />
      </>
    ),
    suppliers: (
      <>
        <path d="M3 7h11v11H3zM14 11h4l3 4v3h-7M6 7V4h5v3" />
        <circle cx="7" cy="19" r="2" />
        <circle cx="18" cy="19" r="2" />
      </>
    ),
    warehouses: (
      <>
        <path d="M3 21V8l9-5 9 5v13M7 21V11h10v10M7 15h10M7 19h10" />
      </>
    ),
    movements: (
      <>
        <path d="M3 7h17l-4-4M21 17H4l4 4M20 7l-4 4M4 17l4-4" />
      </>
    ),
    activity: (
      <>
        <path d="M8 3H4v18h16V3h-4M8 3h8v4H8zM8 12h8M8 16h8" />
      </>
    ),
    team: (
      <>
        <circle cx="9" cy="8" r="3" />
        <path d="M3 21v-3a6 6 0 0112 0v3M17 5a3 3 0 010 6M18 15c2 0 3 2 3 4v2" />
      </>
    ),
    plus: <path d="M12 5v14M5 12h14" />,
    close: <path d="M6 6l12 12M6 18L18 6" />,
    menu: <path d="M4 6h16M4 12h16M4 18h16" />,
    logout: (
      <>
        <path d="M10 3H4v18h6M10 12h11l-4-4M21 12l-4 4" />
      </>
    ),
    search: (
      <>
        <circle cx="10" cy="10" r="6" />
        <path d="M15 15l6 6" />
      </>
    ),
    download: (
      <>
        <path d="M12 3v12l-5-5M12 15l5-5M4 16v5h16v-5" />
      </>
    ),
  };
  return (
    <svg
      width="20"
      height="20"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.7"
      strokeLinecap="square"
      strokeLinejoin="miter"
      aria-hidden="true"
    >
      {paths[name]}
    </svg>
  );
}
export function PageHeading({
  title,
  description,
  action,
}: {
  title: string;
  description: string;
  action?: ReactNode;
}) {
  return (
    <header className="page-heading">
      <div>
        <h1>{title}</h1>
        <p>{description}</p>
      </div>
      {action && <div className="heading-actions">{action}</div>}
    </header>
  );
}
export function ErrorNotice({ error, retry }: { error: unknown; retry?: () => void }) {
  if (!error) return null;
  return (
    <div className="notice error" role="alert">
      <strong>
        {error instanceof ApiError && error.status === 403
          ? 'This action is not available to your role.'
          : 'We could not complete that request.'}
      </strong>
      <p>{error instanceof Error ? error.message : 'An unexpected error occurred.'}</p>
      {error instanceof ApiError && Object.keys(error.fields).length > 0 && (
        <ul>
          {Object.entries(error.fields).map(([field, values]) => (
            <li key={field}>
              {label(field)}: {values.join(' ')}
            </li>
          ))}
        </ul>
      )}
      {error instanceof ApiError && error.traceId && (
        <small>Request reference: {error.traceId}</small>
      )}
      {retry && (
        <button className="button secondary" onClick={retry} type="button">
          Retry request
        </button>
      )}
    </div>
  );
}
export function Loading({ text = 'Loading workspace records…' }: { text?: string }) {
  return (
    <div className="loading" role="status">
      <span className="loading-mark" aria-hidden="true" />
      {text}
    </div>
  );
}
export function Empty({
  title,
  children,
  action,
}: {
  title: string;
  children: ReactNode;
  action?: ReactNode;
}) {
  return (
    <div className="empty">
      <h3>{title}</h3>
      <p>{children}</p>
      {action}
    </div>
  );
}
export function Status({ value }: { value: string }) {
  return <span className={`status status-${value.toLowerCase()}`}>{label(value)}</span>;
}
export function Search({
  value,
  onChange,
  placeholder = 'Search records',
  label: searchLabel = 'Search',
}: {
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  label?: string;
}) {
  const id = useId();
  return (
    <div className="search-field">
      <label className="sr-only" htmlFor={id}>
        {searchLabel}
      </label>
      <Icon name="search" />
      <input
        id={id}
        type="search"
        maxLength={200}
        value={value}
        placeholder={placeholder}
        onChange={(event) => onChange(event.target.value)}
      />
    </div>
  );
}
export function Pagination({
  result,
  page,
  onPage,
}: {
  result: PageResult<unknown>;
  page: number;
  onPage: (page: number) => void;
}) {
  const pages = Math.max(1, Math.ceil(result.total / result.pageSize));
  return (
    <div className="pagination">
      <span>
        {result.total
          ? `${(page - 1) * result.pageSize + 1}–${Math.min(page * result.pageSize, result.total)} of ${result.total} records`
          : '0 records'}
      </span>
      <div>
        <button
          type="button"
          className="button secondary"
          disabled={page <= 1}
          onClick={() => onPage(page - 1)}
        >
          Previous
        </button>
        <span>
          Page {page} of {pages}
        </span>
        <button
          type="button"
          className="button secondary"
          disabled={page >= pages}
          onClick={() => onPage(page + 1)}
        >
          Next
        </button>
      </div>
    </div>
  );
}
export function Table({ label: tableLabel, children }: { label: string; children: ReactNode }) {
  return (
    <div className="table-scroll" role="region" aria-label={tableLabel} tabIndex={0}>
      <table>{children}</table>
    </div>
  );
}
export function Modal({
  title,
  children,
  onClose,
  wide = false,
}: {
  title: string;
  children: ReactNode;
  onClose: () => void;
  wide?: boolean;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  // Child autofocus runs before the opening effect, so retain the opener during render.
  const trigger = useRef<HTMLElement | null>(
    document.activeElement instanceof HTMLElement ? document.activeElement : null,
  );
  const id = useId();
  useEffect(() => {
    const dialog = ref.current!;
    dialog.showModal();
    return () => {
      dialog.close();
      if (trigger.current?.isConnected) trigger.current.focus();
    };
  }, []);
  return (
    <dialog
      ref={ref}
      className={wide ? 'dialog wide' : 'dialog'}
      aria-labelledby={id}
      onCancel={(event) => {
        event.preventDefault();
        onClose();
      }}
    >
      <header className="dialog-heading">
        <h2 id={id}>{title}</h2>
        <button type="button" className="icon-button" aria-label="Close dialog" onClick={onClose}>
          <Icon name="close" />
        </button>
      </header>
      {children}
    </dialog>
  );
}
export function Field({
  label: fieldLabel,
  hint,
  children,
  className = '',
}: {
  label: string;
  hint?: string;
  children: ReactNode;
  className?: string;
}) {
  const id = useId();
  const fields = useContext(ValidationContext);
  const childProps = isValidElement(children)
    ? children.props as { name?: string; 'aria-label'?: string; 'aria-labelledby'?: string }
    : {};
  const name = isValidElement(children)
    ? String(childProps.name ?? '')
    : '';
  const messages =
    Object.entries(fields).find(([key]) => key.toLowerCase() === name.toLowerCase())?.[1] ?? [];
  const descriptions = [hint ? id : '', messages.length ? `${id}-error` : '']
    .filter(Boolean)
    .join(' ');
  const control = isValidElement(children)
    ? cloneElement(children as ReactElement<Record<string, unknown>>, {
        ...(childProps['aria-label'] === undefined && childProps['aria-labelledby'] === undefined
          ? { 'aria-labelledby': `${id}-label` }
          : {}),
        'aria-describedby': descriptions || undefined,
        'aria-invalid': messages.length ? true : undefined,
      })
    : children;
  return (
    <div className={`field ${className}`}>
      <label>
        <span id={`${id}-label`}>{fieldLabel}</span>
        {control}
      </label>
      {hint && <small id={id}>{hint}</small>}
      {messages.length > 0 && (
        <small className="field-error" id={`${id}-error`}>
          {messages.join(' ')}
        </small>
      )}
    </div>
  );
}
export function Form({
  children,
  submit,
  onCancel,
  submitLabel = 'Save changes',
  className = '',
}: {
  children: ReactNode;
  submit: (data: FormData) => Promise<unknown>;
  onCancel?: () => void;
  submitLabel?: string;
  className?: string;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  async function handle(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (busy) return;
    const data = new FormData(event.currentTarget);
    setBusy(true);
    setError(null);
    try {
      await submit(data);
    } catch (failure) {
      setError(failure);
    } finally {
      setBusy(false);
    }
  }
  return (
    <form onSubmit={handle} className={`form ${className}`} aria-busy={busy}>
      <ErrorNotice error={error} />
      <ValidationContext.Provider value={error instanceof ApiError ? error.fields : {}}>
        <fieldset disabled={busy}>{children}</fieldset>
      </ValidationContext.Provider>
      <div className="form-actions">
        {onCancel && (
          <button type="button" className="button secondary" disabled={busy} onClick={onCancel}>
            Cancel
          </button>
        )}
        <button className="button primary" disabled={busy} type="submit">
          {busy ? 'Saving…' : submitLabel}
        </button>
      </div>
    </form>
  );
}
export function text(data: FormData, key: string) {
  return String(data.get(key) ?? '').trim();
}
export function numeric(data: FormData, key: string) {
  return Number(data.get(key));
}
export function ResultNotice({ children }: { children: ReactNode }) {
  return (
    <div className="notice success" role="status">
      {children}
    </div>
  );
}
