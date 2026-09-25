import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, switchMap, throwError } from 'rxjs';
import { AuthService } from './auth.service';

export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const authService = inject(AuthService);
  const accessToken = authService.accessToken;
  const authorized = accessToken
    ? request.clone({ setHeaders: { Authorization: `Bearer ${accessToken}` } })
    : request;

  return next(authorized).pipe(
    catchError((error: HttpErrorResponse) => {
      const isAuthCall = request.url.includes('/api/auth/');
      if (error.status !== 401 || isAuthCall || !authService.accessToken) {
        return throwError(() => error);
      }

      try {
        return authService.refreshSession().pipe(
          switchMap(() => {
            const nextToken = authService.accessToken;
            const retry = nextToken
              ? request.clone({ setHeaders: { Authorization: `Bearer ${nextToken}` } })
              : request;
            return next(retry);
          })
        );
      } catch {
        return throwError(() => error);
      }
    })
  );
};
