-- Pełna Księgowość 1.0.0 — kontrola uprawnień SQL przed dopuszczeniem stanowiska.
-- Uruchom bezpośrednio na docelowym połączeniu indywidualnego operatora:
--   psql "host=... dbname=... user=..." -v ON_ERROR_STOP=1 -f scripts/postgres-operator-preflight.sql
-- Ta kontrola NIE zastępuje zaufanej warstwy autoryzacji; połączenie z bezpośrednimi
-- prawami INSERT/UPDATE/DELETE nadal pozwala ominąć reguły biznesowe klienta.
BEGIN READ ONLY;
DO $$
DECLARE
  bad text[];
BEGIN
  SELECT array_remove(ARRAY[
    CASE WHEN r.rolsuper THEN 'SUPERUSER' END,
    CASE WHEN r.rolcreaterole THEN 'CREATEROLE' END,
    CASE WHEN r.rolcreatedb THEN 'CREATEDB' END,
    CASE WHEN r.rolreplication THEN 'REPLICATION' END,
    CASE WHEN r.rolbypassrls THEN 'BYPASSRLS' END,
    CASE WHEN pg_has_role(r.oid, d.datdba, 'MEMBER') THEN 'DATABASE OWNER' END,
    CASE WHEN has_database_privilege(r.oid, d.oid, 'CREATE') THEN 'DATABASE CREATE' END,
    CASE WHEN EXISTS (
      SELECT 1 FROM pg_namespace n WHERE n.nspname = 'public' AND
        (pg_has_role(r.oid, n.nspowner, 'MEMBER')
         OR has_schema_privilege(r.oid, n.oid, 'CREATE'))
    ) THEN 'SCHEMA OWNER/CREATE' END,
    CASE WHEN EXISTS (
      SELECT 1 FROM pg_class c
      JOIN pg_namespace n ON n.oid=c.relnamespace
      WHERE n.nspname='public' AND c.relkind IN ('r','p','v','m','f')
        AND pg_has_role(r.oid,c.relowner,'MEMBER')
    ) THEN 'OBJECT OWNER' END,
    CASE WHEN EXISTS (
      SELECT 1 FROM pg_class c
      JOIN pg_namespace n ON n.oid=c.relnamespace
      WHERE n.nspname='public' AND c.relkind IN ('r','p')
        AND (has_table_privilege(r.oid,c.oid,'TRUNCATE')
             OR has_table_privilege(r.oid,c.oid,'TRIGGER'))
    ) THEN 'TABLE TRUNCATE/TRIGGER' END
  ], NULL) INTO bad
  FROM pg_roles r JOIN pg_database d ON d.datname=current_database()
  WHERE r.rolname=session_user;
  IF bad IS NULL OR array_length(bad,1) IS NULL THEN
    RAISE NOTICE 'Wstępna kontrola uprawnień: brak wykrytych przywilejów administracyjnych na koncie %', session_user;
  ELSE
    RAISE EXCEPTION 'NIEDOZWOLONE uprawnienia operatora %: %', session_user, array_to_string(bad,', ');
  END IF;
  -- Effective rights matter: grants inherited from other roles may override
  -- a direct REVOKE from the operator. Check the resulting privileges.
  IF to_regclass('public."AuditEvents"') IS NOT NULL AND (
    has_table_privilege(session_user, 'public."AuditEvents"', 'UPDATE') OR
    has_table_privilege(session_user, 'public."AuditEvents"', 'DELETE')
  ) THEN
    RAISE EXCEPTION 'NIEDOZWOLONE: operator może zmieniać lub usuwać wpisy AuditEvents. DBA musi odebrać efektywne prawa UPDATE/DELETE (także z ról dziedziczonych).';
  END IF;
END $$;
ROLLBACK;
-- Dodatkowo sprawdź ręcznie, kto ma prawa do DML na tabelach i brak restrykcji
-- dostępu poza aplikacją. Bezpieczne operacje księgowe wymagają serwerowej autoryzacji.
