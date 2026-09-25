import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { AuthService } from './auth.service';
import {
  AuditLog,
  AccountApplication,
  BankAccount,
  BankTransaction,
  CustomerProfile,
  DashboardSummary,
  Statement,
  UserNotification
} from './models';

@Injectable({ providedIn: 'root' })
export class BankingService {
  constructor(
    private readonly http: HttpClient,
    private readonly authService: AuthService
  ) {}

  private get apiUrl() {
    return this.authService.apiUrl;
  }

  getProfile() {
    return this.http.get<CustomerProfile>(`${this.apiUrl}/api/customers/me`);
  }

  updateProfile(payload: { fullName: string; phoneNumber: string; address: string }) {
    return this.http.put<CustomerProfile>(`${this.apiUrl}/api/customers/me`, payload);
  }

  getAccounts() {
    return this.http.get<BankAccount[]>(`${this.apiUrl}/api/accounts`);
  }

  getAccountApplications() {
    return this.http.get<AccountApplication[]>(`${this.apiUrl}/api/accounts/applications`);
  }

  applyForAccount(payload: {
    accountType: string;
    purpose: string;
    identityDocument: File;
    addressDocument: File;
    signature: File;
  }) {
    const form = new FormData();
    form.append('accountType', payload.accountType);
    form.append('purpose', payload.purpose);
    form.append('termsAccepted', 'true');
    form.append('identityDocument', payload.identityDocument);
    form.append('addressDocument', payload.addressDocument);
    form.append('signature', payload.signature);
    return this.http.post<AccountApplication>(`${this.apiUrl}/api/accounts/applications`, form);
  }

  approveAccountApplication(applicationId: string, note = '') {
    return this.http.post<AccountApplication>(`${this.apiUrl}/api/accounts/applications/${applicationId}/approve`, { note });
  }

  rejectAccountApplication(applicationId: string, note = '') {
    return this.http.post<AccountApplication>(`${this.apiUrl}/api/accounts/applications/${applicationId}/reject`, { note });
  }

  deposit(accountId: string, amount: number, description: string) {
    return this.http.post<BankTransaction>(`${this.apiUrl}/api/transactions/deposit`, { accountId, amount, description });
  }

  withdraw(accountId: string, amount: number, description: string) {
    return this.http.post<BankTransaction>(`${this.apiUrl}/api/transactions/withdraw`, { accountId, amount, description });
  }

  transfer(sourceAccountId: string, destinationAccountNumber: string, amount: number, description: string) {
    return this.http.post<BankTransaction>(`${this.apiUrl}/api/transactions/transfer`, {
      sourceAccountId,
      destinationAccountNumber,
      amount,
      description
    });
  }

  searchTransactions(filters: {
    searchText?: string;
    transactionType?: string;
    sortBy?: string;
    sortDirection?: string;
    minimumAmount?: number;
    maximumAmount?: number;
  }) {
    let params = new HttpParams();
    Object.entries(filters).forEach(([key, value]) => {
      if (value !== undefined && value !== null && value !== '') {
        params = params.set(key, String(value));
      }
    });
    return this.http.get<BankTransaction[]>(`${this.apiUrl}/api/transactions`, { params });
  }

  getStatement(accountId: string, fromDateUtc: string, toDateUtc: string) {
    const params = new HttpParams().set('fromDateUtc', fromDateUtc).set('toDateUtc', toDateUtc);
    return this.http.get<Statement>(`${this.apiUrl}/api/transactions/statement/${accountId}`, { params });
  }

  getNotifications() {
    return this.http.get<UserNotification[]>(`${this.apiUrl}/api/notifications`);
  }

  markNotificationRead(notificationId: string) {
    return this.http.post(`${this.apiUrl}/api/notifications/${notificationId}/read`, {});
  }

  markAllNotificationsRead() {
    return this.http.post(`${this.apiUrl}/api/notifications/read-all`, {});
  }

  getAdminDashboard() {
    return this.http.get<DashboardSummary>(`${this.apiUrl}/api/admin/dashboard`);
  }

  getAdminCustomers() {
    return this.http.get<CustomerProfile[]>(`${this.apiUrl}/api/admin/customers`);
  }

  getAdminAccounts() {
    return this.http.get<BankAccount[]>(`${this.apiUrl}/api/admin/accounts`);
  }

  getAdminTransactions() {
    return this.http.get<BankTransaction[]>(`${this.apiUrl}/api/admin/transactions`);
  }

  getAdminAuditLogs() {
    return this.http.get<AuditLog[]>(`${this.apiUrl}/api/admin/audit-logs`);
  }

  freezeAccount(accountId: string) {
    return this.http.post(`${this.apiUrl}/api/admin/accounts/${accountId}/freeze`, {});
  }

  unfreezeAccount(accountId: string) {
    return this.http.post(`${this.apiUrl}/api/admin/accounts/${accountId}/unfreeze`, {});
  }
}
