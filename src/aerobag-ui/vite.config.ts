import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

// Geliştirmede API isteklerini .NET API'sine yönlendirir (CORS gerekmez).
export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    proxy: {
      "/v1": "http://localhost:5080",
      "/hubs": { target: "http://localhost:5080", ws: true },
    },
  },
});
