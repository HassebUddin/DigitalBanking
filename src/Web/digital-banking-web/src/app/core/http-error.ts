import { HttpErrorResponse } from '@angular/common/http';

export function readErrorMessage(error: unknown, fallback = 'Something went wrong.') {
  if (error instanceof HttpErrorResponse) {
    return error.error?.message || fallback;
  }

  return fallback;
}

export function formatMoney(amount: number, currency = 'PKR') {
  return `${currency} ${amount.toLocaleString('en-PK', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
}

export function formatDate(value: string) {
  return new Date(value).toLocaleString();
}
