import { HttpErrorResponse } from '@angular/common/http';

export function readErrorMessage(error: unknown, fallback = 'Something went wrong.') {
  if (error instanceof HttpErrorResponse) {
    if (error.status === 0) {
      return 'Cannot reach the bank. Check your connection.';
    }
    if (error.status === 401) {
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

export function formatMoney(amount: number, currency = 'PKR') {
  return `${currency} ${amount.toLocaleString('en-PK', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
}

export function formatDate(value: string) {
  return new Date(value).toLocaleString();
}
