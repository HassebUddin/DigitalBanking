export interface AuthResponse {
  userId: string;
  email: string;
  fullName: string;
  role: string;
  accessToken: string;
  refreshToken: string;
}

export interface CustomerProfile {
  id: string;
  userId: string;
  fullName: string;
  email: string;
  nationalId: string;
  phoneNumber: string;
  address: string;
  kycStatus: string;
  createdAtUtc: string;
}

export interface AccountApplication {
  id: string;
  userId: string;
  accountType: string;
  purpose: string;
  status: string;
  reviewNote: string;
  hasIdentityDocument: boolean;
  hasIdentityBackDocument: boolean;
  hasAddressDocument: boolean;
  hasSignature: boolean;
  identityDocumentUrl?: string;
  identityBackDocumentUrl?: string;
  addressDocumentUrl?: string;
  signatureUrl?: string;
  createdAccountId?: string;
  createdAtUtc: string;
}

export interface BankAccount {
  id: string;
  customerId: string;
  userId: string;
  accountNumber: string;
  accountType: string;
  balance: number;
  currency: string;
  status: string;
  createdAtUtc: string;
}

export interface BankTransaction {
  id: string;
  accountId: string;
  counterpartyAccountId?: string;
  transactionType: string;
  amount: number;
  status: string;
  referenceNumber: string;
  description: string;
  createdAtUtc: string;
}

export interface Statement {
  accountId: string;
  fromDateUtc: string;
  toDateUtc: string;
  totalDeposits: number;
  totalWithdrawals: number;
  transactions: BankTransaction[];
}

export interface UserNotification {
  id: string;
  userId: string;
  channel: string;
  title: string;
  body: string;
  isRead: boolean;
  createdAtUtc: string;
}

export interface DashboardSummary {
  customerCount: number;
  accountCount: number;
  transactionCount: number;
  totalBalances: number;
}

export interface AuditLog {
  id: string;
  eventType: string;
  actorUserId?: string;
  payload: string;
  occurredAtUtc: string;
}
