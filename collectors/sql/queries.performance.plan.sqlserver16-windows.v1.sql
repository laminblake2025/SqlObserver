SET NOCOUNT ON;
/* M7 bounded Query Store plan XML lookup for SQL Server 16. Treat XML as sensitive, inert data. */
SELECT TOP (4) p.plan_id, p.query_plan
FROM sys.query_store_plan AS p
JOIN sys.query_store_query AS q ON q.query_id = p.query_id
JOIN sys.query_store_query_text AS qt ON qt.query_text_id = q.query_text_id
WHERE p.plan_id IN (@plan_id_0, @plan_id_1, @plan_id_2, @plan_id_3)
  AND qt.has_restricted_text = 0
  AND qt.is_part_of_encrypted_module = 0
  AND DATALENGTH(p.query_plan) BETWEEN 2 AND 524288
ORDER BY p.plan_id;
