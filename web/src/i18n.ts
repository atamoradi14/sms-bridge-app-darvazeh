export type Lang = "fa" | "en";
export function loadLang(): Lang {
  return localStorage.getItem("darvazeh_lang") === "en" ? "en" : "fa";
}
export function saveLang(lang: Lang) {
  localStorage.setItem("darvazeh_lang", lang);
}
