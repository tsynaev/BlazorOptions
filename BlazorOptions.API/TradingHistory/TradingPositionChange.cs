namespace BlazorOptions.API.TradingHistory;

public readonly struct TradingPositionChange
{
    public TradingPositionChange(
        decimal positionBefore,
        decimal positionAfter,
        decimal signedChange,
        decimal closeQuantity,
        decimal openQuantitySigned)
    {
        PositionBefore = positionBefore;
        PositionAfter = positionAfter;
        SignedChange = signedChange;
        CloseQuantity = closeQuantity;
        OpenQuantitySigned = openQuantitySigned;
    }

    public decimal PositionBefore { get; }

    public decimal PositionAfter { get; }

    public decimal SignedChange { get; }

    public decimal CloseQuantity { get; }

    public decimal OpenQuantitySigned { get; }

    public static TradingPositionChange Resolve(decimal positionBefore, decimal positionAfter)
    {
        var signedChange = positionAfter - positionBefore;
        var closeQuantity = 0m;
        var openQuantitySigned = 0m;

        var beforeSign = Math.Sign(positionBefore);
        var afterSign = Math.Sign(positionAfter);
        var beforeAbs = Math.Abs(positionBefore);
        var afterAbs = Math.Abs(positionAfter);

        if (beforeSign == 0)
        {
            openQuantitySigned = positionAfter;
        }
        else if (afterSign == 0)
        {
            closeQuantity = beforeAbs;
        }
        else if (beforeSign == afterSign)
        {
            if (afterAbs > beforeAbs)
            {
                openQuantitySigned = positionAfter - positionBefore;
            }
            else if (beforeAbs > afterAbs)
            {
                closeQuantity = beforeAbs - afterAbs;
            }
        }
        else
        {
            closeQuantity = beforeAbs;
            openQuantitySigned = positionAfter;
        }

        return new TradingPositionChange(
            positionBefore,
            positionAfter,
            signedChange,
            closeQuantity,
            openQuantitySigned);
    }
}
