SET NOCOUNT ON;
PRINT '-- Signature Studio style presets export';
PRINT 'BEGIN TRANSACTION;';
SELECT
  'INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, ThumbnailPath, IsActive, SortOrder) VALUES (' +
  'N''' + REPLACE(Name,'''','''''') + ''', ' +
  'N''' + REPLACE(ISNULL(Description,''),'''','''''') + ''', ' +
  'N''' + REPLACE(PromptTemplate,'''','''''') + ''', ' +
  'N''Signature Studio'', ' +
  'N''' + REPLACE(ISNULL(IconEmoji,''),'''','''''') + ''', ' +
  ISNULL('N'''+AccentColor+'''','NULL') + ', ' +
  ISNULL('N'''+ThumbnailPath+'''','NULL') + ', ' +
  CAST(CAST(IsActive AS INT) AS NVARCHAR) + ', ' +
  CAST(SortOrder AS NVARCHAR) + ');'
FROM StylePresets
WHERE Category = N'Signature Studio'
ORDER BY SortOrder;
PRINT 'COMMIT;';
