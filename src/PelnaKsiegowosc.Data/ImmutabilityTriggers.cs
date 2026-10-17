namespace PelnaKsiegowosc.Data;

/// <summary>
/// Wyzwalacze bazodanowe — druga linia ochrony (obok logiki aplikacji) przed modyfikacją lub usunięciem
/// zaksięgowanych zapisów i wpisów dziennika audytowego. Jedyną dozwoloną zmianą zaksięgowanego zapisu jest
/// ustawienie odwołania do zapisu stornującego (<c>ReversedByEntryId</c>).
/// UWAGA: migracja <c>ImmutabilityGuards</c> wykonuje te polecenia — nie modyfikować istniejących tablic;
/// zmiany wprowadzać nową migracją z nową wersją wyzwalaczy.
/// </summary>
public static class ImmutabilityTriggers
{
    public static readonly string[] Sqlite =
    {
        """
        CREATE TRIGGER trg_je_posted_no_update BEFORE UPDATE ON JournalEntries
        WHEN OLD.Status = 'Zaksiegowany' AND (
            NEW.Status IS NOT OLD.Status OR NEW.SequenceNumber IS NOT OLD.SequenceNumber OR NEW.JournalNumber IS NOT OLD.JournalNumber OR
            NEW.OperationDate IS NOT OLD.OperationDate OR NEW.DocumentDate IS NOT OLD.DocumentDate OR NEW.PostingDate IS NOT OLD.PostingDate OR
            NEW.Description IS NOT OLD.Description OR NEW.DocumentNumber IS NOT OLD.DocumentNumber OR NEW.DocumentType IS NOT OLD.DocumentType OR
            NEW.Hash IS NOT OLD.Hash OR NEW.PreviousHash IS NOT OLD.PreviousHash OR NEW.PeriodId IS NOT OLD.PeriodId OR
            NEW.FiscalYearId IS NOT OLD.FiscalYearId OR NEW.PostedByUserId IS NOT OLD.PostedByUserId OR NEW.Currency IS NOT OLD.Currency OR
            NEW.ExchangeRate IS NOT OLD.ExchangeRate OR NEW.Kind IS NOT OLD.Kind OR NEW.ReversesEntryId IS NOT OLD.ReversesEntryId)
        BEGIN SELECT RAISE(ABORT, 'Zapis zaksiegowany nie moze byc zmieniony (wymagane storno).'); END;
        """,
        """
        CREATE TRIGGER trg_je_posted_no_delete BEFORE DELETE ON JournalEntries
        WHEN OLD.Status = 'Zaksiegowany'
        BEGIN SELECT RAISE(ABORT, 'Zapis zaksiegowany nie moze byc usuniety.'); END;
        """,
        """
        CREATE TRIGGER trg_jl_posted_no_update BEFORE UPDATE ON JournalLines
        WHEN (SELECT Status FROM JournalEntries WHERE Id = OLD.EntryId) = 'Zaksiegowany'
        BEGIN SELECT RAISE(ABORT, 'Pozycja zaksiegowanego zapisu nie moze byc zmieniona.'); END;
        """,
        """
        CREATE TRIGGER trg_jl_posted_no_delete BEFORE DELETE ON JournalLines
        WHEN (SELECT Status FROM JournalEntries WHERE Id = OLD.EntryId) = 'Zaksiegowany'
        BEGIN SELECT RAISE(ABORT, 'Pozycja zaksiegowanego zapisu nie moze byc usunieta.'); END;
        """,
        """
        CREATE TRIGGER trg_audit_no_update BEFORE UPDATE ON AuditEvents
        BEGIN SELECT RAISE(ABORT, 'Dziennik audytowy jest tylko do dopisywania.'); END;
        """,
        """
        CREATE TRIGGER trg_audit_no_delete BEFORE DELETE ON AuditEvents
        BEGIN SELECT RAISE(ABORT, 'Dziennik audytowy jest tylko do dopisywania.'); END;
        """
    };

    public static readonly string[] SqliteDrop =
    {
        "DROP TRIGGER IF EXISTS trg_je_posted_no_update;", "DROP TRIGGER IF EXISTS trg_je_posted_no_delete;",
        "DROP TRIGGER IF EXISTS trg_jl_posted_no_update;", "DROP TRIGGER IF EXISTS trg_jl_posted_no_delete;",
        "DROP TRIGGER IF EXISTS trg_audit_no_update;", "DROP TRIGGER IF EXISTS trg_audit_no_delete;"
    };

