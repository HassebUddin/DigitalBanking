import type { CapacitorConfig } from '@capacitor/cli';

const config: CapacitorConfig = {
  appId: 'com.haseebbank.app',
  appName: 'Digital Bank',
  webDir: 'dist/digital-banking-web/browser',
  server: {
    androidScheme: 'http',
    cleartext: true
  }
};

export default config;
