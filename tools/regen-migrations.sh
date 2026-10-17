#!/usr/bin/env bash
# Narzędzie deweloperskie: odtworzenie migracji początkowych (TYLKO przed pierwszym wydaniem produkcyjnym).
# Po wydaniu 1.0 zmiany schematu wprowadza się wyłącznie nowymi migracjami: dotnet ef migrations add <Nazwa> ...
set -euo pipefail
cd "$(dirname "$0")/.."
for e in Sqlite PostgreSql; do
  proj=src/PelnaKsiegowosc.Migrations.$e
  rm -rf "$proj/Migrations"
  dotnet ef migrations add Initial --project "$proj" --startup-project "$proj" -o Migrations
  dotnet ef migrations add ImmutabilityGuards --project "$proj" --startup-project "$proj" -o Migrations
  f=$(ls "$proj"/Migrations/*_ImmutabilityGuards.cs)
  python3 - "$f" "$e" <<'PY'
import sys,re
f,e=sys.argv[1],sys.argv[2]; s=open(f).read()
up=f"foreach (var sql in PelnaKsiegowosc.Data.ImmutabilityTriggers.{e}) migrationBuilder.Sql(sql);"
down=f"foreach (var sql in PelnaKsiegowosc.Data.ImmutabilityTriggers.{e}Drop) migrationBuilder.Sql(sql);"
s=re.sub(r'(protected override void Up\(MigrationBuilder migrationBuilder\)\s*\{)\s*\}', r'\1\n            '+up+'\n        }', s)
s=re.sub(r'(protected override void Down\(MigrationBuilder migrationBuilder\)\s*\{)\s*\}', r'\1\n            '+down+'\n        }', s)
open(f,'w').write(s)
PY
done
