-- Operator-run synthetic lab setup. Never invoked by a SqlObserver collector.
SET NOCOUNT ON;
IF CONVERT(nvarchar(128),SERVERPROPERTY('MachineName')) <> N'WIN-QNGOV5GDM24'
 THROW 51000,'This fixture is restricted to WIN-QNGOV5GDM24.',1;
IF DB_ID(N'SqlObserverLabSales') IS NULL CREATE DATABASE [SqlObserverLabSales];
IF DB_ID(N'SqlObserverLabWarehouse') IS NULL CREATE DATABASE [SqlObserverLabWarehouse];
ALTER DATABASE [SqlObserverLabSales] SET RECOVERY SIMPLE;
ALTER DATABASE [SqlObserverLabWarehouse] SET RECOVERY SIMPLE;
ALTER DATABASE [SqlObserverLabSales] SET QUERY_STORE = ON (OPERATION_MODE=READ_WRITE, QUERY_CAPTURE_MODE=ALL, INTERVAL_LENGTH_MINUTES=1, DATA_FLUSH_INTERVAL_SECONDS=60, MAX_STORAGE_SIZE_MB=128);
-- The warehouse deliberately exercises the collector's plan-cache fallback.
ALTER DATABASE [SqlObserverLabWarehouse] SET QUERY_STORE = OFF;
GO
USE [SqlObserverLabSales];
IF OBJECT_ID(N'dbo.Customers') IS NULL
BEGIN
 CREATE TABLE dbo.Customers(CustomerId int NOT NULL PRIMARY KEY, CustomerName nvarchar(80) NOT NULL, Region char(2) NOT NULL);
 CREATE TABLE dbo.Orders(OrderId int NOT NULL PRIMARY KEY,CustomerId int NOT NULL REFERENCES dbo.Customers(CustomerId),OrderDate date NOT NULL,Status varchar(12) NOT NULL);
 CREATE TABLE dbo.OrderLines(LineId int NOT NULL PRIMARY KEY,OrderId int NOT NULL REFERENCES dbo.Orders(OrderId),Quantity int NOT NULL,UnitPrice decimal(12,2) NOT NULL);
 CREATE TABLE dbo.LockScenario(Id int NOT NULL PRIMARY KEY,Value int NOT NULL);
 INSERT dbo.LockScenario VALUES(1,0),(2,0),(3,0),(4,0);
 WITH d(n) AS (SELECT n FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9))v(n)), nums AS (SELECT TOP(2000) ROW_NUMBER() OVER(ORDER BY (SELECT NULL)) n FROM d a CROSS JOIN d b CROSS JOIN d c CROSS JOIN d e)
 INSERT dbo.Customers SELECT n,CONCAT(N'Synthetic customer ',n),CASE WHEN n%2=0 THEN 'NE' ELSE 'SW' END FROM nums;
 WITH d(n) AS (SELECT n FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9))v(n)), nums AS (SELECT TOP(40000) ROW_NUMBER() OVER(ORDER BY (SELECT NULL)) n FROM d a CROSS JOIN d b CROSS JOIN d c CROSS JOIN d e CROSS JOIN d f)
 INSERT dbo.Orders SELECT n,1+(n%2000),DATEADD(day,-CONVERT(int,n%365),CONVERT(date,'20260905',112)),CASE WHEN n%7=0 THEN 'Pending' ELSE 'Shipped' END FROM nums;
 INSERT dbo.OrderLines SELECT OrderId*2-1,OrderId,1+(OrderId%5),10+(OrderId%300) FROM dbo.Orders;
 INSERT dbo.OrderLines SELECT OrderId*2,OrderId,1+(OrderId%3),5+(OrderId%120) FROM dbo.Orders;
 CREATE INDEX IX_Orders_Customer ON dbo.Orders(CustomerId,OrderDate) INCLUDE(Status);
 CREATE INDEX IX_OrderLines_Order ON dbo.OrderLines(OrderId) INCLUDE(Quantity,UnitPrice);
