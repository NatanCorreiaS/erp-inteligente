-- Nexus: the role matrix of references/SEGURANCA_DADOS.md (section 3) on a server DBMS with native GRANT/DENY.
--
-- The app runs on SQLite (desktop, no server; see section 1), where the same rules are role_permissions + triggers + the
-- AI connection's authorizer. This script shows the matrix in SQL Server 2022, whose DENY always wins over a GRANT, plus
-- the data protection the scenario calls for: column-level DENY, dynamic data masking and row-level security by
-- warehouse. Subset of the schema; fictitious data only. Run by db/sqlserver/test_sqlserver.py in a throwaway container.

SET NOCOUNT ON;
GO
CREATE DATABASE nexus_roles;
GO
USE nexus_roles;
GO

-- ---------------------------------------------------------------------------
-- Tables (subset of db/schema.sql)
-- ---------------------------------------------------------------------------
CREATE TABLE dbo.warehouses (
    id   INT PRIMARY KEY,
    code VARCHAR(10) NOT NULL UNIQUE
);

CREATE TABLE dbo.products (
    id        INT PRIMARY KEY,
    sku       VARCHAR(7) NOT NULL UNIQUE,
    name      NVARCHAR(100) NOT NULL,
    unit_cost DECIMAL(10, 2) NULL                           -- sensitive: cost
);

CREATE TABLE dbo.stock_balances (
    id           INT IDENTITY PRIMARY KEY,
    warehouse_id INT NOT NULL REFERENCES dbo.warehouses (id),
    product_id   INT NOT NULL REFERENCES dbo.products (id),
    location     VARCHAR(10) NOT NULL,
    quantity     INT NOT NULL CHECK (quantity >= 0)
);

CREATE TABLE dbo.suppliers (
    id            INT PRIMARY KEY,
    name          NVARCHAR(100) NOT NULL,
    cnpj          VARCHAR(18) MASKED WITH (FUNCTION = 'partial(0, "**.***.***/****-**", 0)') NULL,   -- sensitive
    contact_email VARCHAR(100) MASKED WITH (FUNCTION = 'email()') NULL                         -- sensitive
);

CREATE TABLE dbo.audit_log (
    id          INT IDENTITY PRIMARY KEY,
    occurred_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    actor       SYSNAME NOT NULL DEFAULT USER_NAME(),
    action      VARCHAR(40) NOT NULL,
    target      NVARCHAR(200) NULL
);
GO

-- The AI reads only views without sensitive columns (the v_ai_* views of db/security.sql).
CREATE VIEW dbo.v_ai_products AS SELECT id, sku, name FROM dbo.products;
GO
CREATE VIEW dbo.v_ai_stock AS
SELECT b.warehouse_id, p.sku, b.location, b.quantity
FROM dbo.stock_balances b JOIN dbo.products p ON p.id = b.product_id;
GO
CREATE VIEW dbo.v_ai_partners AS SELECT id, name FROM dbo.suppliers;
GO

-- ---------------------------------------------------------------------------
-- Fictitious data (same values as db/seed.sql where they exist)
-- ---------------------------------------------------------------------------
INSERT INTO dbo.warehouses VALUES (1, 'CD-01'), (2, 'CD-02');
INSERT INTO dbo.products VALUES (1, 'ABC-123', N'Caixa organizadora fictícia', 12.50), (2, 'LIM-200', N'Detergente fictício', 3.10);
INSERT INTO dbo.stock_balances (warehouse_id, product_id, location, quantity) VALUES
    (1, 1, 'A-01-01', 40), (1, 2, 'B-01-01', 120), (2, 1, 'A-01-01', 999);
INSERT INTO dbo.suppliers VALUES (1, N'Fornecedora Fictícia Alfa Ltda', '11.222.333/0001-81', 'contato@alfa.exemplo.test');
INSERT INTO dbo.audit_log (action, target) VALUES ('seed', 'nexus_roles');
GO

-- ---------------------------------------------------------------------------
-- Roles and the matrix. "nexus_staff" is what every human role has in common (read stock); the specific roles add to it.
-- ---------------------------------------------------------------------------
CREATE ROLE nexus_staff;
CREATE ROLE role_operator;
CREATE ROLE role_supervisor;
CREATE ROLE role_admin;
CREATE ROLE role_auditor;
CREATE ROLE role_ai;

ALTER ROLE nexus_staff ADD MEMBER role_operator;
ALTER ROLE nexus_staff ADD MEMBER role_supervisor;
ALTER ROLE nexus_staff ADD MEMBER role_admin;
ALTER ROLE nexus_staff ADD MEMBER role_auditor;

-- stock.read for every human role: whole tables, including the sensitive columns...
GRANT SELECT ON dbo.products TO nexus_staff;
GRANT SELECT ON dbo.stock_balances TO nexus_staff;
GRANT SELECT ON dbo.suppliers TO nexus_staff;
GRANT SELECT ON dbo.warehouses TO nexus_staff;

-- ...but sensitive.read is DENY for the operator and the auditor. Column-level DENY wins over the table-level GRANT they
-- inherit from nexus_staff: they read the product, never its cost.
DENY SELECT ON dbo.products (unit_cost) TO role_operator, role_auditor;

