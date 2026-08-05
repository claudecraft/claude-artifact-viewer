-- =============================================================
-- Warehouse inventory — schema and stored procedures
--
-- Synthetic fixture for the README's SQL view screenshot. Nothing here
-- comes from a real system: invented schema, invented data, no author.
-- Exercises the parts of the view that are worth showing —
--   * a mix of object kinds: TABLE, VIEW, PROCEDURE, TRIGGER
--   * numbered steps nested under their procedure
--   * a commented-out GO and a CREATE inside dynamic SQL, neither of
--     which may split a batch
--
-- It does NOT exercise prefix stripping, and can't: CommonNamePrefix takes the
-- prefix shared by every named object, so the t/v/sp/tr kind letters cut it to
-- nothing. That path needs an all-procedures script — warehouse-reports.sql.
-- =============================================================

CREATE TABLE dbo.tWAREHOUSE_ITEMS
(
     item_id        INT IDENTITY(1,1) PRIMARY KEY
    ,sku            VARCHAR(32)  NOT NULL UNIQUE
    ,description    VARCHAR(200) NOT NULL
    ,unit_cost      MONEY        NOT NULL DEFAULT 0
    ,reorder_level  INT          NOT NULL DEFAULT 0
    ,is_active      BIT          NOT NULL DEFAULT 1
    ,created_utc    DATETIME2    NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

CREATE TABLE dbo.tWAREHOUSE_STOCK_MOVEMENTS
(
     movement_id    INT IDENTITY(1,1) PRIMARY KEY
    ,item_id        INT          NOT NULL REFERENCES dbo.tWAREHOUSE_ITEMS(item_id)
    ,location_code  VARCHAR(10)  NOT NULL
    ,quantity       INT          NOT NULL      -- negative for a pick, positive for a receipt
    ,reason_code    VARCHAR(20)  NULL
    ,moved_utc      DATETIME2    NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

CREATE VIEW dbo.vWAREHOUSE_STOCK_ON_HAND
AS
    SELECT
         i.item_id
        ,i.sku
        ,i.description
        ,m.location_code
        ,SUM(m.quantity) AS quantity_on_hand
    FROM dbo.tWAREHOUSE_ITEMS AS i
    JOIN dbo.tWAREHOUSE_STOCK_MOVEMENTS AS m
      ON m.item_id = i.item_id
    WHERE i.is_active = 1
    GROUP BY i.item_id, i.sku, i.description, m.location_code;
GO

-- =============================================================
-- Record a stock movement. The workhorse: everything else reads.
-- =============================================================
CREATE PROCEDURE [dbo].[spWAREHOUSE_RECORD_MOVEMENT]
     @item_id        INT
    ,@location_code  VARCHAR(10)
    ,@quantity       INT
    ,@reason_code    VARCHAR(20)  = NULL
    ,@allow_negative BIT          = 0
    ,@movement_id    INT          = 0 OUT
    ,@error_message  VARCHAR(300)     OUT
AS
BEGIN
    SET NOCOUNT ON;

    -- 1. Validate the inputs
    SET @error_message = NULL;

    IF @quantity = 0
    BEGIN
        SET @error_message = 'Quantity must be non-zero.';
        RETURN 1;
    END

    IF NOT EXISTS (SELECT 1 FROM dbo.tWAREHOUSE_ITEMS WHERE item_id = @item_id AND is_active = 1)
    BEGIN
        SET @error_message = 'Unknown or inactive item.';
        RETURN 1;
    END

    -- 2. Check the resulting balance
    DECLARE @on_hand INT;

    SELECT @on_hand = ISNULL(SUM(quantity), 0)
    FROM dbo.tWAREHOUSE_STOCK_MOVEMENTS
    WHERE item_id = @item_id
      AND location_code = @location_code;

    IF @allow_negative = 0 AND (@on_hand + @quantity) < 0
    BEGIN
        SET @error_message = 'Movement would drive stock negative.';
        RETURN 2;
    END

    -- 3. Write the movement
    BEGIN TRY
        INSERT INTO dbo.tWAREHOUSE_STOCK_MOVEMENTS (item_id, location_code, quantity, reason_code)
        VALUES (@item_id, @location_code, @quantity, @reason_code);

        SET @movement_id = SCOPE_IDENTITY();
    END TRY
    BEGIN CATCH
        SET @error_message = ERROR_MESSAGE();
        RETURN 3;
    END CATCH

    -- 4. Flag anything that has fallen below its reorder level
    IF EXISTS (SELECT 1
               FROM dbo.tWAREHOUSE_ITEMS AS i
               WHERE i.item_id = @item_id
                 AND (@on_hand + @quantity) < i.reorder_level)
    BEGIN
        PRINT 'Item is below its reorder level.';
    END

    RETURN 0;
END
GO

-- =============================================================
-- Stock for one location, or all of them when @location_code is NULL.
-- =============================================================
CREATE PROCEDURE [dbo].[spWAREHOUSE_GET_STOCK_ON_HAND]
     @location_code  VARCHAR(10) = NULL
    ,@include_zero   BIT         = 0
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
         sku
        ,description
        ,location_code
        ,quantity_on_hand
    FROM dbo.vWAREHOUSE_STOCK_ON_HAND
    WHERE (@location_code IS NULL OR location_code = @location_code)
      AND (@include_zero = 1 OR quantity_on_hand <> 0)
    ORDER BY sku, location_code;
END
GO

-- =============================================================
-- Movement history for one item, newest first.
-- =============================================================
CREATE PROCEDURE [dbo].[spWAREHOUSE_GET_MOVEMENT_HISTORY]
     @item_id   INT
    ,@from_utc  DATETIME2 = NULL
    ,@to_utc    DATETIME2 = NULL
    ,@max_rows  INT       = 500
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@max_rows)
         m.movement_id
        ,m.location_code
        ,m.quantity
        ,m.reason_code
        ,m.moved_utc
    FROM dbo.tWAREHOUSE_STOCK_MOVEMENTS AS m
    WHERE m.item_id = @item_id
      AND (@from_utc IS NULL OR m.moved_utc >= @from_utc)
      AND (@to_utc   IS NULL OR m.moved_utc <  @to_utc)
    ORDER BY m.moved_utc DESC;
END
GO

-- =============================================================
-- Nightly reconciliation. Steps are numbered because this is the one
-- that gets read at 2am when the totals disagree.
-- =============================================================
CREATE PROCEDURE [dbo].[spWAREHOUSE_RECONCILE_NIGHTLY]
     @as_of_utc     DATETIME2 = NULL
    ,@dry_run       BIT       = 1
    ,@rows_adjusted INT       = 0 OUT
AS
BEGIN
    SET NOCOUNT ON;
    SET @as_of_utc = ISNULL(@as_of_utc, SYSUTCDATETIME());
    SET @rows_adjusted = 0;

    -- 1. Snapshot the current balances
    DECLARE @balances TABLE
    (
         item_id       INT
        ,location_code VARCHAR(10)
        ,quantity      INT
    );

    INSERT INTO @balances (item_id, location_code, quantity)
    SELECT item_id, location_code, SUM(quantity)
    FROM dbo.tWAREHOUSE_STOCK_MOVEMENTS
    WHERE moved_utc < @as_of_utc
    GROUP BY item_id, location_code;

    -- 2. Find the negatives, which should not exist
    SELECT
         b.item_id
        ,b.location_code
        ,b.quantity
    FROM @balances AS b
    WHERE b.quantity < 0;

    -- 3. Build the correction statement
    --    Dynamic SQL on purpose: the CREATE TABLE inside this string must not
    --    be read as a definition, and the GO inside it must not split a batch.
    DECLARE @sql NVARCHAR(MAX) = N'
        CREATE TABLE #corrections (item_id INT, location_code VARCHAR(10), delta INT);
        GO
        INSERT INTO #corrections SELECT item_id, location_code, -quantity FROM @src WHERE quantity < 0;
    ';

    -- 4. Apply, unless this is a dry run
    IF @dry_run = 0
    BEGIN
        INSERT INTO dbo.tWAREHOUSE_STOCK_MOVEMENTS (item_id, location_code, quantity, reason_code)
        SELECT item_id, location_code, -quantity, 'RECONCILE'
        FROM @balances
        WHERE quantity < 0;

        SET @rows_adjusted = @@ROWCOUNT;
    END

    -- 5. Report
    SELECT @rows_adjusted AS rows_adjusted, @as_of_utc AS as_of_utc, @dry_run AS was_dry_run;
END
-- GO      <- commented out deliberately; this must not split the batch either
GO

CREATE TRIGGER dbo.trWAREHOUSE_ITEMS_AUDIT
ON dbo.tWAREHOUSE_ITEMS
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF UPDATE(is_active)
        PRINT 'Item activation changed.';
END
GO