END;
IF OBJECT_ID(N'dbo.LockScenario') IS NOT NULL
BEGIN
 IF NOT EXISTS(SELECT 1 FROM dbo.LockScenario WHERE Id=3) INSERT dbo.LockScenario VALUES(3,0);
 IF NOT EXISTS(SELECT 1 FROM dbo.LockScenario WHERE Id=4) INSERT dbo.LockScenario VALUES(4,0);
END;
IF USER_ID(N'WIN-QNGOV5GDM24\SqlObserverCollector') IS NULL CREATE USER [WIN-QNGOV5GDM24\SqlObserverCollector] FOR LOGIN [WIN-QNGOV5GDM24\SqlObserverCollector];
GRANT VIEW DATABASE PERFORMANCE STATE TO [WIN-QNGOV5GDM24\SqlObserverCollector];
GO
CREATE OR ALTER PROCEDURE dbo.SimulateOrderLookup @CustomerId int AS
BEGIN
 SET NOCOUNT ON;
 DECLARE @amount decimal(18,2);
 SELECT @amount=SUM(l.Quantity*l.UnitPrice) FROM dbo.Orders o JOIN dbo.OrderLines l ON l.OrderId=o.OrderId WHERE o.CustomerId=@CustomerId OPTION(MAXDOP 2);
END;
GO
CREATE OR ALTER PROCEDURE dbo.SimulateSalesReport AS
BEGIN
 SET NOCOUNT ON;
 DECLARE @amount decimal(18,2);
 SELECT @amount=SUM(l.Quantity*l.UnitPrice) FROM dbo.Orders o JOIN dbo.OrderLines l ON l.OrderId=o.OrderId JOIN dbo.Customers c ON c.CustomerId=o.CustomerId WHERE c.Region='NE' AND o.Status='Shipped' OPTION(MAXDOP 2);
END;
GO
USE [SqlObserverLabWarehouse];
IF OBJECT_ID(N'dbo.FactInventory') IS NULL
BEGIN
 CREATE TABLE dbo.FactInventory(Id int NOT NULL PRIMARY KEY,ProductId int NOT NULL,WarehouseId int NOT NULL,Quantity int NOT NULL,Description nvarchar(120) NOT NULL);
 WITH d(n) AS (SELECT n FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9))v(n)), nums AS (SELECT TOP(80000) ROW_NUMBER() OVER(ORDER BY (SELECT NULL)) n FROM d a CROSS JOIN d b CROSS JOIN d c CROSS JOIN d e CROSS JOIN d f)
 INSERT dbo.FactInventory SELECT n,n%10000,n%12,n%500,CONCAT(N'Synthetic inventory ',n) FROM nums;
END;
IF USER_ID(N'WIN-QNGOV5GDM24\SqlObserverCollector') IS NULL CREATE USER [WIN-QNGOV5GDM24\SqlObserverCollector] FOR LOGIN [WIN-QNGOV5GDM24\SqlObserverCollector];
GRANT VIEW DATABASE PERFORMANCE STATE TO [WIN-QNGOV5GDM24\SqlObserverCollector];
GO
CREATE OR ALTER PROCEDURE dbo.SimulateInventoryReport AS
BEGIN
 SET NOCOUNT ON;
 DECLARE @value bigint;
 SELECT @value=SUM(CONVERT(bigint,Quantity)*ProductId) FROM dbo.FactInventory WHERE WarehouseId IN(1,3,5,7) OPTION(MAXDOP 2);
 SELECT TOP(20000) Id,Description INTO #SyntheticSort FROM dbo.FactInventory ORDER BY Description DESC OPTION(MAXDOP 2);
 DROP TABLE #SyntheticSort;
END;
GO
USE [master];
SELECT name,state_desc,is_query_store_on FROM sys.databases WHERE name IN(N'SqlObserverLabSales',N'SqlObserverLabWarehouse');
GO