-- stock.receive / stock.move / stock.adjust: the operator moves stock; adjusting (UPDATE of quantity outside a plan) is
-- the supervisor's. In the app every change goes through the plan engine; here it is the table right.
GRANT INSERT ON dbo.stock_balances TO role_operator, role_supervisor;
GRANT UPDATE ON dbo.stock_balances (location) TO role_operator;
GRANT UPDATE ON dbo.stock_balances TO role_supervisor;
DENY UPDATE ON dbo.stock_balances (quantity) TO role_operator;
-- The administrator configures but does not operate stock (segregation of duties).
DENY INSERT, UPDATE, DELETE ON dbo.stock_balances TO role_admin;
-- The auditor only reads.
DENY INSERT, UPDATE, DELETE ON dbo.stock_balances TO role_auditor;

-- catalog.manage
GRANT INSERT, UPDATE ON dbo.products TO role_supervisor, role_admin;
GRANT INSERT, UPDATE ON dbo.suppliers TO role_supervisor, role_admin;

-- audit.read: supervisor, administrator and auditor. The operator and the AI never read it.
GRANT SELECT ON dbo.audit_log TO role_supervisor, role_admin, role_auditor;
DENY SELECT ON dbo.audit_log TO role_operator, role_ai;
-- Everyone (the app writes through its own login) may append; nobody may change or erase history.
GRANT INSERT ON dbo.audit_log TO nexus_staff, role_ai;
DENY UPDATE, DELETE ON dbo.audit_log TO nexus_staff, role_ai;

-- sensitive.read: unmasked values only for supervisor and administrator.
GRANT UNMASK TO role_supervisor;
GRANT UNMASK TO role_admin;

-- user.manage / role.manage: only the administrator manages members of the roles.
GRANT ALTER ON ROLE::role_operator TO role_admin;
GRANT ALTER ON ROLE::role_supervisor TO role_admin;
GRANT ALTER ON ROLE::role_auditor TO role_admin;

-- The AI: only the v_ai_* views (ownership chaining lets the views read the base tables), and an explicit DENY on every
-- base table, so a future GRANT to "public" or a mistake can never open them. No write right at all: the AI proposes a
-- plan, the engine writes.
GRANT SELECT ON dbo.v_ai_products TO role_ai;
GRANT SELECT ON dbo.v_ai_stock TO role_ai;
GRANT SELECT ON dbo.v_ai_partners TO role_ai;
DENY SELECT, INSERT, UPDATE, DELETE ON dbo.products TO role_ai;
DENY SELECT, INSERT, UPDATE, DELETE ON dbo.stock_balances TO role_ai;
DENY SELECT, INSERT, UPDATE, DELETE ON dbo.suppliers TO role_ai;
DENY SELECT, INSERT, UPDATE, DELETE ON dbo.warehouses TO role_ai;
DENY UNMASK TO role_ai;
GO

-- ---------------------------------------------------------------------------
-- Row-level security by warehouse (the $warehouse_id the app's policy engine fills in): a session sees only the rows of
-- the warehouse in SESSION_CONTEXT, which the app sets at login (read-only for the session).
-- ---------------------------------------------------------------------------
CREATE SCHEMA security;
GO
CREATE FUNCTION security.fn_same_warehouse (@warehouse_id INT)
RETURNS TABLE WITH SCHEMABINDING AS
RETURN SELECT 1 AS allowed WHERE @warehouse_id = CAST(SESSION_CONTEXT(N'warehouse_id') AS INT);
GO
CREATE SECURITY POLICY security.warehouse_filter
    ADD FILTER PREDICATE security.fn_same_warehouse(warehouse_id) ON dbo.stock_balances,
    ADD BLOCK PREDICATE security.fn_same_warehouse(warehouse_id) ON dbo.stock_balances AFTER INSERT
    WITH (STATE = ON);
GO

-- ---------------------------------------------------------------------------
-- Users of the seed (no login: they are impersonated by the tests with EXECUTE AS USER)
-- ---------------------------------------------------------------------------
CREATE USER op_joao WITHOUT LOGIN;
CREATE USER sup_maria WITHOUT LOGIN;
CREATE USER adm_carlos WITHOUT LOGIN;
CREATE USER aud_lucia WITHOUT LOGIN;
CREATE USER svc_ia WITHOUT LOGIN;
ALTER ROLE role_operator ADD MEMBER op_joao;
ALTER ROLE role_supervisor ADD MEMBER sup_maria;
ALTER ROLE role_admin ADD MEMBER adm_carlos;
ALTER ROLE role_auditor ADD MEMBER aud_lucia;
ALTER ROLE role_ai ADD MEMBER svc_ia;
GO

-- ---------------------------------------------------------------------------
-- Probe used by the tests: runs @sql as @user inside warehouse 1 and returns OK or the error number.
-- (Fixed list of users; @sql comes only from the test file, never from input.)
-- ---------------------------------------------------------------------------
CREATE PROCEDURE dbo.probe @user SYSNAME, @sql NVARCHAR(MAX), @warehouse INT = 1
AS
BEGIN
    SET NOCOUNT ON;
    IF @user NOT IN (N'op_joao', N'sup_maria', N'adm_carlos', N'aud_lucia', N'svc_ia')
        THROW 50001, 'unknown user', 1;
    DECLARE @result NVARCHAR(400);
    EXEC sp_set_session_context N'warehouse_id', @warehouse;
    EXECUTE AS USER = @user;
    BEGIN TRY
        DECLARE @out TABLE (value NVARCHAR(400));
        INSERT INTO @out EXEC sp_executesql @sql;
        SELECT @result = N'OK ' + COALESCE((SELECT STRING_AGG(value, N';') FROM @out), N'');
    END TRY
    BEGIN CATCH
        SELECT @result = N'ERR ' + CAST(ERROR_NUMBER() AS NVARCHAR(10));
    END CATCH;
    REVERT;
    SELECT @result AS result;
END;
GO
