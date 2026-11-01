import type { CapacitorConfig } from "@capacitor/cli";

const config: CapacitorConfig = {
  appId: "com.fieldsense.smsforward",
  appName: "FieldSense SMS",
  webDir: "www",
  server: {
    androidScheme: "https",
  },
};

export default config;
