import { defineConfig, transformWithEsbuild } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [
    {
      name: "treat-js-files-as-jsx",
      enforce: "pre",
      async transform(code, id) {
        if (!id.match(/src\/.*\.js$/) || id.includes("node_modules")) return null;
        return transformWithEsbuild(code, id, { loader: "jsx", jsx: "automatic" });
      },
    },
    react(),
  ],

  optimizeDeps: {
    esbuildOptions: {
      loader: {
        ".js": "jsx",
      },
    },
  },

  server: {
    port: 3000,
    proxy: {
      "/API": "http://localhost:3001",
      "/odata": "http://localhost:3001",
      "/Image": "http://localhost:3001",
      "/ImageThumb": "http://localhost:3001",
      "/BoardgameImage": "http://localhost:3001",
      "/BoardgameImageThumb": "http://localhost:3001",
    },
  },

  build: {
    outDir: "build",
  },

  test: {
    globals: true,
    environment: "happy-dom",
    setupFiles: ["./src/setupTests.js"],
  },
});
