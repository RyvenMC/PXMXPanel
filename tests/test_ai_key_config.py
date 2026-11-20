import importlib.util
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

try:
    import yaml
except ModuleNotFoundError:
    sys.modules["yaml"] = types.SimpleNamespace()

ROOT = pathlib.Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("pxmx_mod_test", str(ROOT / "gateway" / "mod.py"))
MOD = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MOD)


class AiKeyConfigTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.workdir = pathlib.Path(self.temp.name)
        self.compose_file = self.workdir / "compose.yml"
        self.compose_file.write_text("services: {}\n", encoding="utf-8")
        self.labels = {
            "com.docker.compose.project.working_dir": str(self.workdir),
            "com.docker.compose.project.config_files": str(self.compose_file),
            "com.docker.compose.project": "pxmx-test",
            "com.docker.compose.service": "bot",
        }
        self.compose_error = None
        self.commands = []
        self.run_patch = patch.object(MOD.subprocess, "run", side_effect=self.fake_run)
        self.which_patch = patch.object(MOD.shutil, "which", return_value=None)
        self.run_patch.start()
        self.which_patch.start()
        self.addCleanup(self.run_patch.stop)
        self.addCleanup(self.which_patch.stop)

    def tearDown(self):
        self.temp.cleanup()

    def fake_run(self, command, **kwargs):
        self.commands.append(command)
        if command[:2] == ["docker", "inspect"]:
            return subprocess.CompletedProcess(command, 0, json.dumps(self.labels).encode(), b"")
        if self.compose_error is not None:
            return subprocess.CompletedProcess(command, 1, b"", self.compose_error)
        return subprocess.CompletedProcess(command, 0, b"", b"")

    def test_writes_key_to_compose_override_without_returning_it(self):
        key = "sk-ant-test-${DROP}-0123456789"

        result = MOD.ki_api_key_set({"api_key": key})

        override = self.workdir / "docker-compose.pxmx-ai.override.yml"
        self.assertTrue(result["api_key_set"])
        self.assertNotIn("api_key", result)
        self.assertIn(key.replace("$", "$$"), override.read_text(encoding="utf-8"))
        self.assertIn("pxmx-test", self.commands[-1])
        self.assertIn("bot", self.commands[-1])
        if os.name != "nt":
            self.assertEqual(os.stat(override).st_mode & 0o777, 0o600)

    def test_compose_failure_restores_old_key_and_redacts_new_key(self):
        old_key = "sk-ant-old-key-0123456789"
        new_key = "sk-ant-new-key-9876543210"
        MOD.ki_api_key_set({"api_key": old_key})
        override = self.workdir / "docker-compose.pxmx-ai.override.yml"
        old_content = override.read_text(encoding="utf-8")
        self.compose_error = ("rejected " + new_key).encode()

        with self.assertRaises(MOD.Fail) as error:
            MOD.ki_api_key_set({"api_key": new_key})

        self.assertNotIn(new_key, str(error.exception))
        self.assertEqual(override.read_text(encoding="utf-8"), old_content)

    def test_clearing_key_keeps_compose_service_but_removes_secret(self):
        result = MOD.ki_api_key_set({"api_key": ""})

        content = (self.workdir / "docker-compose.pxmx-ai.override.yml").read_text(encoding="utf-8")
        self.assertFalse(result["api_key_set"])
        self.assertIn('ANTHROPIC_API_KEY: ""', content)


if __name__ == "__main__":
    unittest.main()
