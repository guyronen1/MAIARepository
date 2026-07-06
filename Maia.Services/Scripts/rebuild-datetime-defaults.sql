-- Rebuild DEFAULT constraints that still use GETUTCDATE() to use GETDATE() (local time).
--
-- Background: migrations created column defaults of GETUTCDATE(). After the team switched
-- to local-time semantics (DateTime.Now / GETDATE()), the migration .cs files were rewritten
-- but live databases that had already applied them kept the old DEFAULT constraint. This
-- script reconciles the live schema with the new convention without requiring a new EF
-- migration.
--
-- Safe to re-run: the cursor only picks up constraints whose definition still contains
-- "utcdate". Already-fixed constraints are skipped.

SET XACT_ABORT ON;
BEGIN TRAN;

DECLARE @table      sysname,
        @col        sysname,
        @constraint sysname,
        @sql        nvarchar(max);

DECLARE cur CURSOR LOCAL FAST_FORWARD FOR
    SELECT t.name, c.name, dc.name
    FROM   sys.default_constraints dc
    JOIN   sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
    JOIN   sys.tables  t ON t.object_id          = c.object_id
    WHERE  LOWER(dc.definition) LIKE '%utcdate%';

OPEN cur;
FETCH NEXT FROM cur INTO @table, @col, @constraint;

WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql =
        N'ALTER TABLE [' + @table + N'] DROP CONSTRAINT [' + @constraint + N'];' +
        N'ALTER TABLE [' + @table + N'] ADD CONSTRAINT [DF_' + @table + N'_' + @col + N'] ' +
        N'DEFAULT (GETDATE()) FOR [' + @col + N'];';
    PRINT @sql;
    EXEC sp_executesql @sql;

    FETCH NEXT FROM cur INTO @table, @col, @constraint;
END;

CLOSE cur;
DEALLOCATE cur;

COMMIT;

-- Verify nothing remains on UTC
SELECT
    t.name + '.' + c.name AS ColName,
    dc.definition         AS DefaultDef,
    dc.name               AS ConstraintName
FROM   sys.default_constraints dc
JOIN   sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
JOIN   sys.tables  t ON t.object_id          = c.object_id
WHERE  LOWER(dc.definition) LIKE '%getdate%' OR LOWER(dc.definition) LIKE '%utcdate%'
ORDER  BY t.name, c.name;
