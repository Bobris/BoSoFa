import { fileURLToPath } from "node:url";
import solid from "@solidjs/vite-plugin";
import tailwindcss from "@tailwindcss/vite";
import { defineConfig } from "vite";

// The web app's source with a desktop platform in front of it. Keeps the root base: the router uses path history.
export default defineConfig({
  plugins: [solid(), tailwindcss()],
  resolve: {
    alias: { "#neoastra": fileURLToPath(new URL("./neoastra/neoastra.ts", import.meta.url)) },
    dedupe: ["@neoastra/client"],
  },
  server: { fs: { allow: [".."] } },
  build: { target: "es2022" },
});
