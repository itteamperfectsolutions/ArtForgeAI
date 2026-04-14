# Style Preset Export Guide

How to export a set of `StylePresets` rows from the local dev DB into a portable SQL + thumbnail bundle for deployment to another server or project.

Use when: porting a category (e.g. `63 Group`, `Trendy Photo Collage`, `FUN`) to a different environment where the incrementalStyles auto-seed path is not available (different project, or prod DB that should not run startup seeding).

For **adding** new presets to this project, use `StylePreset-SeedingGuide.md` instead — this document is only for exporting existing rows.

---

## What you produce

Two files per export, placed in `deploy/`:

1. `seed-<name>.sql` — UTF-16 LE T-SQL file wrapping N `INSERT` statements in a single transaction. Contains `Name`, `Description`, `PromptTemplate`, `Category`, `IconEmoji`, `AccentColor`, `ThumbnailPath`, `IsActive`, `SortOrder`.
2. `<name>-thumbs.zip` — flat archive of every `ThumbnailPath` PNG referenced by the INSERTs, copied from `ArtForgeAI/wwwroot/presets/`.

The consumer extracts the zip into the target app's `wwwroot/presets/` and runs the SQL.

---

## Prerequisites

- SQL Server local dev DB reachable via `sqlcmd` (connection string in `ArtForgeAI/appsettings.json` under `DefaultConnection`).
- PowerShell available for `Compress-Archive` (Windows).
- `iconv` for optional UTF-8 verification (Git Bash provides it).

---

## Step 1 — Write the export SQL

Create `scripts/export-<name>.sql`. Parameterise the `WHERE` clause for the rows you want. Template:

```sql
SET NOCOUNT ON;
PRINT '-- <Category> style presets export';
PRINT 'BEGIN TRANSACTION;';
SELECT
  'INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, ThumbnailPath, IsActive, SortOrder) VALUES (' +
  'N''' + REPLACE(Name,'''','''''') + ''', ' +
  'N''' + REPLACE(ISNULL(Description,''),'''','''''') + ''', ' +
  'N''' + REPLACE(PromptTemplate,'''','''''') + ''', ' +
  'N''<Category>'', ' +
  'N''' + REPLACE(ISNULL(IconEmoji,''),'''','''''') + ''', ' +
  ISNULL('N'''+AccentColor+'''','NULL') + ', ' +
  ISNULL('N'''+ThumbnailPath+'''','NULL') + ', ' +
  CAST(CAST(IsActive AS INT) AS NVARCHAR) + ', ' +
  CAST(SortOrder AS NVARCHAR) + ');'
FROM StylePresets
WHERE Category = N'<Category>'        -- adjust filter as needed
ORDER BY SortOrder;
PRINT 'COMMIT;';
```

Notes:
- Single-quote escaping is done via `REPLACE(...,'''','''''')` — emits doubled quotes inside the literal.
- `N''` prefix on every string ensures the generated INSERT preserves Unicode (emoji, em-dashes, Devanagari, etc).
- The hardcoded `Category` literal in the SELECT is safe — if you need to rename the category on the target, edit the generated file, or parameterise this line.
- Do **not** emit `DELETE FROM StylePresets WHERE Category = ...` unless the target should be wiped. Additive inserts are the default.
- To restrict by SortOrder (e.g. "only the new ones since Bollywood Retro"), add `AND SortOrder > 60`.

## Step 2 — Generate the SQL file

Run from the repo root:

```bash
sqlcmd -S "<server>" -d ArtForgeAI -E \
  -i "C:\Users\vivij\source\repos\ArtForgeAI\scripts\export-<name>.sql" \
  -o "C:\Users\vivij\source\repos\ArtForgeAI\deploy\seed-<name>.sql" \
  -y 0 -u
```

Flags explained (all required — sqlcmd is picky about which combinations it accepts):

| Flag | Purpose |
|------|---------|
| `-E` | Windows auth (trusted connection). Mutually exclusive with `-U/-P`. |
| `-i` | Input script path. **Must be absolute Windows path** — sqlcmd doesn't handle Git Bash CWD correctly. |
| `-o` | Output path. Same absolute-path requirement. |
| `-y 0` | Unlimited column width (default 256 truncates prompts). Mutually exclusive with `-h` and `-W`. |
| `-u` | Write output as **UTF-16 LE** — preserves emojis and Unicode. Without this, non-ASCII turns into `?`. |

Common failures:

- `-E and -U/-P options are mutually exclusive` → you passed both auth modes. Use only `-E`.
- `-W and -y/-Y options are mutually exclusive` → drop `-W`; `-y 0` alone is enough.
- `-h and -y 0 options are mutually exclusive` → drop `-h -1`; `PRINT` lines plus the generated rows come through cleanly without header suppression.
- `Error occurred while opening or operating on file` → you're in a subdirectory and passed a relative path. Use an absolute Windows path for `-i` and `-o`.

