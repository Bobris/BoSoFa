import solid from "@solidjs/vite-plugin";
import tailwindcss from "@tailwindcss/vite";
import { defineConfig } from "vite";

// Pure SPA: no start mode, no SSR. In development Vite serves the app and proxies the server's routes;
// in production the server serves dist/ itself.
const server = process.env.BOSOFA_SERVER ?? "http://localhost:5080";

export default defineConfig({
  plugins: [solid({ diagnostics: true }), tailwindcss()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: Object.fromEntries(
      ["/api", "/auth", "/openapi", "/health", "/install/"].map((path) => [path, { target: server, ws: path === "/api" }]),
    ),
  },
  build: { target: "es2022" },
});
