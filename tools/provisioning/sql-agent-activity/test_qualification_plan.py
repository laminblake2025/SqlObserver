"""Pure offline contract tests. No SQL, network, installer, or shared .NET build."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

FOLDER = Path(__file__).resolve().parent
ROOT = FOLDER.parents[2]
PWSH = shutil.which("pwsh")


class QualificationPlanTests(unittest.TestCase):
    def run_plan(self, principal=r"LAB\Monitor", folder=FOLDER, extra=()):
        self.assertIsNotNone(PWSH, "PowerShell7 is required; do not silently skip validation")
        return subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-File",
                               str(folder / "New-QualificationPlan.ps1"),
                               "-Principal", principal, *extra], capture_output=True,
                              text=True, encoding="utf-8", timeout=20)

    def test_module_exactly_preserves_pinned_source_capture_body(self):
        manifest = json.loads((FOLDER / "capture-v1.manifest.json").read_text())
        source = (ROOT / manifest["sourceAsset"]).read_bytes()
        self.assertEqual(manifest["sourceAssetSha256"], hashlib.sha256(source).hexdigest())
        text = source.decode("utf-8")
        projection = (ROOT / manifest["serviceProjectionAsset"]).read_bytes()
        self.assertEqual(manifest["serviceProjectionSha256"], hashlib.sha256(projection).hexdigest())
        marker = "/*SQLAGENT_SERVICE_PROJECTION_V2*/"
        self.assertEqual(2, text.count(marker))
        text = text.replace(marker, projection.decode("utf-8"))
        self.assertEqual(manifest["expandedSourceUtf8Sha256"], hashlib.sha256(text.encode()).hexdigest())
        body = text[text.index("SET NOCOUNT ON;"):]
        body = body.replace("IF @maximum_rows <> 513 THROW", "IF @maximum_rows IS NULL OR @maximum_rows <> 513 THROW")
        expected = "CREATE PROCEDURE [sqlobserver_agent].[capture_v1]\n    @maximum_rows int\nWITH EXECUTE AS CALLER\nAS\nBEGIN\n" + body + "END;\n"
        actual = (FOLDER / "capture-v1.sql").read_bytes()
        self.assertEqual(expected.encode(), actual)
        self.assertEqual(manifest["moduleUtf8Sha256"], hashlib.sha256(actual).hexdigest())
        self.assertEqual(manifest["expectedSqlDefinitionUtf16LeSha256"], hashlib.sha256(expected.encode("utf-16le")).hexdigest())
        self.assertNotIn(b"\r", actual)
        self.assertFalse(actual.startswith(b"\xef\xbb\xbf"))
        self.assertNotIn(marker, actual.decode())

    def test_preview_is_explicitly_non_executable_and_minimally_scoped(self):
        result = self.run_plan()
        self.assertEqual(0, result.returncode, result.stderr)
        plan = json.loads(result.stdout)
        self.assertEqual("offline_preview_only", plan["operation"])
        self.assertFalse(plan["installerImplemented"])
        self.assertFalse(plan["removalImplemented"])
        self.assertFalse(plan["executableInstallOrRemovalSqlGenerated"])
        self.assertFalse(plan["runtimeAdmissionChanged"])
        self.assertFalse(plan["directPermissionEvidenceMayBeSynthesized"])
        self.assertEqual(7, len(plan["proposedObjects"]))
        self.assertEqual(5, len(plan["proposedGrants"]))
        self.assertIn("VIEW SERVER SECURITY STATE -> certificate login only", plan["proposedGrants"][0])

    def test_identity_quoting_is_lossless(self):
        principal = "LAB\\O'Brien]雪"
        result = self.run_plan(principal)
        self.assertEqual(0, result.returncode, result.stderr)
        plan = json.loads(result.stdout)
        self.assertEqual(principal, plan["principal"])
        self.assertEqual("[LAB\\O'Brien]]雪]", plan["principalSqlIdentifier"])
        self.assertEqual("N'LAB\\O''Brien]雪'", plan["principalSqlLiteral"])

    def test_invalid_principals_fail(self):
        for principal in ["sysadmin", " LAB\\Monitor", "LAB\\Monitor\n", "x" * 130 + "\\m", "a\\b\\c"]:
            with self.subTest(principal=repr(principal)):
                self.assertNotEqual(0, self.run_plan(principal).returncode)

    def test_output_is_create_only_and_unsupported_actions_refused(self):
        with tempfile.TemporaryDirectory() as temp:
            path = str(Path(temp) / "plan.json")
            self.assertEqual(0, self.run_plan(extra=("-OutputPath", path)).returncode)
            original = Path(path).read_bytes()
            self.assertNotEqual(0, self.run_plan(extra=("-OutputPath", path)).returncode)
            self.assertEqual(original, Path(path).read_bytes())
            self.assertNotEqual(0, self.run_plan(extra=("-Operation", "Install")).returncode)

    def test_tampered_module_or_manifest_is_rejected_without_output(self):
        for asset in ("capture-v1.sql", "capture-v1.manifest.json", "verify-v1.sql", "unexpected-manifest-field"):
            with self.subTest(asset=asset), tempfile.TemporaryDirectory() as temp:
                folder = Path(temp)
                for name in ("New-QualificationPlan.ps1", "capture-v1.sql", "capture-v1.manifest.json", "verify-v1.sql"):
                    shutil.copyfile(FOLDER / name, folder / name)
                path = folder / ("capture-v1.manifest.json" if asset == "unexpected-manifest-field" else asset)
                if asset.endswith(".sql"):
                    path.write_bytes(path.read_bytes() + b"-- altered\n")
                else:
                    value = json.loads(path.read_text())
                    value["unexpectedField" if asset == "unexpected-manifest-field" else "installerImplemented"] = True
                    path.write_text(json.dumps(value))
                output = folder / "must-not-exist.json"
                self.assertNotEqual(0, self.run_plan(folder=folder, extra=("-OutputPath", str(output))).returncode)
                self.assertFalse(output.exists())

    def test_verification_is_read_only_and_uses_signature_validity(self):
        sql = (FOLDER / "verify-v1.sql").read_text()
        executable = "\n".join(line for line in sql.splitlines() if not line.lstrip().startswith("--"))
        self.assertRegex(executable, r"sys\.fn_check_object_signatures")
        self.assertRegex(executable, r"OBJECT_DEFINITION")
        self.assertNotRegex(executable.upper(), r"\b(CREATE|ALTER|DROP|GRANT|REVOKE|DENY|INSERT|UPDATE|DELETE|EXECUTE)\b")
        self.assertNotIn("CERTPRIVATEKEY", executable.upper())


if __name__ == "__main__":
    unittest.main(verbosity=2)
