import { defineConfig } from 'vite'
import { fileURLToPath, URL } from "url";
// import { visualizer } from "rollup-plugin-visualizer";
import vue from '@vitejs/plugin-vue'

export default defineConfig({
    plugins: [vue()],
    base: '/dist/',
    resolve: {
        alias: {
            "@": fileURLToPath(new URL("./src", import.meta.url)),
            "#": "@silveredgold/beta-shared",
            // the package's exports map names an .mjs file it doesn't ship
            "@silveredgold/beta-shared-components": fileURLToPath(new URL("./node_modules/@silveredgold/beta-shared-components/lib/beta-shared-components.es.js", import.meta.url))
        },
    },
    build: {
        outDir: '../wwwroot/dist',
        emptyOutDir: true,
        // where _Layout.cshtml reads it (Vite 5+ would put it in .vite/)
        manifest: 'manifest.json',
        rollupOptions: {
            input: {
                main: './main.ts',
                config: './config.ts'
            }
        }
    },
    server: {
        hmr: {
            protocol: 'ws'
        }
    }
})