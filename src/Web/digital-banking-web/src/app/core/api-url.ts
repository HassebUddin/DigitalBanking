import { Capacitor } from '@capacitor/core';

const apiUrlKey = 'digitalBanking.apiUrl';

export function resolveApiUrl() {
  const override = localStorage.getItem(apiUrlKey);
  if (override) {
    return override.replace(/\/$/, '');
  }

  if (Capacitor.isNativePlatform() && Capacitor.getPlatform() === 'android') {
    return 'http://10.0.2.2:5000';
  }

  return 'http://localhost:5000';
}

export function saveApiUrl(url: string) {
  const clean = url.trim().replace(/\/$/, '');
  if (clean) {
    localStorage.setItem(apiUrlKey, clean);
  }
}

export function isNativeApp() {
  return Capacitor.isNativePlatform();
}
