import { Capacitor } from '@capacitor/core';

const apiUrlKey = 'digitalBanking.apiUrl';

export function resolveApiUrl() {
  // A shared link like ...login?server=https://xyz.trycloudflare.com sets the server for the friend.
  const shared = new URLSearchParams(window.location.search).get('server');
  if (shared) {
    saveApiUrl(shared);
  }

  const override = localStorage.getItem(apiUrlKey);
  if (override) {
    return override.replace(/\/$/, '');
  }

  if (Capacitor.isNativePlatform() && Capacitor.getPlatform() === 'android') {
    return 'http://192.168.100.27:5000';
  }

  // Served together with the API (e.g. through a tunnel): call the same site.
  if (!['localhost', '127.0.0.1'].includes(window.location.hostname)) {
    return window.location.origin;
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
