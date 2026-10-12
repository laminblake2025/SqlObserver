SET NOCOUNT ON;
/* M7 bounded Query Store text lookup for SQL Server 17; never returns restricted or encrypted-module text. */
SELECT TOP (32) query_text_id, query_sql_text
FROM sys.query_store_query_text
WHERE query_text_id IN (@text_id_0, @text_id_1, @text_id_2, @text_id_3, @text_id_4, @text_id_5, @text_id_6, @text_id_7, @text_id_8, @text_id_9, @text_id_10, @text_id_11, @text_id_12, @text_id_13, @text_id_14, @text_id_15, @text_id_16, @text_id_17, @text_id_18, @text_id_19, @text_id_20, @text_id_21, @text_id_22, @text_id_23, @text_id_24, @text_id_25, @text_id_26, @text_id_27, @text_id_28, @text_id_29, @text_id_30, @text_id_31)
  AND is_part_of_encrypted_module = 0
  AND has_restricted_text = 0
  AND DATALENGTH(query_sql_text) BETWEEN 2 AND 8192
ORDER BY query_text_id;
