CREATE FUNCTION [Admin].[ReplaceUnicodeChars]
(
    @CurrentValue nvarchar(200)
)
RETURNS nvarchar(200)
AS

BEGIN

    Declare @NewValue as varchar(200)
    Set @NewValue = @CurrentValue

    Declare @unicodeCodePattern as varchar(50)
    Set @unicodeCodePattern = '%&#[0-9]%;%'

    while PatIndex(@unicodeCodePattern, @NewValue) > 0 
    BEGIN
           Declare @nameEnd as varchar(50)
           set @nameEnd = RIGHT(@NewValue, len(@NewValue) - PatIndex(@unicodeCodePattern, @NewValue)+1)

           Declare @unicode as varchar(50)
           set @unicode = SUBSTRING(@nameEnd, PatIndex(@unicodeCodePattern, @nameEnd), PatIndex('%;%', @nameEnd))

           Declare @startIndex int
           set @startIndex = 3

           Declare @asciiCode as varchar(50)
           set @asciiCode = SUBSTRING(@nameEnd,@startIndex, PatIndex('%;%', @nameEnd)-@startIndex)

           Set @NewValue = REPLACE(@NewValue, @unicode, char(cast(@asciiCode as int)))

    END

    RETURN @NewValue

END

GO

