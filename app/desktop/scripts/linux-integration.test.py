#!/usr/bin/env python3
"""Linux 桌面注册与发布冒烟脚本的隔离 fixture 测试。"""

import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import unittest

SCRIPTS = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("smoke", SCRIPTS / "smoke-published.py")
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)


class DesktopTests(unittest.TestCase):
	def setUp(self):
		self.temporary = tempfile.TemporaryDirectory(prefix="nori-integration-")
		self.addCleanup(self.temporary.cleanup)
		self.base = Path(self.temporary.name)
		self.root = self.base / '包 空格 "引号" $美元 `反引号` %百分号 \\斜线'
		self.root.mkdir()
		shutil.copyfile(SCRIPTS / "linux-desktop.sh", self.root / "linux-desktop.sh")
		(self.root / "Nori").write_text("#!/bin/sh\nexit 0\n")
		(self.root / "Nori").chmod(0o755)
		(self.root / ".current").write_text("app-0.0.0-0\n")
		(self.root / "nori.png").touch()
		self.env = dict(os.environ, HOME=str(self.base / "home"), XDG_DATA_HOME=str(self.base / "data"), XDG_CONFIG_HOME=str(self.base / "config"))
		self.menu = self.base / "data/applications/cn.erhio.noriDesktopPet.desktop"
		self.autostart = self.base / "config/autostart/cn.erhio.noriDesktopPet.desktop"

	def run_script(self, command, expected=0):
		result = subprocess.run(["bash", str(self.root / "linux-desktop.sh"), command], env=self.env, capture_output=True, text=True)
		self.assertEqual(result.returncode, expected, result.stdout + result.stderr)

	def test_registration_and_removal(self):
		self.run_script("install")
		self.assertFalse(self.autostart.exists())
		entry = self.menu.read_text()
		self.assertIn("%%百分号", entry)
		self.assertNotIn("app-0.0.0", entry)
		self.assertFalse((self.root / "data").exists())
		if shutil.which("desktop-file-validate"):
			subprocess.run(["desktop-file-validate", str(self.menu)], check=True)
		self.run_script("install")
		self.run_script("enable-autostart")
		self.assertEqual(self.autostart.read_text(), entry)
		self.run_script("disable-autostart")
		self.assertFalse(self.autostart.exists())
		self.assertTrue(self.menu.exists())
		self.run_script("enable-autostart")
		self.run_script("uninstall")
		self.run_script("uninstall")
		self.assertFalse(self.menu.exists())
		self.assertFalse(self.autostart.exists())

	@unittest.skipUnless(shutil.which("gio"), "未安装 GIO，跳过真实 desktop-entry 启动")
	def test_gio_launches_special_character_path(self):
		(self.root / "Nori").write_text("#!/bin/sh\npwd > launched\n")
		self.run_script("install")
		result = subprocess.run(["gio", "launch", str(self.menu)], env=self.env, capture_output=True, text=True)
		self.assertEqual(result.returncode, 0, result.stderr)
		marker = self.root / "launched"
		for _ in range(50):
			if marker.exists():
				break
			time.sleep(0.1)
		self.assertEqual(marker.read_text().strip(), str(self.root))

	def test_home_fallback(self):
		self.env.pop("XDG_DATA_HOME")
		self.env.pop("XDG_CONFIG_HOME")
		self.run_script("install")
		self.run_script("enable-autostart")
		self.assertTrue((self.base / "home/.local/share/applications" / self.menu.name).exists())
		self.assertTrue((self.base / "home/.config/autostart" / self.menu.name).exists())

	def test_reject_relative_xdg_and_control_characters(self):
		for value in ("relative", str(self.base / "bad\npath")):
			self.env["XDG_DATA_HOME"] = value
			self.run_script("install", 2)
		self.assertFalse(self.menu.exists())

	def test_missing_launcher(self):
		(self.root / "Nori").unlink()
		self.run_script("install", 1)
		self.assertFalse(self.menu.exists())

	def test_dont_remove_another_package(self):
		self.run_script("install")
		self.menu.write_text("[Desktop Entry]\nX-Nori-PackageRoot=/another/package\n")
		self.run_script("uninstall", 1)
		self.assertTrue(self.menu.exists())

	def test_removal_after_launcher_deleted(self):
		self.run_script("install")
		(self.root / "Nori").unlink()
		self.run_script("uninstall")
		self.assertFalse(self.menu.exists())


