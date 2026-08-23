import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import path from "node:path";

// Dev: Vite on :5173 proxies /api + asset routes to the .NET API on :5180.
// Prod: `npm run build` emits the compiled SPA straight into the API's wwwroot,
// so the single .NET process still serves everything on :5180.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { "@": path.resolve(__dirname, "./src") },
  },
  server: {
    port: 5173,
    proxy: {
      "/api": "http://localhost:5180",
    },
  },
  build: {
    outDir: path.resolve(__dirname, "../src/MemeShorts.Api/wwwroot"),
    emptyOutDir: true,
  },
});
