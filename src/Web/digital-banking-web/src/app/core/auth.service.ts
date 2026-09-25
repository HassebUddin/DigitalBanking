import { Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Router } from '@angular/router';
import { tap } from 'rxjs';
import { AuthResponse } from './models';
import { resolveApiUrl } from './api-url';

const accessTokenKey = 'digitalBanking.accessToken';
const refreshTokenKey = 'digitalBanking.refreshToken';
const roleKey = 'digitalBanking.role';
const emailKey = 'digitalBanking.email';
const userIdKey = 'digitalBanking.userId';
const fullNameKey = 'digitalBanking.fullName';

@Injectable({ providedIn: 'root' })
export class AuthService {
  get apiUrl() {
    return resolveApiUrl();
  }
  readonly email = signal(localStorage.getItem(emailKey) ?? '');
  readonly role = signal(localStorage.getItem(roleKey) ?? '');
  readonly fullName = signal(localStorage.getItem(fullNameKey) ?? '');
  readonly userId = signal(localStorage.getItem(userIdKey) ?? '');

  constructor(
    private readonly http: HttpClient,
    private readonly router: Router
  ) {}

  get accessToken(): string | null {
    return localStorage.getItem(accessTokenKey);
  }

  get isSignedIn(): boolean {
    return !!this.accessToken;
  }

  get isAdmin(): boolean {
    return this.role() === 'Admin';
  }

  get isEmployee(): boolean {
    return this.role() === 'InternalEmployee' || this.role() === 'ExternalEmployee';
  }

  get isStaff(): boolean {
    return this.isAdmin || this.isEmployee;
  }

  get canReviewApplications(): boolean {
    return this.isAdmin || this.role() === 'InternalEmployee';
  }

  homeRoute(): string {
    if (this.isAdmin) {
      return '/admin';
    }
    if (this.isEmployee) {
      return '/desk';
    }
    return '/dashboard';
  }

  register(payload: {
    email: string;
    password: string;
    fullName: string;
    nationalId: string;
    phoneNumber: string;
    address: string;
  }) {
    return this.http.post<AuthResponse>(`${this.apiUrl}/api/auth/register`, payload).pipe(tap((response) => this.storeSession(response)));
  }

  login(email: string, password: string) {
    return this.http.post<AuthResponse>(`${this.apiUrl}/api/auth/login`, { email, password }).pipe(tap((response) => this.storeSession(response)));
  }

  refreshSession() {
    const refreshToken = localStorage.getItem(refreshTokenKey);
    if (!refreshToken) {
      throw new Error('No refresh token');
    }
    return this.http.post<AuthResponse>(`${this.apiUrl}/api/auth/refresh`, { refreshToken }).pipe(tap((response) => this.storeSession(response)));
  }

  get isCustomer(): boolean {
    return this.role() === 'Customer';
  }

  clearSession() {
    localStorage.removeItem(accessTokenKey);
    localStorage.removeItem(refreshTokenKey);
    localStorage.removeItem(roleKey);
    localStorage.removeItem(emailKey);
    localStorage.removeItem(userIdKey);
    localStorage.removeItem(fullNameKey);
    this.email.set('');
    this.role.set('');
    this.fullName.set('');
    this.userId.set('');
  }

  logout() {
    const refreshToken = localStorage.getItem(refreshTokenKey);
    if (refreshToken) {
      this.http.post(`${this.apiUrl}/api/auth/logout`, { refreshToken }).subscribe();
    }
    this.clearSession();
    void this.router.navigateByUrl('/login');
  }

  changePassword(currentPassword: string, newPassword: string) {
    return this.http.post(`${this.apiUrl}/api/auth/change-password`, { currentPassword, newPassword });
  }

  forgotPassword(email: string) {
    return this.http.post<{ message: string; resetCode: string }>(`${this.apiUrl}/api/auth/forgot-password`, { email });
  }

  resetPassword(email: string, resetCode: string, newPassword: string) {
    return this.http.post(`${this.apiUrl}/api/auth/reset-password`, { email, resetCode, newPassword });
  }

  private storeSession(response: AuthResponse) {
    localStorage.setItem(accessTokenKey, response.accessToken);
    localStorage.setItem(refreshTokenKey, response.refreshToken);
    localStorage.setItem(roleKey, response.role);
    localStorage.setItem(emailKey, response.email);
    localStorage.setItem(userIdKey, response.userId);
    localStorage.setItem(fullNameKey, response.fullName || response.email);
    this.role.set(response.role);
    this.email.set(response.email);
    this.fullName.set(response.fullName || response.email);
    this.userId.set(response.userId);
  }
}
