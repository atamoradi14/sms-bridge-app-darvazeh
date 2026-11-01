"use client";

import { useCallback, useSyncExternalStore } from "react";
import {
  defaultSmsForwardSettings,
  loadSmsForwardSettings,
  saveSmsForwardSettings,
  SMS_FORWARD_STORAGE_KEY,
  type SmsForwardSettings,
} from "@/lib/sms-forward";

function subscribe(onStoreChange: () => void) {
  const onStorage = (event: StorageEvent) => {
    if (event.key === SMS_FORWARD_STORAGE_KEY) onStoreChange();
  };
  const onCustom = () => onStoreChange();
  window.addEventListener("storage", onStorage);
  window.addEventListener("fieldsense:sms-settings", onCustom);
  return () => {
    window.removeEventListener("storage", onStorage);
    window.removeEventListener("fieldsense:sms-settings", onCustom);
  };
}

function getSnapshot(): SmsForwardSettings {
  return loadSmsForwardSettings();
}

function getServerSnapshot(): SmsForwardSettings {
  return defaultSmsForwardSettings();
}

export function useSmsForwardSettings() {
  const settings = useSyncExternalStore(subscribe, getSnapshot, getServerSnapshot);

  const update = useCallback((patch: Partial<SmsForwardSettings>) => {
    saveSmsForwardSettings({ ...loadSmsForwardSettings(), ...patch });
  }, []);

  const replace = useCallback((next: SmsForwardSettings) => {
    saveSmsForwardSettings(next);
  }, []);

  return { settings, update, replace, hydrated: true };
}
