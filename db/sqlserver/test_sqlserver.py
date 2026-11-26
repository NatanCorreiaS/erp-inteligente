"""The role matrix with native GRANT/DENY in SQL Server 2022 (db/sqlserver/roles.sql), checked user by user.

Starts a throwaway container (mcr.microsoft.com/mssql/server:2022-latest), applies roles.sql and impersonates each user
of the seed with EXECUTE AS USER through dbo.probe. Errors: 229 = permission denied on an object, 230 = on a column,
15151/15247 = no permission to change a role, 33504 = blocked by the row-level security policy.

The SA password is random, generated for each run and passed to the container and to sqlcmd only through environment
variables (never in the repository or on a command line). The container is removed at the end. Skipped without Docker.

    python3 -m unittest db/sqlserver/test_sqlserver.py -v
"""

import os
import pathlib
import secrets
import shutil
import subprocess
import time
import unittest

IMAGE = "mcr.microsoft.com/mssql/server:2022-latest"
SQLCMD = "/opt/mssql-tools18/bin/sqlcmd"
SCRIPT = pathlib.Path(__file__).resolve().parent / "roles.sql"


def docker_available():
    if shutil.which("docker") is None:
        return False
    return subprocess.run(["docker", "info"], capture_output=True).returncode == 0


@unittest.skipUnless(docker_available(), "Docker is not available")
class SqlServerRoleTests(unittest.TestCase):
    container = None
    password = None

    @classmethod
    def setUpClass(cls):
        # Complexity rule of SQL Server: upper, lower, digit and symbol.
        cls.password = "Nx" + secrets.token_urlsafe(18) + "9!a"
        env = dict(os.environ, MSSQL_SA_PASSWORD=cls.password)
        cls.container = "nexus-sqlserver-test-" + secrets.token_hex(4)
        subprocess.run(["docker", "run", "-d", "--rm", "--name", cls.container, "-e", "ACCEPT_EULA=Y",
                        "-e", "MSSQL_SA_PASSWORD", "-e", "MSSQL_PID=Developer", IMAGE],
                       check=True, capture_output=True, env=env)
        deadline = time.monotonic() + 120
        while True:
            if cls.sql("SELECT 1", database="master", check=False).returncode == 0:
                break
            if time.monotonic() > deadline:
                raise RuntimeError("SQL Server did not start in 120 s")
            time.sleep(2)
        subprocess.run(["docker", "cp", str(SCRIPT), f"{cls.container}:/tmp/roles.sql"], check=True, capture_output=True)
        applied = cls.run_sqlcmd(["-i", "/tmp/roles.sql"])
        if applied.returncode != 0:
            raise RuntimeError("roles.sql failed:\n" + applied.stdout + applied.stderr)

    @classmethod
    def tearDownClass(cls):
        if cls.container:
            subprocess.run(["docker", "rm", "-f", cls.container], capture_output=True)

    @classmethod
    def run_sqlcmd(cls, args, database=None):
        command = ["docker", "exec", "-e", "SQLCMDPASSWORD", cls.container, SQLCMD, "-S", "localhost", "-U", "sa", "-C",
                   "-b", "-h", "-1", "-W"]
        if database:
            command += ["-d", database]
        env = dict(os.environ, SQLCMDPASSWORD=cls.password)
        return subprocess.run(command + args, capture_output=True, text=True, env=env)

    @classmethod
    def sql(cls, query, database="nexus_roles", check=True):
        result = cls.run_sqlcmd(["-Q", "SET NOCOUNT ON; " + query], database=database)
        if check and result.returncode != 0:
            raise AssertionError(result.stdout + result.stderr)
        return result

    def probe(self, user, statement, warehouse=1):
        """'OK <values>' or 'ERR <number>' for the statement run as the user."""
        quoted = statement.replace("'", "''")
        out = self.sql(f"EXEC dbo.probe @user = N'{user}', @sql = N'{quoted}', @warehouse = {warehouse}").stdout
        return out.strip().splitlines()[-1].strip()

    def assertAllowed(self, user, statement):
        result = self.probe(user, statement)
        self.assertTrue(result.startswith("OK"), f"{user}: {statement} -> {result}")
        return result[3:]

    def assertDenied(self, user, statement, *codes):
        result = self.probe(user, statement)
        self.assertIn(result, [f"ERR {c}" for c in codes], f"{user}: {statement}")

    # ---- stock.read and sensitive.read -------------------------------------------------------------------

    def test_every_human_role_reads_stock(self):
        for user in ("op_joao", "sup_maria", "adm_carlos", "aud_lucia"):
            self.assertAllowed(user, "SELECT CAST(SUM(quantity) AS NVARCHAR(20)) FROM dbo.stock_balances")

    def test_deny_on_the_cost_column_beats_the_inherited_grant(self):
        # Operator and auditor inherit GRANT SELECT on the whole table from nexus_staff; DENY on the column wins.
        for user in ("op_joao", "aud_lucia"):
            self.assertAllowed(user, "SELECT sku FROM dbo.products WHERE id = 1")
            self.assertDenied(user, "SELECT CAST(unit_cost AS NVARCHAR(20)) FROM dbo.products WHERE id = 1", 230)
            self.assertDenied(user, "SELECT CAST(COUNT(*) AS NVARCHAR(20)) FROM dbo.products WHERE unit_cost > 10", 230)
        for user in ("sup_maria", "adm_carlos"):
            self.assertEqual(self.assertAllowed(user, "SELECT CAST(unit_cost AS NVARCHAR(20)) FROM dbo.products WHERE id = 1"), "12.50")

    def test_sensitive_columns_are_masked_without_unmask(self):
        # Without UNMASK the value is masked (an expression over a masked column is masked entirely).
        self.assertEqual(self.assertAllowed("op_joao", "SELECT cnpj FROM dbo.suppliers WHERE id = 1"), "**.***.***/****-**")
        self.assertEqual(self.assertAllowed("aud_lucia", "SELECT contact_email FROM dbo.suppliers WHERE id = 1"), "cXXX@XXXX.com")
        self.assertEqual(self.assertAllowed("op_joao", "SELECT cnpj + N'|' + contact_email FROM dbo.suppliers WHERE id = 1"), "xxxx")
        clear = self.assertAllowed("sup_maria", "SELECT cnpj + N'|' + contact_email FROM dbo.suppliers WHERE id = 1")
        self.assertEqual(clear, "11.222.333/0001-81|contato@alfa.exemplo.test")

    # ---- stock changes ---------------------------------------------------------------------------------------

    def test_operator_moves_but_does_not_adjust(self):
        self.assertAllowed("op_joao", "UPDATE dbo.stock_balances SET location = location WHERE id = 1; SELECT N'1'")
        self.assertDenied("op_joao", "UPDATE dbo.stock_balances SET quantity = 0 WHERE id = 1", 230)
        self.assertAllowed("sup_maria", "UPDATE dbo.stock_balances SET quantity = quantity WHERE id = 1; SELECT N'1'")

    def test_admin_and_auditor_do_not_operate_stock(self):
        for user in ("adm_carlos", "aud_lucia"):
            self.assertDenied(user, "UPDATE dbo.stock_balances SET location = location WHERE id = 1", 229, 230)
            self.assertDenied(user, "INSERT INTO dbo.stock_balances (warehouse_id, product_id, location, quantity) VALUES (1, 1, 'X', 1)", 229)

    # ---- audit -----------------------------------------------------------------------------------------------

    def test_audit_is_read_by_supervisor_admin_auditor_only(self):
        for user in ("sup_maria", "adm_carlos", "aud_lucia"):
            self.assertAllowed(user, "SELECT CAST(COUNT(*) AS NVARCHAR(20)) FROM dbo.audit_log")
        for user in ("op_joao", "svc_ia"):
            self.assertDenied(user, "SELECT CAST(COUNT(*) AS NVARCHAR(20)) FROM dbo.audit_log", 229)

    def test_nobody_changes_or_erases_the_audit(self):
        for user in ("op_joao", "sup_maria", "adm_carlos", "aud_lucia", "svc_ia"):
            self.assertDenied(user, "UPDATE dbo.audit_log SET action = 'x'", 229)
            self.assertDenied(user, "DELETE FROM dbo.audit_log", 229)
        self.assertAllowed("op_joao", "INSERT INTO dbo.audit_log (action, target) VALUES ('stock.move', 'A-01-01'); SELECT USER_NAME()")
        self.assertEqual(self.sql("SELECT TOP 1 actor FROM dbo.audit_log ORDER BY id DESC").stdout.strip(), "op_joao")

    # ---- the AI --------------------------------------------------------------------------------------------

    def test_ai_reads_only_the_views_without_sensitive_columns(self):
        self.assertEqual(self.assertAllowed("svc_ia", "SELECT sku FROM dbo.v_ai_products WHERE id = 1"), "ABC-123")
        self.assertAllowed("svc_ia", "SELECT name FROM dbo.v_ai_partners")
        for table in ("dbo.products", "dbo.suppliers", "dbo.stock_balances", "dbo.warehouses"):
            self.assertDenied("svc_ia", f"SELECT CAST(COUNT(*) AS NVARCHAR(20)) FROM {table}", 229)
        self.assertDenied("svc_ia", "SELECT CAST(unit_cost AS NVARCHAR(20)) FROM dbo.v_ai_products", 207)

    def test_ai_cannot_write(self):
        self.assertDenied("svc_ia", "UPDATE dbo.stock_balances SET quantity = 0", 229)
        self.assertDenied("svc_ia", "INSERT INTO dbo.products (id, sku, name) VALUES (9, 'XYZ-999', N'x')", 229)
        self.assertDenied("svc_ia", "UPDATE dbo.v_ai_stock SET quantity = 0", 229)

    # ---- privilege escalation -------------------------------------------------------------------------------

    def test_only_the_admin_changes_role_members(self):
        for user in ("op_joao", "sup_maria", "aud_lucia", "svc_ia"):
            self.assertDenied(user, "ALTER ROLE role_supervisor ADD MEMBER op_joao", 15151, 15247)
        self.assertAllowed("adm_carlos", "ALTER ROLE role_auditor ADD MEMBER op_joao; ALTER ROLE role_auditor DROP MEMBER op_joao; SELECT N'1'")

    def test_nobody_grants_itself_more(self):
        for user in ("op_joao", "svc_ia", "aud_lucia"):
            self.assertDenied(user, "GRANT UNMASK TO role_ai", 1088, 4613, 15151, 15247)
            self.assertDenied(user, "REVOKE SELECT ON dbo.products (unit_cost) FROM role_operator", 1088, 4613, 15151, 15247)

    # ---- row-level security ------------------------------------------------------------------------------

    def test_rows_of_another_warehouse_are_invisible_and_blocked(self):
        self.assertEqual(self.assertAllowed("op_joao", "SELECT CAST(COUNT(*) AS NVARCHAR(20)) FROM dbo.stock_balances"), "2")
        self.assertEqual(self.probe("op_joao", "SELECT CAST(COUNT(*) AS NVARCHAR(20)) FROM dbo.stock_balances", warehouse=2), "OK 1")
        self.assertEqual(self.assertAllowed("svc_ia", "SELECT CAST(SUM(quantity) AS NVARCHAR(20)) FROM dbo.v_ai_stock"), "160")
        self.assertDenied("op_joao", "INSERT INTO dbo.stock_balances (warehouse_id, product_id, location, quantity) VALUES (2, 1, 'A-02-01', 5)", 33504)


if __name__ == "__main__":
    unittest.main()
