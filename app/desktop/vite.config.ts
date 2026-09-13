import {defineConfig} from "vite"
import vue from "@vitejs/plugin-vue"
import UnoCSS from "unocss/vite"
import Components from "unplugin-vue-components/vite"
import {NaiveUiResolver} from "unplugin-vue-components/resolvers"
import {sentryVitePlugin} from "@sentry/vite-plugin"

// 宿主开发模式下 AssetServer 固定监听的端口 (见 Nori.Core/Assets/AssetServer.cs)
const HOST_ASSET_PORT = 14201
// https://vite.dev/config/
export default defineConfig(async () => {
	const SENTRY_RELEASE = process.env.NORI_SENTRY_RELEASE ?? ""
	const SENTRY_AUTH_TOKEN = process.env.SENTRY_AUTH_TOKEN ?? ""
	const SENTRY_ORG = process.env.SENTRY_ORG ?? ""
	const SENTRY_PROJECT = process.env.SENTRY_PROJECT_WEB ?? ""
	const SENTRY_URL = process.env.SENTRY_URL ?? ""
	const PRODUCT_VERSION_INPUT = process.env.NORI_PRODUCT_VERSION?.trim() || "Dev"
	const PRODUCT_INFORMATIONAL_VERSION = process.env.NORI_PRODUCT_INFORMATIONAL_VERSION?.trim() || ""
	const COMMIT_SHA = process.env.NORI_COMMIT_SHA?.trim() || "unknown"
	const SHORT_COMMIT_SHA = COMMIT_SHA.length >= 7 ? COMMIT_SHA.slice(0, 7) : COMMIT_SHA
	const PRODUCT_VERSION_CORE = PRODUCT_VERSION_INPUT.replace(/^v/i, "")
	const PRODUCT_VERSION = PRODUCT_INFORMATIONAL_VERSION
		|| (PRODUCT_VERSION_INPUT === "Dev"
			? "Dev"
			: `v${PRODUCT_VERSION_CORE}${PRODUCT_VERSION_CORE.includes("+") ? "" : `+${SHORT_COMMIT_SHA}`}`)
	const SHOULD_UPLOAD_SOURCEMAPS = Boolean(SENTRY_RELEASE && SENTRY_AUTH_TOKEN && SENTRY_ORG && SENTRY_PROJECT)
	const PLUGINS = [
		vue(),
		// 原子样式引擎 (配置在 uno.config.ts, 不带 reset)
		UnoCSS(),
		Components({
			resolvers: [NaiveUiResolver()],
			dts: true,
		}),
	]
	if (SHOULD_UPLOAD_SOURCEMAPS) {
		PLUGINS.push(...sentryVitePlugin({
			authToken: SENTRY_AUTH_TOKEN,
			org: SENTRY_ORG,
			project: SENTRY_PROJECT,
			url: SENTRY_URL || undefined,
			release: {
				name: SENTRY_RELEASE,
				create: true,
				finalize: true,
			},
			sourcemaps: {
				assets: "dist/**",
				filesToDeleteAfterUpload: "dist/**/*.map",
			},
		}))
	}

	return {
		plugins: PLUGINS,
		// 相对基址: 生产下页面挂在 /<随机前缀>/app/ 里, 用绝对路径引资源会漏掉前缀直接 404.
		// 路由用的是 hash history, 不受相对基址影响.
		base: "./",
		// 桌面端使用现代 WebView(WebView2/系统 webview), 放宽构建目标以支持
		// import.meta.glob 等生成的 top-level await, 避免 es2020 转译失败
		define: {
			"import.meta.env.VITE_APP_VERSION": JSON.stringify(PRODUCT_VERSION),
			"import.meta.env.VITE_SENTRY_RELEASE": JSON.stringify(SENTRY_RELEASE),
			"import.meta.env.VITE_SENTRY_ENVIRONMENT": JSON.stringify(process.env.NORI_SENTRY_ENVIRONMENT ?? "production"),
		},
		build: {
			target: "esnext",
			sourcemap: SHOULD_UPLOAD_SOURCEMAPS ? "hidden" : false,
			rollupOptions: {
				output: {
					manualChunks: (id) => {
						if (!id.includes("node_modules")) return undefined
						if (id.includes("@sentry") || id.includes("rrweb")) return "sentry"
						if (id.includes("naive-ui")) return "naive-ui"
						return undefined
					},
				},
			},
		},
		// 1. 不要用 vite 的清屏盖掉宿主的编译错误
		clearScreen: false,
		server: {
			// 2. 宿主开发模式固定指向这个端口, 端口被占用直接失败而不是换一个
			port: 1420,
			strictPort: true,
			// 3. 资源请求代理到宿主的回环资源服务, 让开发与生产下前端写法一致 (同源相对路径)
			proxy: {
				"/nori-assets": {
					target: `http://127.0.0.1:${HOST_ASSET_PORT}`,
					changeOrigin: false,
				},
				"/media": {
					target: `http://127.0.0.1:${HOST_ASSET_PORT}`,
					changeOrigin: false,
				},
				"/plugins": {
					target: `http://127.0.0.1:${HOST_ASSET_PORT}`,
					changeOrigin: false,
				},
			},
			watch: {
				// 4. 不监听 .NET 构建产物
				ignored: ["**/bin/**", "**/obj/**"],
			},
		},
	}
})
