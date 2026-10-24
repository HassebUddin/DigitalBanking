import { HttpErrorResponse } from '@angular/common/http';

export function readErrorMessage(error: unknown, fallback = 'Something went wrong.') {
  if (error instanceof HttpErrorResponse) {
    if (error.status === 0) {
      return 'Cannot reach the bank. Check your connection.';
    }
    // A 401 with the server's own message (e.g. "Email or password is incorrect.") shows that message;
    // a bare 401 means the sign-in token ran out.
    if (error.status === 401 && !serverMessage(error.error)) {
      return 'Session expired. Sign in again and retry.';
    }

    const body = error.error;
    if (typeof body === 'string' && body.trim()) {
      try {
        const parsed = JSON.parse(body) as { message?: string; detail?: string; title?: string };
        return parsed.message || parsed.detail || parsed.title || fallback;
      } catch {
        return fallback;
      }
    }

    if (body && typeof body === 'object') {
      return body.message || body.detail || body.title || fallback;
    }
  }

  return fallback;
}

// The message the server put in an error body, if any (the body may arrive as text or as parsed JSON).
function serverMessage(body: unknown): string {
  if (typeof body === 'string' && body.trim()) {
    try {
      const parsed = JSON.parse(body) as { message?: string };
      return parsed.message ?? '';
    } catch {
      return '';
    }
  }

  return body && typeof body === 'object' ? ((body as { message?: string }).message ?? '') : '';
}

export function formatMoney(amount: number, currency = 'PKR') {
  return `${currency} ${amount.toLocaleString('en-PK', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
}

export function formatDate(value: string) {
  return new Date(value).toLocaleString();
}
