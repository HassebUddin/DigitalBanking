import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';

export const authGuard: CanActivateFn = () => {
  const authService = inject(AuthService);
  const router = inject(Router);
  return authService.isSignedIn ? true : router.createUrlTree(['/login']);
};

export const adminGuard: CanActivateFn = () => {
  const authService = inject(AuthService);
  const router = inject(Router);
  return authService.isAdmin ? true : router.createUrlTree([authService.homeRoute()]);
};

export const employeeGuard: CanActivateFn = () => {
  const authService = inject(AuthService);
  const router = inject(Router);
  return authService.isEmployee ? true : router.createUrlTree([authService.homeRoute()]);
};

export const customerGuard: CanActivateFn = () => {
  const authService = inject(AuthService);
  const router = inject(Router);
  return authService.isCustomer ? true : router.createUrlTree([authService.homeRoute()]);
};

export const guestGuard: CanActivateFn = () => {
  const authService = inject(AuthService);
  const router = inject(Router);
  if (!authService.isSignedIn) {
    return true;
  }

  return router.createUrlTree([authService.homeRoute()]);
};
