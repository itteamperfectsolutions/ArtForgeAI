SET NOCOUNT ON;
PRINT '-- 63 Group style presets export';
PRINT 'BEGIN TRANSACTION;';
PRINT 'DELETE FROM StylePresets WHERE Category = N''63 Group'';';
SELECT
  'INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, ThumbnailPath, IsActive, SortOrder) VALUES (' +
  'N''' + REPLACE(Name,'''','''''') + ''', ' +
  'N''' + REPLACE(ISNULL(Description,''),'''','''''') + ''', ' +
  'N''' + REPLACE(PromptTemplate,'''','''''') + ''', ' +
  'N''63 Group'', ' +
  'N''' + REPLACE(ISNULL(IconEmoji,''),'''','''''') + ''', ' +
  ISNULL('N'''+AccentColor+'''','NULL') + ', ' +
  ISNULL('N'''+ThumbnailPath+'''','NULL') + ', ' +
  CAST(CAST(IsActive AS INT) AS NVARCHAR) + ', ' +
  CAST(SortOrder AS NVARCHAR) + ');'
FROM StylePresets
WHERE Category = N'63 Group'
ORDER BY SortOrder;
PRINT 'COMMIT;';
