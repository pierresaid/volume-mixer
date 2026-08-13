import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// Dev loop: `dotnet run` the server (real audio, port 6002), then `npm run dev`
// here; the proxy forwards the hub and icons so the page behaves like production.
export default defineConfig({
  plugins: [vue()],
  build: {
    outDir: '../server/VolumeMixer/wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/volumehub': { target: 'http://localhost:6002', ws: true },
      '/icons': { target: 'http://localhost:6002' },
      '/media': { target: 'http://localhost:6002' },
    },
  },
})
