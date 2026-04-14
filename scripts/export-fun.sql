SET NOCOUNT ON;
PRINT '-- FUN style presets export (after Bollywood Retro, SortOrder > 60)';
PRINT 'BEGIN TRANSACTION;';
SELECT
  'INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, ThumbnailPath, IsActive, SortOrder) VALUES (' +
  'N''' + REPLACE(Name,'''','''''') + ''', ' +
  'N''' + REPLACE(ISNULL(Description,''),'''','''''') + ''', ' +
  'N''' + REPLACE(PromptTemplate,'''','''''') + ''', ' +
  'N''FUN'', ' +
  'N''' + REPLACE(ISNULL(IconEmoji,''),'''','''''') + ''', ' +
  ISNULL('N'''+AccentColor+'''','NULL') + ', ' +
  ISNULL('N'''+ThumbnailPath+'''','NULL') + ', ' +
  CAST(CAST(IsActive AS INT) AS NVARCHAR) + ', ' +
  CAST(SortOrder AS NVARCHAR) + ');'
FROM StylePresets
WHERE Category = N'FUN' AND SortOrder > 60
ORDER BY SortOrder;
PRINT 'COMMIT;';