    public static readonly string[] PostgreSql =
    {
        """
        CREATE OR REPLACE FUNCTION psa_je_guard() RETURNS trigger AS $$
        BEGIN
          IF TG_OP = 'DELETE' THEN
            IF OLD."Status" = 'Zaksiegowany' THEN RAISE EXCEPTION 'Zapis zaksięgowany nie może być usunięty.'; END IF;
            RETURN OLD;
          END IF;
          IF OLD."Status" = 'Zaksiegowany' AND (
              NEW."Status" IS DISTINCT FROM OLD."Status" OR NEW."SequenceNumber" IS DISTINCT FROM OLD."SequenceNumber" OR
              NEW."JournalNumber" IS DISTINCT FROM OLD."JournalNumber" OR NEW."OperationDate" IS DISTINCT FROM OLD."OperationDate" OR
              NEW."DocumentDate" IS DISTINCT FROM OLD."DocumentDate" OR NEW."PostingDate" IS DISTINCT FROM OLD."PostingDate" OR
              NEW."Description" IS DISTINCT FROM OLD."Description" OR NEW."DocumentNumber" IS DISTINCT FROM OLD."DocumentNumber" OR
              NEW."DocumentType" IS DISTINCT FROM OLD."DocumentType" OR NEW."Hash" IS DISTINCT FROM OLD."Hash" OR
              NEW."PreviousHash" IS DISTINCT FROM OLD."PreviousHash" OR NEW."PeriodId" IS DISTINCT FROM OLD."PeriodId" OR
              NEW."FiscalYearId" IS DISTINCT FROM OLD."FiscalYearId" OR NEW."PostedByUserId" IS DISTINCT FROM OLD."PostedByUserId" OR
              NEW."Currency" IS DISTINCT FROM OLD."Currency" OR NEW."ExchangeRate" IS DISTINCT FROM OLD."ExchangeRate" OR
              NEW."Kind" IS DISTINCT FROM OLD."Kind" OR NEW."ReversesEntryId" IS DISTINCT FROM OLD."ReversesEntryId") THEN
            RAISE EXCEPTION 'Zapis zaksięgowany nie może być zmieniony (wymagane storno).';
          END IF;
          RETURN NEW;
        END; $$ LANGUAGE plpgsql;
        """,
        """
        CREATE TRIGGER trg_je_guard BEFORE UPDATE OR DELETE ON "JournalEntries" FOR EACH ROW EXECUTE FUNCTION psa_je_guard();
        """,
        """
        CREATE OR REPLACE FUNCTION psa_jl_guard() RETURNS trigger AS $$
        DECLARE st text;
        BEGIN
          SELECT "Status" INTO st FROM "JournalEntries" WHERE "Id" = OLD."EntryId";
          IF st = 'Zaksiegowany' THEN RAISE EXCEPTION 'Pozycja zaksięgowanego zapisu nie może być zmieniona ani usunięta.'; END IF;
          IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
          RETURN NEW;
        END; $$ LANGUAGE plpgsql;
        """,
        """
        CREATE TRIGGER trg_jl_guard BEFORE UPDATE OR DELETE ON "JournalLines" FOR EACH ROW EXECUTE FUNCTION psa_jl_guard();
        """,
        """
        CREATE OR REPLACE FUNCTION psa_audit_guard() RETURNS trigger AS $$
        BEGIN RAISE EXCEPTION 'Dziennik audytowy jest tylko do dopisywania.'; END; $$ LANGUAGE plpgsql;
        """,
        """
        CREATE TRIGGER trg_audit_guard BEFORE UPDATE OR DELETE ON "AuditEvents" FOR EACH ROW EXECUTE FUNCTION psa_audit_guard();
        """
    };

    public static readonly string[] PostgreSqlDrop =
    {
        "DROP TRIGGER IF EXISTS trg_je_guard ON \"JournalEntries\";", "DROP FUNCTION IF EXISTS psa_je_guard();",
        "DROP TRIGGER IF EXISTS trg_jl_guard ON \"JournalLines\";", "DROP FUNCTION IF EXISTS psa_jl_guard();",
        "DROP TRIGGER IF EXISTS trg_audit_guard ON \"AuditEvents\";", "DROP FUNCTION IF EXISTS psa_audit_guard();"
    };
}
