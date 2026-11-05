"""Offline configuration and manifest checks; no Azure or GitHub credentials."""

import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
from configuration import seed_settings, sharepoint_settings


def load_script(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / "scripts" / f"{name}.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


VARIABLES = {
    "SHAREPOINT_TENANT_ID": "11111111-1111-1111-1111-111111111111",
    "SHAREPOINT_CLIENT_ID": "22222222-2222-2222-2222-222222222222",
    "SHAREPOINT_HOST": "sharepoint.example.test",
    "SHAREPOINT_SITE_PREFIX": "/teams",
    "AZURE_SUBSCRIPTION_ID": "33333333-3333-3333-3333-333333333333",
    "AZURE_PRODUCTION_SUBSCRIPTION_ID": "44444444-4444-4444-4444-444444444444",
    "KEY_VAULT_NAME": "examplevault",
    "STORAGE_RESOURCE_GROUP": "example-storage",
    "STORAGE_ACCOUNT": "examplestorage",
    "COSMOS_RESOURCE_GROUP": "example-database",
    "COSMOS_ACCOUNT": "exampledatabase",
    "COSMOS_ENDPOINT": "https://cosmos.example.test/",
    "COSMOS_DATABASE": "example-db",
    "EVENTHUB_NAMESPACE": "eventhub.example.test",
    "CORS_ALLOWED_ORIGINS": "https://portal.example.test",
}


class ConfigurationTests(unittest.TestCase):
    def test_missing_sharepoint_variables_fail_without_fallback(self):
        for name in (key for key in VARIABLES if key.startswith("SHAREPOINT_")):
            with self.subTest(name=name), patch.dict(os.environ, VARIABLES, clear=True):
                del os.environ[name]
                with self.assertRaisesRegex(ValueError, name):
                    sharepoint_settings()

    def test_host_rejects_urls_and_seed_requires_input(self):
        with patch.dict(os.environ, {**VARIABLES, "SHAREPOINT_HOST": "https://sharepoint.example.test"}, clear=True):
            with self.assertRaisesRegex(ValueError, "SHAREPOINT_HOST"):
                sharepoint_settings()
            with self.assertRaisesRegex(ValueError, "MISSING_ENDPOINT"):
                seed_settings({"ENDPOINT": "${MISSING_ENDPOINT}"})

    def test_test_generator_takes_identifiers_from_variables_and_secret_from_vault(self):
        module = load_script("generate-test-appsettings")
        def azure(*args):
            if args[:3] == ("storage", "account", "show"):
                return {"primaryEndpoints": {"blob": "https://storage.example.test/"}}
            return {"value": json.dumps({"SHAREPOINT_CLIENT_SECRET": "test-only-secret",
                                         "SHAREPOINT_HOST": "stale.example.test"})}
        with tempfile.TemporaryDirectory() as directory, patch.dict(os.environ, VARIABLES, clear=True), \
                patch.object(module, "azure_json", side_effect=azure), \
                patch.object(module, "OUTPUT", Path(directory) / "worker.json"), \
                patch.object(module, "CONVERTER_OUTPUT", Path(directory) / "converter.json"), \
                contextlib.redirect_stdout(io.StringIO()) as output:
            module.main()
            settings = json.loads(module.OUTPUT.read_text())
            self.assertEqual(settings["SHAREPOINT_HOST"], VARIABLES["SHAREPOINT_HOST"])
            self.assertEqual(settings["SHAREPOINT_SITE_PREFIX"], "/teams")
            self.assertEqual(settings["SHAREPOINT_CLIENT_SECRET"], "test-only-secret")
            self.assertEqual(settings["COSMOS_ENDPOINT"], VARIABLES["COSMOS_ENDPOINT"])
            self.assertEqual(module.OUTPUT.stat().st_mode & 0o777, 0o600)
            self.assertNotIn("test-only-secret", output.getvalue())

    def test_production_generator_takes_sharepoint_variables_with_vault_secret(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict(os.environ, VARIABLES, clear=True):
            module = load_script("generate-production-appsettings")
            worker, converter = Path(directory) / "worker.json", Path(directory) / "converter.json"
            source = {"COSMOS_DATABASE": "example-db", "EVENTHUB_NAMESPACE": "eventhub.example.test",
                      "NOTIFICATION_SERVICE_URL": "https://notification.example.test/api/sendNotice"}
            def azure(subscription, *args):
                if args[:2] == ("keyvault", "secret"):
                    return {"value": json.dumps(source)}
                if args[0] == "cosmosdb":
                    return {"documentEndpoint": VARIABLES["COSMOS_ENDPOINT"]}
                return {"primaryEndpoints": {"blob": "https://storage.example.test/"}}
            with patch.object(module, "azure_json", side_effect=azure), \
                    patch.object(module, "graph_secret", return_value="test-only-secret"), \
                    patch.object(module, "production_origins", return_value="https://portal.example.test"), \
                    patch.object(module, "missing_blob_containers", return_value=[]), \
                    patch.object(sys, "argv", ["generate", "--output", str(worker), "--converter-output", str(converter)]), \
                    contextlib.redirect_stdout(io.StringIO()) as output:
                module.main()
            settings = json.loads(worker.read_text())
            self.assertEqual(settings["SHAREPOINT_HOST"], VARIABLES["SHAREPOINT_HOST"])
            self.assertEqual(settings["SHAREPOINT_TENANT_ID"], VARIABLES["SHAREPOINT_TENANT_ID"])
            self.assertEqual(settings["SHAREPOINT_CLIENT_SECRET"], "test-only-secret")
            self.assertEqual(worker.stat().st_mode & 0o777, 0o600)
            self.assertNotIn("test-only-secret", output.getvalue())

    def test_both_deployment_environments_render_only_supplied_values(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "bundle.yaml"
            env = {
                "PATH": os.environ["PATH"], "DRY_RUN": "1", "RENDER_OUTPUT": str(output),
                "AKS_SUBSCRIPTION_ID": VARIABLES["AZURE_SUBSCRIPTION_ID"],
                "AZURE_TENANT_ID": VARIABLES["SHAREPOINT_TENANT_ID"],
                "AZURE_WORKLOAD_IDENTITY_CLIENT_ID": VARIABLES["SHAREPOINT_CLIENT_ID"],
                "AZURE_CONVERTER_WORKLOAD_IDENTITY_CLIENT_ID": VARIABLES["SHAREPOINT_CLIENT_ID"],
                "AKS_RESOURCE_GROUP": "example-group", "AKS_CLUSTER_NAME": "example-cluster",
                "KEY_VAULT_NAME": "examplevault", "ACR_LOGIN_SERVER": "registry.example.test",
                "IMAGE_TAG": "sha-" + "0" * 40,
                "MARKDOWN_CONVERTER_INGRESS_HOST": "converter.example.test",
                "MARKDOWN_CONVERTER_TLS_CERT_KEYVAULT_URI": "https://examplevault.vault.azure.net/certificates/example",
            }
            for name in ("test", "production"):
                with self.subTest(environment=name):
                    subprocess.run(["bash", f"scripts/deploy-aks-{name}.sh"], env=env,
                                   cwd=ROOT, capture_output=True, check=True)
                    bundle = output.read_text()
                    self.assertNotIn("<", bundle)
                    self.assertIn("registry.example.test/aimie-operations:sha-", bundle)
                    self.assertIn("converter.example.test", bundle)
                    invalid = dict(env)
                    del invalid["ACR_LOGIN_SERVER"]
                    result = subprocess.run(["bash", f"scripts/deploy-aks-{name}.sh"], env=invalid,
                                            cwd=ROOT, capture_output=True, text=True)
                    self.assertNotEqual(result.returncode, 0)
                    self.assertIn("ACR_LOGIN_SERVER", result.stderr)


if __name__ == "__main__":
    unittest.main()
