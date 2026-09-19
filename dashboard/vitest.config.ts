import {defineConfig} from 'vitest/config';
import react from '@vitejs/plugin-react';
import {fileURLToPath,URL} from 'node:url';
export default defineConfig({plugins:[react()],resolve:{alias:{'@':fileURLToPath(new URL('./src',import.meta.url))}},server:{fs:{allow:[fileURLToPath(new URL('./',import.meta.url)),fileURLToPath(new URL('../docs/players',import.meta.url))]}},test:{environment:'jsdom',include:['src/**/*.test.ts','src/features/manager/*.test.tsx','src/features/governor/*.test.tsx']}});