## Step 3 — Verify the SQL file

```bash
iconv -f UTF-16LE -t UTF-8 deploy/seed-<name>.sql | grep -c "^INSERT"
iconv -f UTF-16LE -t UTF-8 deploy/seed-<name>.sql | sed -n '3p' | head -c 400
```

Check:
- INSERT count matches the row count you expected.
- Emojis and em-dashes render correctly in the sample line (not `???` or `â€"`).
- Line 1 is the `-- ... export` comment, line 2 is `BEGIN TRANSACTION;`, last line is `COMMIT;`.

## Step 4 — Copy thumbnail files

Dump the list of `ThumbnailPath` values, then copy each one from `ArtForgeAI/wwwroot/` into `deploy/<name>-presets/`:

```bash
mkdir -p deploy/<name>-presets

sqlcmd -S "<server>" -d ArtForgeAI -E \
  -Q "SET NOCOUNT ON; SELECT ThumbnailPath FROM StylePresets WHERE <same filter> AND ThumbnailPath IS NOT NULL" \
  -h -1 -W > deploy/<name>-thumb-paths.txt

cd ArtForgeAI/wwwroot
copied=0; missing=0
while IFS= read -r p; do
  p=$(echo "$p" | tr -d '\r')
  [ -z "$p" ] && continue
  if [ -f "$p" ]; then
    cp "$p" "../../deploy/<name>-presets/"
    copied=$((copied+1))
  else
    echo "MISSING: $p"
    missing=$((missing+1))
  fi
done < ../../deploy/<name>-thumb-paths.txt
echo "copied=$copied missing=$missing"
cd ../..
```

If `missing > 0`, the referenced thumbnail is not on disk — either generate it via the Admin UI first, or accept that those rows will deploy with a broken image reference (the INSERT still carries the stale path).

Rows where `ThumbnailPath IS NULL` are skipped by the copy step but their INSERT still emits `ThumbnailPath = NULL` — this is intentional, not a bug.

## Step 5 — Zip the thumbnails

```bash
powershell -Command "Compress-Archive -Path 'deploy\<name>-presets\*' -DestinationPath 'deploy\<name>-thumbs.zip' -Force"
```

`-Force` overwrites any previous bundle with the same name.

## Step 6 — Deploy on the target server

1. Extract `<name>-thumbs.zip` into the target app's `wwwroot/presets/` folder (flat, no subdirectories).
2. Run the SQL file. Use one of:
   - SSMS: open the file — it auto-detects UTF-16 — and execute against the target DB.
   - `sqlcmd -S <target> -d <db> -i seed-<name>.sql -f 65001` — `-f 65001` forces UTF-8 console codepage so PRINT output is readable; the file itself is UTF-16 and sqlcmd handles it.
3. **Before running**, verify:
   - Target `StylePresets` table has the same columns: `Name`, `Description`, `PromptTemplate`, `Category`, `IconEmoji`, `AccentColor`, `ThumbnailPath`, `IsActive`, `SortOrder`. If any differ, edit the generated INSERT column list.
   - Target `SortOrder` range has no collisions with existing rows in the same category — query `SELECT SortOrder, Name FROM StylePresets WHERE Category = '<cat>' ORDER BY SortOrder` on the target first. Collisions don't error but cause unstable display order.

## Tips

- **Renumbering to avoid collisions**: if source and target have overlapping SortOrders, open the generated SQL in a text editor and bulk-adjust the trailing `, <N>);` values — they're the last integer before the closing paren. For larger shifts, regenerate with `SELECT ... CAST(SortOrder + <offset> AS NVARCHAR) ...` in the export script.
- **Re-runnable deploys**: wrap the INSERTs with `IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = N'...' AND Category = N'...')` guards if the target may already have some rows. The default bundle is *not* idempotent.
- **Incremental exports** (only rows changed since date X): `StylePresets` has no `UpdatedAt` column, so this is impossible from the DB alone. Use git history on `ArtForgeAI/Program.cs` to find which entries were added/modified, then filter by name or SortOrder range in the export.
- **Preserving a rename**: the `Category` string is hardcoded into the generated SQL. If you rename the category on the source later, re-run the export — the new category string will flow through automatically.

## Known pitfalls

- sqlcmd without `-u` silently corrupts emojis to `?` — always pass `-u`.
- sqlcmd without `-y 0` truncates `PromptTemplate` at 256 characters — always pass `-y 0`.
- Git Bash relative paths fail with `-i`/`-o`; use absolute Windows paths.
- `PowerShell Compress-Archive` with a trailing `*` glob includes only the files; without `*` it nests the folder. Use `deploy\<name>-presets\*`.
- The output file is UTF-16 LE with BOM. Editors that can't detect it (e.g. old Notepad++) will show garbage. SSMS and VS Code handle it correctly.
