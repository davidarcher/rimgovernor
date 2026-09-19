import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { fileURLToPath, URL } from 'node:url';
// RIMGOVERNOR_API points the dev proxy at another serve (an acceptance run's --listen 127.0.0.1:0 port).
export default defineConfig({
  plugins:[react()], resolve:{alias:{'@':fileURLToPath(new URL('./src',import.meta.url))}},
  server:{fs:{allow:[fileURLToPath(new URL('./',import.meta.url)),fileURLToPath(new URL('../docs/players',import.meta.url))]},proxy:{'/api':{target:process.env.RIMGOVERNOR_API??'http://127.0.0.1:8787',ws:true}}},
  build:{outDir:'dist',emptyOutDir:true,rollupOptions:{input:{main:fileURLToPath(new URL('./index.html',import.meta.url)),timeline:fileURLToPath(new URL('./timeline.html',import.meta.url))}}},
});
