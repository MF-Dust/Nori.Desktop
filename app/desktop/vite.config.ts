import {defineConfig} from "vite"

// 宿主开发模式下 AssetServer 固定监听的端口 (见 Nori.Core/Assets/AssetServer.cs)
const HOST_ASSET_PORT = 14201

export default defineConfig({
	// 生产页面位于 /<随机前缀>/app/，相对资源路径必须保留前缀。
	base: "./",
	build: {
		target: "esnext",
	},
	clearScreen: false,
	server: {
		// 宿主开发模式固定指向这个端口，端口被占用直接失败而不是换一个。
		port: 1420,
		strictPort: true,
		// 音频宿主和受控插件页面使用同源相对资源路径。
		proxy: {
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
			// 不监听 .NET 构建产物。
			ignored: ["**/bin/**", "**/obj/**"],
		},
	},
})
