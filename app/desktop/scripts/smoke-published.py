#!/usr/bin/env python3
"""Linux 发布包冒烟：只使用自建临时 profile，绝不读取已有用户数据。"""

import argparse
import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile


def require(condition, message):
	if not condition:
		raise ValueError(message)


def check_readiness(ready, profile, mode, safe_mode, expected_version):
	"""校验与 Windows 冒烟一致的 schema v2 契约。"""
	require(ready.get("schema_version") == 2, "readiness schema_version 必须为 2")
	require(ready.get("status") == "ready", "宿主未就绪")
	require(ready.get("mode") == mode, "冒烟模式不匹配")
	require(ready.get("safe_mode") is safe_mode, "安全模式不匹配")
	require(ready.get("first_run") is (mode == "first-run"), "首次运行分支不匹配")
	require(ready.get("initial_window") == ("first-run" if mode == "first-run" else "init"), "启动窗口不匹配")
	require(ready.get("product_version"), "缺少产品版本")
	if expected_version:
		require(ready["product_version"] == expected_version, "产品版本不匹配")
	for key in ("database_schema_version", "config_schema_version"):
		require(type(ready.get(key)) is int and ready[key] >= 1, f"{key} 无效")
	require(Path(ready["data_dir"]).resolve() == profile / "data", "数据目录逸出临时 profile")
	require((profile / "data/core/database/nori.db").is_file(), "未创建配置数据库")


def main():
	parser = argparse.ArgumentParser(description=__doc__)
	parser.add_argument("binary", type=Path)
	parser.add_argument("--mode", choices=("first-run", "initialized"), default="first-run")
	parser.add_argument("--safe-mode", action="store_true")
	parser.add_argument("--expected-version", default="")
	args = parser.parse_args()
	binary = args.binary.resolve(strict=True)
	with tempfile.TemporaryDirectory(prefix="nori-smoke-") as temporary:
		profile = Path(temporary).resolve()
		command = [str(binary), "--smoke-test", args.mode, "--profile", str(profile)]
		if args.safe_mode:
			command.append("--safe-mode")
		# 稳定 launcher 会等待宿主；超时必须回收整个进程组，不能遗留桌宠或启动画面。
		with tempfile.TemporaryFile(mode="w+") as output:
			process = subprocess.Popen(command, cwd=binary.parent, stdout=output, stderr=subprocess.STDOUT, start_new_session=True)
			try:
				try:
					code = process.wait(timeout=45)
					if code != 0:
						raise RuntimeError(f"发布程序退出码不是 0: {code}")
					with (profile / "readiness.json").open(encoding="utf-8") as source:
						check_readiness(json.load(source), profile, args.mode, args.safe_mode, args.expected_version)
				except Exception:
					output.seek(0)
					print(output.read())
					raise
			finally:
				try:
					os.killpg(process.pid, signal.SIGKILL)
				except ProcessLookupError:
					pass
				process.wait()
	print(f"发布冒烟通过: {args.mode}, safe_mode={args.safe_mode}")


if __name__ == "__main__":
	main()
