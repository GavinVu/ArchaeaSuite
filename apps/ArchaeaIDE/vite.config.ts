import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  clearScreen: false,
  server: {
    port: 1430,
    strictPort: true,
    // The stylesheet is shared with the launcher (../../launcher/src/styles.css).
    fs: { allow: ["../.."] },
    watch: { ignored: ["**/src-tauri/**"] },
  },
  build: {
    target: "chrome105",
    sourcemap: false,
    chunkSizeWarningLimit: 6000,
  },
});
