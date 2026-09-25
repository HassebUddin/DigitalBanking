import { Routes } from '@angular/router';
import { adminGuard, authGuard, customerGuard, employeeGuard, guestGuard } from './core/guards';
import { StaffLoginPage } from './pages/staff-login-page';
import { AuthShell } from './layout/auth-shell';
import { CustomerShell } from './layout/customer-shell';
import { AdminShell } from './layout/admin-shell';
import { EmployeeShell } from './layout/employee-shell';
import { ChatPage } from './pages/chat-page';
import { LoginPage } from './pages/login-page';
import { RegisterPage } from './pages/register-page';
import { ForgotPasswordPage } from './pages/forgot-password-page';
import { ResetPasswordPage } from './pages/reset-password-page';
import { DashboardPage } from './pages/dashboard-page';
import { AccountsPage } from './pages/accounts-page';
import { TransferPage } from './pages/transfer-page';
import { TransactionsPage } from './pages/transactions-page';
import { NotificationsPage } from './pages/notifications-page';
import { ProfilePage } from './pages/profile-page';
import { ProfileEditPage } from './pages/profile-edit-page';
import {
  AdminAccountsPage,
  AdminAuditPage,
  AdminCustomersPage,
  AdminDashboardPage,
  AdminTransactionsPage
} from './pages/admin-pages';

export const routes: Routes = [
  {
    path: '',
    component: AuthShell,
    canActivate: [guestGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'login' },
      { path: 'login', component: LoginPage },
      { path: 'staff-login', component: StaffLoginPage },
      { path: 'register', component: RegisterPage },
      { path: 'forgot-password', component: ForgotPasswordPage },
      { path: 'reset-password', component: ResetPasswordPage }
    ]
  },
  {
    path: '',
    component: CustomerShell,
    canActivate: [authGuard, customerGuard],
    children: [
      { path: 'dashboard', component: DashboardPage },
      { path: 'accounts', component: AccountsPage },
      { path: 'transfer', component: TransferPage },
      { path: 'transactions', component: TransactionsPage },
      { path: 'notifications', component: NotificationsPage },
      { path: 'chat', component: ChatPage },
      { path: 'profile/edit', component: ProfileEditPage },
      { path: 'profile', component: ProfilePage }
    ]
  },
  {
    path: 'admin',
    component: AdminShell,
    canActivate: [authGuard, adminGuard],
    children: [
      { path: '', component: AdminDashboardPage },
      { path: 'customers', component: AdminCustomersPage },
      { path: 'accounts', component: AdminAccountsPage },
      { path: 'transactions', component: AdminTransactionsPage },
      { path: 'audit', component: AdminAuditPage },
      { path: 'chat', component: ChatPage }
    ]
  },
  {
    path: 'desk',
    component: EmployeeShell,
    canActivate: [authGuard, employeeGuard],
    children: [{ path: '', component: ChatPage }]
  },
  { path: '**', redirectTo: 'login' }
];
