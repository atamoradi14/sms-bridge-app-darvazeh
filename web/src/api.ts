declare global {
  interface Window {
    __FIELDSENSE_API_BASE__?: string;
  }
}
const TOKEN_KEY = "fs_token";
const base = () =>
  (import.meta.env.DEV
    ? import.meta.env.VITE_API_BASE || "http://127.0.0.1:49199"
    : window.__FIELDSENSE_API_BASE__ ||
      import.meta.env.VITE_API_BASE ||
      "https://api1.daryajan.com"
  ).replace(/\/$/, "");
export type Subscription = {
  planCode: string;
  planName: string;
  startsAt: string;
  expiresAt: string;
  messageLimit: number;
  usedMessages: number;
  remainingMessages: number;
  active: boolean;
  sendEnabled: boolean;
  receiveEnabled: boolean;
  canSend: boolean;
  canReceive: boolean;
  emailForwardLimit: number;
  usedEmailForwards: number;
  remainingEmailForwards: number;
  canForwardEmail: boolean;
};
export type User = {
  id: string;
  email: string;
  role: string;
  username?: string;
  hasPassword?: boolean;
  emailVerified: boolean;
  suspendedAt?: string;
  emailVerifiedAt?: string;
  trialStartedAt?: string;
  forwardEmailEnabled: boolean;
  forwardEmailAddress?: string;
  subscription: Subscription;
  devices?: Device[];
};
export type Device = {
  id: string;
  name: string;
  phoneNumber?: string;
  platform?: string;
  deviceModel?: string;
  appVersion?: string;
  isOnline: boolean;
  lastSeenAt?: string;
  batteryLevel?: number;
  isCharging?: boolean;
  networkType?: string;
  networkValidated?: boolean;
  smsPermissionsGranted?: boolean;
  batteryOptimizationExempt?: boolean;
  healthReportedAt?: string;
  revokedAt?: string;
  adminPausedAt?: string;
};
export type Message = {
  id: string;
  direction: string;
  peerNumber: string;
  body: string;
  status: string;
  emailForwarded: boolean;
  emailForwardStatus: string;
  batteryLevel?: number;
  isCharging?: boolean;
  networkType?: string;
  createdAt: string;
};
export type AdminUser = User & {
  createdAt: string;
  lastLoginAt?: string;
  deviceCount: number;
  received: number;
  sent: number;
  queued: number;
  failed: number;
};
export type Plan = {
  emailForwardLimit: number;
  id: string;
  code: string;
  name: string;
  durationDays: number;
  messageLimit: number;
  sendEnabled: boolean;
  receiveEnabled: boolean;
  priceUsdCents: number | null;
  priceToman: number | null;
  isPurchasable: boolean;
};

export type PaymentOrder = {
  id: string;
  planName: string;
  durationDays: number;
  messageLimit: number;
  currency: string;
  amountMinor: number;
  refundedMinor: number;
  status: string;
  createdAt: string;
  paidAt?: string;
  fulfilledAt?: string;
  email?: string;
};
export type BillingCatalog = {
  plans: Plan[];
  stripeAvailable: boolean;
  stripeTestMode: boolean;
  tomanAvailable: boolean;
  legalName: string;
  tradingName: string;
  queuedPeriods: number;
};
export type Activity = {
  id: string;
  userId: string;
  email: string;
  action: string;
  detail: string;
  createdAt: string;
};
export type Stats = {
  users: number;
  devices: number;
  received: number;
  sent: number;
  queued: number;
  failed: number;
  activeSubscriptions: number;
};
export class RequestError extends Error {
  status = 0;
  code?: string;
  needAdminPassword?: boolean;
}
async function req<T>(path: string, init?: RequestInit): Promise<T> {
  const token = localStorage.getItem(TOKEN_KEY);
  let response: Response;
  try {
    response = await fetch(base() + path, {
      ...init,
      headers: {
        "Content-Type": "application/json",
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
        ...init?.headers,
      },
    });
  } catch {
    const error = new RequestError("Connection unavailable");
    error.code = "connection";
    throw error;
  }
  const data = await response.json().catch(() => ({}));
  if (!response.ok) {
    const error = new RequestError(
      data.error || "Request could not be completed",
    );
    error.status = response.status;
    error.code = data.code;
    error.needAdminPassword = data.needAdminPassword;
    throw error;
  }
  return data as T;
}
const send = <T>(path: string, method: string, body: unknown) =>
  req<T>(path, { method, body: JSON.stringify(body) });
