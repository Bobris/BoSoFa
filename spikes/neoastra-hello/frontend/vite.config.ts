import { fileURLToPath } from "node:url";
import solid from "@solidjs/vite-plugin";
import { defineConfig } from "vite";

// Same setup as web/apps/web (pure SPA, Solid 2), plus what NeoAstra needs: a relative base for the app:// scheme and
// the generated RPC binding under the #neoastra alias.
export default defineConfig({
  plugins: [solid({ diagnostics: true })],
  resolve: {
    alias: { "#neoastra": fileURLToPath(new URL("../obj/neoastra/neoastra.ts", import.meta.url)) },
    dedupe: ["@neoastra/client"],
  },
  base: "./",
  server: { fs: { allow: [".."] }, host: "127.0.0.1", port: 5173, strictPort: true },
  build: { target: "es2022", sourcemap: false },
});
