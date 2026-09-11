import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { fileURLToPath, URL } from 'node:url';
export default defineConfig({
  plugins:[react()], resolve:{alias:{'@':fileURLToPath(new URL('./src',import.meta.url))}},
  server:{fs:{allow:[fileURLToPath(new URL('./',import.meta.url)),fileURLToPath(new URL('../docs/players',import.meta.url))]},proxy:{'/api':{target:'http://127.0.0.1:8787',ws:true}}},
  build:{outDir:'../controller/rimgovernor/static',emptyOutDir:true},
});