export type EmailStatus={id:string;kind:string;status:string;errorCode?:string;attempts:number;createdAt:string;acceptedAt?:string;completedAt?:string};
export type LinkRequest = {id:string;deviceName:string;comparisonCode:string;createdAt:string;expiresAt:string};
export type PhoneAlert = {id:string;deviceId:string;deviceName:string;kind:string;createdAt:string;resolvedAt?:string;readAt?:string};
export const api = {
  messagePage: (search:string,direction:string,page:number,pageSize:number,userId?:string)=>req<{items:Message[];total:number;page:number;pageSize:number;totalPages:number}>((userId?`/api/admin/users/${userId}/messages/page`:"/api/user/messages/page")+"?"+new URLSearchParams({search,direction,page:String(page),pageSize:String(pageSize)})),
  deleteMessages: (ids:string[],userId?:string)=>send(userId?`/api/admin/users/${userId}/messages/delete`:"/api/user/messages/delete","POST",{ids}),
  adminSecurity: ()=>req<{adminRequireOtp:boolean}>("/api/admin/security"),
  setAdminSecurity: (adminRequireOtp:boolean,password:string)=>send("/api/admin/security","PUT",{adminRequireOtp,password}),
  createUser: (email:string,password:string,confirmPassword:string)=>send("/api/admin/users","POST",{email,password,confirmPassword}),
  modifyUser: (id:string,email:string,username:string,password?:string,confirmPassword?:string)=>send(`/api/admin/users/${id}`,"PUT",{email,username,password,confirmPassword}),
  suspendUser: (id:string,suspended:boolean)=>send(`/api/admin/users/${id}/suspension`,"PUT",{suspended}),
  deleteUser: (id:string)=>req(`/api/admin/users/${id}`,{method:"DELETE"}),
  adminDevices: (id:string)=>req<Device[]>(`/api/admin/users/${id}/devices`),
  setDeviceConnection: (id:string,deviceId:string,connected:boolean)=>send(`/api/admin/users/${id}/devices/${deviceId}/connection`,"PUT",{connected}),
  authOptions: ()=>req<{googleEnabled:boolean}>("/api/auth/google/options"),
  googleStart: ()=>base()+"/api/auth/google/start",
  googleExchange: (flowId:string,code:string)=>req<{token:string}>("/api/auth/google/exchange",{method:"POST",credentials:"include",body:JSON.stringify({flowId,code})}),
  passwordSignup: (email:string,password:string,code:string,confirmPassword:string)=>send<{token:string;user:User}>("/api/auth/password/signup","POST",{email,password,code,confirmPassword}),
  passwordSignin: (identity:string,password:string,adminPassword?:string,code?:string)=>send<{token:string;user:User}>("/api/auth/password/signin","POST",{identity,password,adminPassword,code}),
  passwordReset: (email:string,password:string,code:string,username:string|undefined,adminPassword:string|undefined,confirmPassword:string)=>send<{token:string;user:User}>("/api/auth/password/reset","POST",{email,password,code,username,adminPassword,confirmPassword}),
  requestVerification: ()=>send("/api/user/verify-email/request","POST",{}),
  verifyAccountEmail: (code:string)=>send("/api/user/verify-email","POST",{code}),
  profilePassword: (password:string,confirmPassword:string,currentPassword?:string)=>send<{token:string}>("/api/user/password","POST",{password,confirmPassword,currentPassword}),
  emailStatus: ()=>req<EmailStatus[]>("/api/admin/email-status"),
  pendingLinks: () => req<LinkRequest[]>("/api/device-links/pending"),
  decideLink: (id:string,approve:boolean,comparisonCode:string) => send(`/api/device-links/${id}/decision`,"POST",{approve,comparisonCode}),
  phoneAlerts: () => req<PhoneAlert[]>("/api/user/alerts"),
  readAlert: (id:string) => send(`/api/user/alerts/${id}/read`,"POST",{}),
  getToken: () => localStorage.getItem(TOKEN_KEY),
  setToken: (token: string | null) =>
    token
      ? localStorage.setItem(TOKEN_KEY, token)
      : localStorage.removeItem(TOKEN_KEY),
  requestOtp: (email: string) =>
    send<{ ok: boolean }>("/api/auth/request-otp", "POST", { email }),
  verifyOtp: (email: string, code: string, adminPassword?: string) =>
    send<{ token: string; user: User }>("/api/auth/verify-otp", "POST", {
      email,
      code,
      adminPassword,
    }),
  me: () => req<User>("/api/user/me"),
  messages: () => req<Message[]>("/api/user/messages?take=500"),
  sendSms: (toNumber: string, body: string, clientMessageId: string) =>
    send("/api/user/sms/send", "POST", { toNumber, body, clientMessageId }),
  removeDevice: (id: string) =>
    req(`/api/user/devices/${id}`, { method: "DELETE" }),
  updateSettings: (forwardEmailEnabled: boolean, forwardEmailAddress: string) =>
    send("/api/user/settings", "PUT", {
      forwardEmailEnabled,
      forwardEmailAddress,
    }),
  adminUsers: () => req<AdminUser[]>("/api/admin/users"),
  adminStats: () => req<Stats>("/api/admin/stats"),
  plans: () => req<Plan[]>("/api/admin/plans"),
  activities: () => req<Activity[]>("/api/admin/activities"),
  setFeatures: (
    id: string,
    sendEnabled: boolean | null,
    receiveEnabled: boolean | null,
  ) =>
    send(`/api/admin/users/${id}/features`, "PUT", {
      sendEnabled,
      receiveEnabled,
    }),
  assignPlan: (id: string, planId: string) =>
    send(`/api/admin/users/${id}/subscription`, "PUT", { planId }),
  createPlan: (plan: Omit<Plan, "id">) =>
    send<Plan>("/api/admin/plans", "POST", plan),
  updatePlan: (id: string, plan: Omit<Plan, "id">) =>
    send<Plan>(`/api/admin/plans/${id}`, "PUT", plan),
  billingCatalog: () => req<BillingCatalog>("/api/billing/catalog"),
  paymentOrders: () => req<PaymentOrder[]>("/api/billing/orders"),
  adminPayments: () => req<PaymentOrder[]>("/api/admin/payments"),
  checkout: (planId: string, currency: string, requestKey: string) =>
    send<{ id: string; status: string; checkoutUrl?: string }>(
      "/api/billing/checkout",
      "POST",
      { planId, currency, requestKey },
    ),
  reconcilePayment: (id: string) =>
    send<{ id: string; status: string }>(
      `/api/billing/orders/${id}/reconcile`,
      "POST",
      {},
    ),
  // Default receiving wallet for toman payments (owner only), connected by phone number.
  wallet: () => req<WalletView>("/api/admin/wallet"),
  walletReusable: () =>
    req<{ phoneNumber: string; verifiedAt: string }[]>("/api/admin/wallet/reusable"),
  walletConnect: (phoneNumber: string) =>
    send<WalletView>("/api/admin/wallet/connection", "POST", { phoneNumber }),
  walletReuse: (phoneNumber: string) =>
    send<WalletView>("/api/admin/wallet/reuse", "POST", { phoneNumber }),
  walletRequestProof: (phoneNumber: string) =>
    send<{ sent: boolean }>("/api/admin/wallet/phone-proof/request", "POST", { phoneNumber }),
  walletConfirmProof: (phoneNumber: string, code: string) =>
    send<WalletView>("/api/admin/wallet/phone-proof/confirm", "POST", { phoneNumber, code }),
  walletConfirm: (challenge: string, verifyCode: string) =>
    send<WalletView>(`/api/admin/wallet/consent/${encodeURIComponent(challenge)}`, "PUT", { verifyCode }),
  walletResend: (challenge: string) =>
    send<WalletView>(`/api/admin/wallet/consent/${encodeURIComponent(challenge)}/resend`, "POST", {}),
  walletMerchant: (enabled: boolean) =>
    send<WalletView>("/api/admin/wallet/merchant", "PUT", { enabled }),
  walletDisconnect: () => req<unknown>("/api/admin/wallet", { method: "DELETE" }),
  walletBalances: () =>
    req<{ accountType: string; balance: string; availableBalance: string }[]>("/api/admin/wallet/balances"),
  walletHistory: (page: number) =>
    req<WalletHistory>(`/api/admin/wallet/history?currentPage=${page}`),
  walletTransactions: (page: number) =>
    req<WalletTransactions>(`/api/admin/wallet/transactions?currentPage=${page}`),
};
export type WalletView = {
  connection: {
    status: "Connected" | "PendingConsent" | "Disconnected";
    phoneNumber?: string | null;
    connectedAt?: string | null;
    challengeId?: string | null;
    challengeExpiresAt?: string | null;
    pendingPhoneNumber?: string | null;
  };
  merchantEnabled: boolean;
  purchaseReady: boolean;
  readinessReason: string;
  providerStatus?: { access: string; identityVerified?: boolean | null; level?: string | null } | null;
  relayConfigured: boolean;
};
export type WalletHistory = {
  currentPage: number;
  total: number;
  items: { amount: string; description?: string | null; fromOrTo?: string | null; transactionNumber?: string | null; utcTimestamp: number; balance: string }[];
};
export type WalletTransactions = {
  currentPage: number;
  total: number;
  items: { id: string; email: string; planName: string; receivingPhone?: string | null; amountMinor: number; currency: string; status: string; providerPaymentId?: string | null; createdAt: string; paidAt?: string | null }[];
};
