IF CONVERT(nvarchar(128),SERVERPROPERTY('MachineName')) <> N'WIN-QNGOV5GDM24' THROW 51000,'Synthetic workload host mismatch.',1;
GO
SET NOCOUNT ON;
DECLARE @stop datetime2=DATEADD(second,240,SYSUTCDATETIME()), @iteration int=0, @customer int;
WHILE SYSUTCDATETIME()<@stop AND @iteration<240
BEGIN
 SET @customer=1+(@iteration*37)%2000;
 EXEC SqlObserverLabSales.dbo.SimulateOrderLookup @CustomerId=@customer;
 EXEC SqlObserverLabSales.dbo.SimulateSalesReport;
 EXEC SqlObserverLabWarehouse.dbo.SimulateInventoryReport;
 SET @iteration+=1;
 WAITFOR DELAY '00:00:01';
END;
EXEC SqlObserverLabSales.sys.sp_query_store_flush_db;
SELECT 'traffic_completed' AS outcome,@iteration AS iterations,SYSUTCDATETIME() AS completed_utc;
