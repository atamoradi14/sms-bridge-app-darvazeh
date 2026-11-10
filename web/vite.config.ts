import react from "@vitejs/plugin-react";
import { defineConfig, loadEnv } from "vite";

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), "");
  // Host secret / CI env (not VITE_*) — used by Cursor publish + local overrides.
  const apiBase =
    process.env.FIELDSENSE_API_BASE ||
    env.FIELDSENSE_API_BASE ||
    env.VITE_API_BASE ||
    "";

  return {
    plugins: [react()],
    define: {
      __FIELDSENSE_API_BASE__: JSON.stringify(apiBase),
    },
    server: {
      host: "0.0.0.0",
      port: 43123,
      strictPort: true,
    },
    preview: {
      host: "0.0.0.0",
      port: 43123,
      strictPort: true,
    },
  };
});
