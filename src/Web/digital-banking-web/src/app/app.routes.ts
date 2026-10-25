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
import { DeskHomePage } from './pages/desk-home-page';
import { DeskPeoplePage } from './pages/desk-people-page';
import { DeskCardsPage } from './pages/desk-cards-page';
import { DeskHistoryPage } from './pages/desk-history-page';
import { DeskAlertsPage } from './pages/desk-alerts-page';
import { DeskKycPage } from './pages/desk-kyc-page';

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
      { path: '', loadComponent: () => import('./ops/pages/command-center-page').then((m) => m.CommandCenterPage) },
      { path: 'regions', loadComponent: () => import('./ops/pages/regions-page').then((m) => m.RegionsPage) },
      { path: 'health', loadComponent: () => import('./ops/pages/health-page').then((m) => m.HealthPage) },
      { path: 'logs', loadComponent: () => import('./ops/pages/logs-page').then((m) => m.LogsPage) },
      { path: 'rules', loadComponent: () => import('./ops/pages/rules-page').then((m) => m.RulesPage) },
      { path: 'infrastructure', loadComponent: () => import('./ops/pages/infrastructure-page').then((m) => m.InfrastructurePage) },
      { path: 'data', loadComponent: () => import('./ops/pages/data-explorer-page').then((m) => m.DataExplorerPage) },
      { path: 'distributed', loadComponent: () => import('./ops/pages/distributed-page').then((m) => m.DistributedPage) },
      { path: 'banking', loadComponent: () => import('./ops/pages/banking-pages').then((m) => m.BankOverviewPage) },
      { path: 'customers', loadComponent: () => import('./ops/pages/banking-pages').then((m) => m.BankCustomersPage) },
      { path: 'accounts', loadComponent: () => import('./ops/pages/banking-pages').then((m) => m.BankAccountsPage) },
      { path: 'transactions', loadComponent: () => import('./ops/pages/banking-pages').then((m) => m.BankTransactionsPage) },
      { path: 'audit', loadComponent: () => import('./ops/pages/banking-pages').then((m) => m.BankAuditPage) },
      { path: 'chat', component: ChatPage }
    ]
  },
  {
    path: 'desk',
    component: EmployeeShell,
    canActivate: [authGuard, employeeGuard],
    children: [
      { path: '', component: DeskHomePage },
      { path: 'customers', component: DeskPeoplePage },
      { path: 'accounts', component: DeskCardsPage },
      { path: 'transactions', component: DeskHistoryPage },
      { path: 'alerts', component: DeskAlertsPage },
      { path: 'kyc', component: DeskKycPage },
      { path: 'chat', component: ChatPage }
    ]
  },
  { path: '**', redirectTo: 'login' }
];
