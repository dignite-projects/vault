SET NOCOUNT ON;
USE CapProbe;
DROP TABLE IF EXISTS dbo.I; CREATE TABLE dbo.I (Id int NOT NULL PRIMARY KEY CLUSTERED, K int NOT NULL, E vector(3) NOT NULL);
INSERT dbo.I (Id, K, E) SELECT value, value, CAST(JSON_ARRAY(CAST(value AS float), CAST(value%7 AS float), 1.0) AS vector(3)) FROM GENERATE_SERIES(1, 200);
GO
BEGIN TRY CREATE VECTOR INDEX vx_i ON dbo.I(E) WITH (METRIC='cosine', TYPE='diskann'); PRINT 'int-pk vector index: created'; END TRY BEGIN CATCH PRINT 'int-pk: ' + ERROR_MESSAGE(); END CATCH
GO
SELECT i.name, v.build_parameters FROM sys.vector_indexes v JOIN sys.indexes i ON v.object_id=i.object_id AND v.index_id=i.index_id;
BEGIN TRY INSERT dbo.I (Id, K, E) VALUES (1002, 1002, '[1,2,3]'); PRINT 'DML after index: ok'; END TRY BEGIN CATCH PRINT 'DML after index: ' + ERROR_MESSAGE(); END CATCH
GO
DECLARE @q vector(3) = '[5,5,1]';
BEGIN TRY
  SELECT TOP (3) i.K, s.distance FROM VECTOR_SEARCH(TABLE = dbo.I AS i, COLUMN = E, SIMILAR_TO = @q, METRIC = 'cosine', TOP_N = 3) AS s WHERE i.K > 150 ORDER BY s.distance;
  PRINT 'TOP_N search + filter: ok';
END TRY BEGIN CATCH PRINT 'TOP_N search: ' + ERROR_MESSAGE(); END CATCH
GO
ALTER DATABASE SCOPED CONFIGURATION SET ALLOW_STALE_VECTOR_INDEX = ON;
GO
BEGIN TRY INSERT dbo.I (Id, K, E) VALUES (1003, 1003, '[1,2,3]'); PRINT 'DML with ALLOW_STALE: ok'; END TRY BEGIN CATCH PRINT 'DML with ALLOW_STALE: ' + ERROR_MESSAGE(); END CATCH
GO
