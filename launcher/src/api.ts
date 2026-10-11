import { invoke } from "@tauri-apps/api/core";
import type {
  BackendError,
  CatalogResult,
  InstalledApp,
  Settings,
  StorageInfo,
  SystemInfo,
} from "./types";

export const api = {
  systemInfo: () => invoke<SystemInfo>("get_system_info"),
  loadCatalog: (refresh: boolean) => invoke<CatalogResult>("load_catalog", { refresh }),
  installed: () => invoke<InstalledApp[]>("get_installed"),
  install: (id: string) => invoke<InstalledApp>("install_app", { id }),
  cancelInstall: (id: string) => invoke<void>("cancel_install", { id }),
  uninstall: (id: string) => invoke<void>("uninstall_app", { id }),
  launch: (id: string) => invoke<void>("launch_app", { id }),
  openAppFolder: (id: string) => invoke<void>("open_app_folder", { id }),
  openInstallDir: () => invoke<void>("open_install_dir"),
  settings: () => invoke<Settings>("get_settings"),
  setSettings: (settings: Settings) => invoke<Settings>("set_settings", { settings }),
  storage: () => invoke<StorageInfo>("get_storage_info"),
  clearCache: () => invoke<void>("clear_cache"),
};

/** Normalises anything thrown by `invoke` or a plugin into `{ code, message }`. */
export function toError(e: unknown): BackendError {
  if (e && typeof e === "object" && "message" in e) {
    const obj = e as { code?: unknown; message: unknown };
    return {
      code: typeof obj.code === "string" ? obj.code : "error",
      message: String(obj.message),
    };
  }
  return { code: "error", message: typeof e === "string" ? e : "Something went wrong." };
}
