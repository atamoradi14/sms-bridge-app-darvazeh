import type { Lang } from "./i18n";
// SQLite-backed API timestamps are UTC even when their JSON representation omits Z.
export function utcDate(value: string): Date {
  return new Date(/(?:Z|[+-]\d{2}:\d{2})$/i.test(value) ? value : value + "Z");
}
export function formatDateTime(
  value: string,
  lang: Lang,
  options: Intl.DateTimeFormatOptions = {
    dateStyle: "medium",
    timeStyle: "short",
  },
): string {
  return utcDate(value).toLocaleString(
    lang === "fa" ? "fa-IR" : "en-NZ",
    options,
  );
}