@unittest.skipUnless(shutil.which("dotnet"), "未安装 .NET SDK，跳过构建属性验证")
class NativeRidTests(unittest.TestCase):
	def test_distribution_sdk_uses_portable_rid_without_overriding_explicit_target(self):
		for project, property_name in (("Nori.Core", "NoriUdaRid"), ("Nori.Desktop", "NoriCoreRid")):
			for portable, explicit in (("linux-x64", None), ("win-x64", None), ("osx-arm64", None), ("linux-x64", "win-x64")):
				with self.subTest(project=project, portable=portable, explicit=explicit):
					command = ["dotnet", "msbuild", str(SCRIPTS.parent / project / f"{project}.csproj"), "-nologo", f"-getProperty:{property_name}", "-p:NETCoreSdkRuntimeIdentifier=arch-x64", f"-p:NETCoreSdkPortableRuntimeIdentifier={portable}"]
					if explicit:
						command.append(f"-p:RuntimeIdentifier={explicit}")
					result = subprocess.check_output(command, text=True).strip()
					self.assertEqual(result, explicit or portable)


class SmokeTests(unittest.TestCase):
	def setUp(self):
		self.temporary = tempfile.TemporaryDirectory(prefix="nori-smoke-fixture-")
		self.addCleanup(self.temporary.cleanup)
		self.profile = Path(self.temporary.name).resolve()
		database = self.profile / "data/core/database/nori.db"
		database.parent.mkdir(parents=True)
		database.touch()
		self.ready = dict(schema_version=2, status="ready", mode="first-run", safe_mode=False, first_run=True, initial_window="first-run", product_version="Dev", database_schema_version=1, config_schema_version=1, data_dir=str(self.profile / "data"))

	def check(self):
		smoke.check_readiness(self.ready, self.profile, "first-run", False, "Dev")

	def test_valid_readiness(self):
		self.check()

	def test_invalid_readiness(self):
		for key, value in dict(schema_version=1, status="starting", mode="initialized", safe_mode=True, first_run=False, initial_window="main", product_version="wrong", database_schema_version=0, config_schema_version=True, data_dir=str(self.profile)).items():
			with self.subTest(key=key):
				original = self.ready[key]
				self.ready[key] = value
				with self.assertRaises((AssertionError, ValueError)):
					self.check()
				self.ready[key] = original

	def test_missing_database(self):
		(self.profile / "data/core/database/nori.db").unlink()
		with self.assertRaises((AssertionError, ValueError)):
			self.check()

	def test_script_launches_isolated_profile(self):
		binary = self.profile / "Nori"
		# 假宿主消费真实参数，模拟 readiness 与数据库；无需桌面或 .NET。
		binary.write_text('''#!/usr/bin/env python3
import json, pathlib, sys
profile = pathlib.Path(sys.argv[sys.argv.index("--profile") + 1])
assert not list(profile.iterdir())
mode = sys.argv[sys.argv.index("--smoke-test") + 1]
database = profile / "data/core/database/nori.db"
database.parent.mkdir(parents=True)
database.touch()
ready = dict(schema_version=2, status="ready", mode=mode, safe_mode="--safe-mode" in sys.argv, first_run=mode == "first-run", initial_window="first-run" if mode == "first-run" else "init", product_version="Dev", database_schema_version=1, config_schema_version=1, data_dir=str(profile / "data"))
(profile / "readiness.json").write_text(json.dumps(ready))
''')
		binary.chmod(0o755)
		for args in ([], ["--mode", "initialized"], ["--mode", "initialized", "--safe-mode"]):
			result = subprocess.run(["python3", str(SCRIPTS / "smoke-published.py"), str(binary), "--expected-version", "Dev", *args], capture_output=True, text=True)
			self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
		self.assertFalse((self.profile / "readiness.json").exists())
		wrong_version = subprocess.run(["python3", str(SCRIPTS / "smoke-published.py"), str(binary), "--expected-version", "wrong"], capture_output=True, text=True)
		self.assertNotEqual(wrong_version.returncode, 0)
		self.assertIn("产品版本不匹配", wrong_version.stderr)

	def test_script_rejects_missing_checkpoint_or_failed_process(self):
		binary = self.profile / "Nori"
		for code in (0, 3):
			binary.write_text(f"#!/bin/sh\nexit {code}\n")
			binary.chmod(0o755)
			result = subprocess.run(["python3", str(SCRIPTS / "smoke-published.py"), str(binary)], capture_output=True, text=True)
			self.assertNotEqual(result.returncode, 0)


if __name__ == "__main__":
	unittest.main()
