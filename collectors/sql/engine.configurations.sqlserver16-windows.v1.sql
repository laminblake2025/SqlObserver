SET NOCOUNT ON;

DECLARE @observed_at_utc datetime2(7) = SYSUTCDATETIME();

SELECT TOP (@maximum_rows)
    @observed_at_utc AS observed_at_utc,
    configuration.configuration_id,
    configuration.name AS configuration_name,
    CONVERT(nvarchar(32), SQL_VARIANT_PROPERTY(configuration.value, 'BaseType')) AS configured_source_type,
    CONVERT(nvarchar(32), SQL_VARIANT_PROPERTY(configuration.value_in_use, 'BaseType')) AS in_use_source_type,
    CONVERT(nvarchar(4000), configuration.value) AS configured_value,
    CONVERT(nvarchar(4000), configuration.value_in_use) AS in_use_value,
    configuration.is_dynamic,
    configuration.is_advanced
FROM sys.configurations AS configuration
ORDER BY configuration.configuration_id;
