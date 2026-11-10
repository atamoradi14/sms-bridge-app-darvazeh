/// <reference types="vite/client" />

declare const __FIELDSENSE_API_BASE__: string | undefined;

interface ImportMetaEnv {
  readonly VITE_API_BASE?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
