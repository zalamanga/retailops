export class ApiError extends Error {
  public readonly fields: Record<string, string[]>;

  constructor(
    message: string,
    public status: number,
    fields: Record<string, string[]> | null = {},
    public traceId?: string,
    public title?: string,
  ) {
    super(message);
    this.name = 'ApiError';
    this.fields = fields ?? {};
  }
}

let csrfToken: string | undefined;
let csrfPromise: Promise<string> | undefined;
let identityGeneration = 0;
export function clearCsrf() {
  identityGeneration += 1;
  csrfToken = undefined;
  csrfPromise = undefined;
}
export async function refreshCsrf(): Promise<string> {
  const generation = identityGeneration;
  csrfToken = undefined;
  let response: Response;
  try {
    response = await fetch('/api/auth/csrf', {
      credentials: 'same-origin',
      headers: { Accept: 'application/json' },
    });
  } catch {
    throw new ApiError(
      'Could not prepare a protected request. Check your connection and retry.',
      0,
    );
  }
  if (!response.ok)
    throw new ApiError(
      'Could not prepare a protected request. Check the connection and retry.',
      response.status,
    );
  const result = (await response.json()) as { token: string };
  if (generation !== identityGeneration) throw new ApiError('The active session changed while preparing this request. Retry from the current workspace.', 0);
  csrfToken = result.token;
  return result.token;
}
async function ensureCsrf() {
  if (csrfToken) return csrfToken;
  csrfPromise ??= refreshCsrf().finally(() => {
    csrfPromise = undefined;
  });
  return csrfPromise;
}
export async function api<T>(path: string, options: RequestInit = {}): Promise<T> {
  const generation = identityGeneration;
  const method = options.method?.toUpperCase() ?? 'GET';
  const headers = new Headers(options.headers);
  headers.set('Accept', 'application/json');
  if (options.body) headers.set('Content-Type', 'application/json');
  if (!['GET', 'HEAD', 'OPTIONS'].includes(method)) headers.set('X-CSRF-TOKEN', await ensureCsrf());
  let response: Response;
  try {
    response = await fetch(`/api${path}`, {
      ...options,
      method,
      credentials: 'same-origin',
      headers,
    });
  } catch {
    throw new ApiError('The server could not be reached. Check your connection and retry.', 0);
  }
  if (!response.ok) {
    const problem = (await response.json().catch(() => ({}))) as {
      detail?: string;
      title?: string;
      errors?: Record<string, string[]> | null;
      traceId?: string;
    };
    if (response.status === 401 && path !== '/auth/login' && path !== '/auth/me' && generation === identityGeneration)
      window.dispatchEvent(new Event('session-expired'));
    throw new ApiError(
      problem.detail || problem.title || `The request failed (${response.status}).`,
      response.status,
      problem.errors,
      problem.traceId,
      problem.title,
    );
  }
  return response.status === 204 ? (undefined as T) : (response.json() as Promise<T>);
}
export function write<T>(path: string, body?: unknown, method = 'POST', headers?: HeadersInit) {
  return api<T>(path, {
    method,
    body: body === undefined ? undefined : JSON.stringify(body),
    headers,
  });
}
export function params(values: Record<string, string | number | boolean | undefined>) {
  const search = new URLSearchParams();
  Object.entries(values).forEach(([key, value]) => {
    if (value !== undefined && value !== '') search.set(key, String(value));
  });
  const result = search.toString();
  return result ? `?${result}` : '';
}
export async function all<T>(path: string): Promise<T[]> {
  const result: T[] = [];
  for (let page = 1; ; page += 1) {
    const response = await api<{ items: T[]; total: number }>(
      `${path}${path.includes('?') ? '&' : '?'}page=${page}&pageSize=100`,
    );
    result.push(...response.items);
    if (result.length >= response.total || !response.items.length) return result;
  }
}
export async function exportInventory(query: string) {
  const generation = identityGeneration;
  let response: Response;
  try {
    response = await fetch(`/api/inventory/export${query}`, { credentials: 'same-origin' });
  } catch {
    throw new ApiError(
      'The export server could not be reached. Check your connection and retry.',
      0,
    );
  }
  if (!response.ok) {
    if (response.status === 401 && generation === identityGeneration) window.dispatchEvent(new Event('session-expired'));
    throw new ApiError(
      'The inventory export could not be downloaded. Retry after reloading the inventory.',
      response.status,
    );
  }
  const url = URL.createObjectURL(await response.blob());
  const link = document.createElement('a');
  link.href = url;
  link.download = 'retailops-inventory.csv';
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
