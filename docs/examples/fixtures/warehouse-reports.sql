-- =============================================================
-- Warehouse reporting procedures
--
-- Synthetic fixture, companion to warehouse-procs.sql. Nothing here comes
-- from a real system. This is the all-procedures case: every object shares
-- the spWHREPORTS_ prefix, so the index strips it and shows it once in its
-- own header instead of wrapping it mid-identifier in a 250px panel.
-- =============================================================

CREATE PROCEDURE [dbo].[spWHREPORTS_GET_DAILY_THROUGHPUT]
     @on_date       DATE
    ,@location_code VARCHAR(10) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
         m.location_code
        ,SUM(CASE WHEN m.quantity > 0 THEN m.quantity ELSE 0 END) AS received
        ,SUM(CASE WHEN m.quantity < 0 THEN -m.quantity ELSE 0 END) AS picked
        ,COUNT(*) AS movement_count
    FROM dbo.tWAREHOUSE_STOCK_MOVEMENTS AS m
    WHERE CAST(m.moved_utc AS DATE) = @on_date
      AND (@location_code IS NULL OR m.location_code = @location_code)
    GROUP BY m.location_code
    ORDER BY m.location_code;
END
GO

CREATE PROCEDURE [dbo].[spWHREPORTS_GET_REORDER_CANDIDATES]
     @include_inactive BIT = 0
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
         i.sku
        ,i.description
        ,i.reorder_level
        ,ISNULL(s.quantity_on_hand, 0) AS quantity_on_hand
    FROM dbo.tWAREHOUSE_ITEMS AS i
    LEFT JOIN dbo.vWAREHOUSE_STOCK_ON_HAND AS s
      ON s.item_id = i.item_id
    WHERE (@include_inactive = 1 OR i.is_active = 1)
      AND ISNULL(s.quantity_on_hand, 0) < i.reorder_level
    ORDER BY (i.reorder_level - ISNULL(s.quantity_on_hand, 0)) DESC;
END
GO

CREATE PROCEDURE [dbo].[spWHREPORTS_GET_SLOW_MOVING_STOCK]
     @idle_days INT = 90
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @cutoff DATETIME2 = DATEADD(DAY, -@idle_days, SYSUTCDATETIME());

    SELECT
         i.sku
        ,i.description
        ,MAX(m.moved_utc) AS last_moved_utc
    FROM dbo.tWAREHOUSE_ITEMS AS i
    JOIN dbo.tWAREHOUSE_STOCK_MOVEMENTS AS m
      ON m.item_id = i.item_id
    GROUP BY i.sku, i.description
    HAVING MAX(m.moved_utc) < @cutoff
    ORDER BY last_moved_utc;
END
GO

-- =============================================================
-- The month-end pack. Numbered because it is read when the numbers
-- disagree with finance, which is the only time anyone opens it.
-- =============================================================
CREATE PROCEDURE [dbo].[spWHREPORTS_BUILD_MONTH_END_PACK]
     @year      INT
    ,@month     INT
    ,@row_count INT = 0 OUT
AS
BEGIN
    SET NOCOUNT ON;

    -- 1. Resolve the period
    DECLARE @from DATETIME2 = DATETIMEFROMPARTS(@year, @month, 1, 0, 0, 0, 0);
    DECLARE @to   DATETIME2 = DATEADD(MONTH, 1, @from);

    -- 2. Movement totals for the period
    SELECT
         i.sku
        ,SUM(m.quantity)   AS net_movement
        ,COUNT(*)          AS movement_count
    INTO #period_totals
    FROM dbo.tWAREHOUSE_STOCK_MOVEMENTS AS m
    JOIN dbo.tWAREHOUSE_ITEMS AS i
      ON i.item_id = m.item_id
    WHERE m.moved_utc >= @from
      AND m.moved_utc <  @to
    GROUP BY i.sku;

    -- 3. Valuation at current unit cost
    SELECT
         t.sku
        ,t.net_movement
        ,t.movement_count
        ,t.net_movement * i.unit_cost AS net_value
    FROM #period_totals AS t
    JOIN dbo.tWAREHOUSE_ITEMS AS i
      ON i.sku = t.sku
    ORDER BY ABS(t.net_movement * i.unit_cost) DESC;

    SET @row_count = @@ROWCOUNT;

    -- 4. Clean up
    DROP TABLE #period_totals;
END
GO

CREATE PROCEDURE [dbo].[spWHREPORTS_GET_LOCATION_UTILISATION]
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
         location_code
        ,COUNT(DISTINCT item_id) AS distinct_items
        ,SUM(quantity_on_hand)   AS total_units
    FROM dbo.vWAREHOUSE_STOCK_ON_HAND
    GROUP BY location_code
    ORDER BY total_units DESC;
END
GO
