SET NOCOUNT ON;
SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(30)) AS ver, CAST(SERVERPROPERTY('Edition') AS nvarchar(60)) AS ed, CAST(SERVERPROPERTY('IsFullTextInstalled') AS int) AS fts;
SELECT name, lcid FROM sys.fulltext_languages WHERE lcid IN (0,1041,2052,1033);
IF DB_ID('CapProbe') IS NOT NULL DROP DATABASE CapProbe;
CREATE DATABASE CapProbe;
GO
USE CapProbe;
ALTER DATABASE SCOPED CONFIGURATION SET PREVIEW_FEATURES = ON;
CREATE TABLE dbo.G (Id uniqueidentifier NOT NULL PRIMARY KEY CLUSTERED DEFAULT NEWSEQUENTIALID(), K int NOT NULL, E vector(3) NOT NULL);
INSERT dbo.G (K, E) SELECT value, CAST(JSON_ARRAY(CAST(value AS float), CAST(value%7 AS float), 1.0) AS vector(3)) FROM GENERATE_SERIES(1, 200);
GO
BEGIN TRY
  CREATE VECTOR INDEX vx_g ON dbo.G(E) WITH (METRIC='cosine', TYPE='diskann');
  PRINT 'guid-pk vector index: created';
END TRY BEGIN CATCH PRINT 'guid-pk vector index: ' + ERROR_MESSAGE(); END CATCH
GO
SELECT i.name, JSON_VALUE(v.build_parameters,'$.Version') AS ver FROM sys.vector_indexes v JOIN sys.indexes i ON v.object_id=i.object_id AND v.index_id=i.index_id;
BEGIN TRY
  INSERT dbo.G (K, E) VALUES (999, '[1,2,3]');
  PRINT 'DML after index: ok';
END TRY BEGIN CATCH PRINT 'DML after index: ' + ERROR_MESSAGE(); END CATCH
GO
DECLARE @q vector(3) = '[5,5,1]';
BEGIN TRY
  SELECT TOP (3) WITH APPROXIMATE g.K, s.distance FROM VECTOR_SEARCH(TABLE = dbo.G AS g, COLUMN = E, SIMILAR_TO = @q, METRIC = 'cosine') AS s WHERE g.K > 100 ORDER BY s.distance;
  PRINT 'approx search with filter: ok';
END TRY BEGIN CATCH PRINT 'approx search: ' + ERROR_MESSAGE(); END CATCH
GO
