import {afterEach, describe, expect, it} from "vitest"
import {createApp, h} from "vue"
import Icon from "../../src/components/Icon.vue"
import {icon, type IconName} from "../../src/services/icon"

/**
 * 图标的 duotone 模式。
 *
 * 这一族守的是一种**不报错的坏**: 改动前模板按模式给整个 svg 设 fill / stroke,
 * 而 duotone 两样都给了 none —— 任何声明了 duotone 的图标都会渲染成一片空白,
 * 控制台安静, 界面上就是"那里本来应该有个图标"。当时没有图标用 duotone,
 * 所以这条路一直没人走过。现在有十一个图标用它了。
 */

const mounts: Array<{app: ReturnType<typeof createApp>; container: HTMLDivElement}> = []

const render = (props: Record<string, unknown>) => {
	const container = document.createElement("div")
	document.body.appendChild(container)
	const app = createApp({render: () => h(Icon, props)})
	app.mount(container)
	mounts.push({app, container})
	return container.querySelector("svg")!
}

afterEach(() => {
	for (const {app, container} of mounts.splice(0)) {
		app.unmount()
		container.remove()
	}
})

/** 声明了 duotone 的图标。 */
const DUOTONE_ICONS = (Object.keys(icon) as IconName[])
	.filter(name => (icon[name].duotone?.length ?? 0) > 0)

describe("Icon.vue duotone", () => {
	it("有图标真的在用 duotone", () => {
		// 这条是前提: 一旦没人用 duotone, 下面那些断言就成了空转, 而渲染的坑还在。
		expect(DUOTONE_ICONS.length).toBeGreaterThan(0)
	})

	it.each(DUOTONE_ICONS)("%s 的 duotone 画得出东西", name => {
		const svg = render({name, mode: "duotone"})
		const paths = [...svg.querySelectorAll("path")]

		const filled = paths.filter(path => path.getAttribute("fill") === "currentColor")
		const stroked = paths.filter(path => path.getAttribute("stroke") === "currentColor")

		// 色块层与线条层都要在: 少了色块就是普通描边, 少了线条就是一团糊。
		expect(filled.length).toBeGreaterThan(0)
		expect(stroked.length).toBeGreaterThan(0)

		// 每条 path 都得有 d, 否则渲染出来仍然是空的
		for (const path of paths) expect(path.getAttribute("d")).toBeTruthy()
	})

	it("色块压在线条下面, 不是盖在上面", () => {
		const svg = render({name: DUOTONE_ICONS[0], mode: "duotone"})
		const paths = [...svg.querySelectorAll("path")]
		const firstStroked = paths.findIndex(path => path.getAttribute("stroke") === "currentColor")
		const lastFilled = paths.map(path => path.getAttribute("fill") === "currentColor")
			.lastIndexOf(true)

		expect(lastFilled).toBeLessThan(firstStroked)
	})

	it("图标没有 duotone 时退回描边, 而不是画一片空白", () => {
		// close 是通用符号, 刻意没给它画色块 —— 正好用来验退化路径。
		expect(icon.close.duotone).toBeUndefined()

		const svg = render({name: "close", mode: "duotone"})
		const stroked = [...svg.querySelectorAll("path")]
			.filter(path => path.getAttribute("stroke") === "currentColor")

		expect(stroked.length).toBeGreaterThan(0)
	})

	it("duotone 的轮廓复用 stroke, 不另存一份几何", () => {
		// 两份几何迟早会漂: 改了一处忘了另一处, 色块和线条就会错位。
		for (const name of DUOTONE_ICONS) {
			expect(icon[name].stroke?.length ?? 0).toBeGreaterThan(0)
			for (const solid of icon[name].duotone ?? []) {
				expect(icon[name].stroke).toContain(solid)
			}
		}
	})
})
