import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: "dist",
    sourcemap: true,
    manifest: ".vite/manifest.json",
    rolldownOptions: {
      output: {
        // The verified asset contract requires eight alphanumeric hash characters.
        hashCharacters: "hex",
        entryFileNames: "assets/entry-[name]-[hash].js",
        chunkFileNames: "assets/chunk-[name]-[hash].js",
        assetFileNames: "assets/[name]-[hash][extname]",
      },
    },
  },
  server: {
    host: "127.0.0.1",
    port: 5173,
    strictPort: true,
    proxy: {
      // Keep the browser's Host/Origin and credentials together for the local API.
      "/api": { target: "http://127.0.0.1:5080", changeOrigin: false, xfwd: false },
    },
  },
});
