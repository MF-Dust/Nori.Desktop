export interface PluginSettingsMessages {
	plugins: {
		title: string
		subtitle: string
		action: {
			install: string
			enable: string
			disable: string
			retry: string
			uninstall: string
			details: string
			cancel: string
			confirm: string
		}
		empty: {title: string; desc: string}
		status: Record<string, string>
		permissions: {
			title: string
			required: string
			optional: string
			granted: string
			unavailable: string
			undeclared: string
			none: string
		}
		risk: {title: string; desc: string; confirm: string}
		uninstall: {title: string; desc: string; deleteData: string; deleteDataHint: string}
		safeMode: {title: string; desc: string}
		restart: string
		error: {load: string; install: string; enable: string; disable: string; uninstall: string}
		detail: {id: string; version: string; author: string; license: string; homepage: string; repository: string; error: string}
		search: {plugin: string; extension: string; install: string; package: string}
	}
}

export interface LocaleMessageTree {
	[key: string]: string | LocaleMessageTree
}

const isLocaleMessageTree = (value: unknown): value is LocaleMessageTree => {
	if (typeof value !== "object" || value === null || Array.isArray(value)) return false
	return Object.values(value).every(item => typeof item === "string" || isLocaleMessageTree(item))
}

export const PLUGIN_MESSAGES: Record<"zh-CN" | "en-US", PluginSettingsMessages> = {
	"zh-CN": {
		plugins: {
			title: "插件管理",
			subtitle: "管理本地插件、扩展、权限状态与 .noripack 安装包。第三方插件在 Nori 进程内运行，仅启用可信来源。",
			action: {install: "安装本地插件", enable: "启用", disable: "禁用", retry: "重试启用", uninstall: "卸载", details: "详情", cancel: "取消", confirm: "确认"},
			empty: {title: "还没有安装插件", desc: "可以从本地 .noripack 安装扩展。新安装的插件默认保持禁用，确认后再手动启用。"},
			status: {installed: "已安装", loading: "正在加载", active: "已启用", stopping: "正在停用", disabled: "已禁用", failed: "启动失败", incompatible: "不兼容", pending_restart: "等待重启"},
			permissions: {title: "权限", required: "必需", optional: "可选", granted: "已授权", unavailable: "当前不可用", undeclared: "未声明", none: "未声明插件权限"},
			risk: {title: "信任本地插件", desc: "Nori 插件是受信任的进程内代码。启用后，它会在 Nori 进程中执行。仅安装你信任来源提供的 .noripack。", confirm: "我了解风险，继续选择文件"},
			uninstall: {title: "卸载插件", desc: "插件程序文件会被删除。默认保留插件数据，方便之后重新安装。", deleteData: "同时删除插件数据", deleteDataHint: "开启后会删除该插件自己的数据目录，此操作无法撤销。"},
			safeMode: {title: "安全模式", desc: "安全模式允许查看、禁用和卸载插件，安装与启用功能暂时关闭。"},
			restart: "需要重启 Nori 后完成当前操作。",
			error: {load: "插件列表加载失败", install: "插件安装失败", enable: "插件启用失败", disable: "插件禁用失败", uninstall: "插件卸载失败"},
			detail: {id: "插件 ID", version: "版本", author: "作者", license: "许可证", homepage: "主页", repository: "代码仓库", error: "错误"},
			search: {plugin: "插件", extension: "扩展", install: "安装", package: "noripack"},
		},
	},
	"en-US": {
		plugins: {
			title: "Plugin management",
			subtitle: "Manage local plugins, extensions, capability status, and .noripack packages. Third-party plugins run in the Nori process, so only enable trusted sources.",
			action: {install: "Install local plugin", enable: "Enable", disable: "Disable", retry: "Retry enable", uninstall: "Uninstall", details: "Details", cancel: "Cancel", confirm: "Confirm"},
			empty: {title: "No plugins installed", desc: "Install extensions from a local .noripack. Newly installed plugins stay disabled until you explicitly enable them."},
			status: {installed: "Installed", loading: "Loading", active: "Enabled", stopping: "Stopping", disabled: "Disabled", failed: "Startup failed", incompatible: "Incompatible", pending_restart: "Restart pending"},
			permissions: {title: "Capabilities", required: "Required", optional: "Optional", granted: "Granted", unavailable: "Unavailable", undeclared: "Not declared", none: "No plugin capabilities declared"},
			risk: {title: "Trust local plugins", desc: "Nori plugins are trusted in-process code. Once enabled, they execute inside the Nori process. Only install .noripack files from sources you trust.", confirm: "I understand the risk and want to choose a file"},
			uninstall: {title: "Uninstall plugin", desc: "Plugin program files will be removed. Plugin data is kept by default so it can be reused after reinstalling.", deleteData: "Also delete plugin data", deleteDataHint: "When enabled, this deletes only this plugin's data directory and cannot be undone."},
			safeMode: {title: "Safe Mode", desc: "Safe Mode allows viewing, disabling, and uninstalling plugins. Install and enable actions are unavailable."},
			restart: "Restart Nori to finish this operation.",
			error: {load: "Failed to load plugins", install: "Plugin installation failed", enable: "Failed to enable plugin", disable: "Failed to disable plugin", uninstall: "Failed to uninstall plugin"},
			detail: {id: "Plugin ID", version: "Version", author: "Author", license: "License", homepage: "Homepage", repository: "Repository", error: "Error"},
			search: {plugin: "plugin", extension: "extension", install: "install", package: "noripack"},
		},
	},
}

export const mergePluginMessages = (locale: string, source: unknown): LocaleMessageTree => {
	if (!isLocaleMessageTree(source)) throw new TypeError("语言资源格式无效")
	const additions = PLUGIN_MESSAGES[locale as "zh-CN" | "en-US"]
	if (!additions) return source
	const views = isLocaleMessageTree(source.views) ? source.views : {}
	const main = isLocaleMessageTree(views.main) ? views.main : {}
	return {
		...source,
		views: {
			...views,
			main: {
				...main,
				plugins: additions.plugins,
			},
		},
	}
}
