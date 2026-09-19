/// <reference types="vite/client" />

interface ImportMetaEnv {
	readonly VITE_APP_VERSION?: string
	readonly VITE_SENTRY_DSN_WEB?: string
	readonly VITE_SENTRY_RELEASE?: string
	readonly VITE_SENTRY_ENVIRONMENT?: string
}

interface ImportMeta {
	readonly env: ImportMetaEnv
}

declare module "*.vue" {
	import type {DefineComponent} from "vue"
	const component: DefineComponent
	export default component
}
