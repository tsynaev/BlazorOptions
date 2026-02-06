using System.Collections.ObjectModel;
using BlazorChart.Models;
using BlazorOptions.API.Positions;
using BlazorOptions.ViewModels;

namespace BlazorOptions.Services;

public static class PositionDtoMapper
{
    public static PositionDto ToDto(PositionModel model)
    {
        var dto = new PositionDto
        {
            Id = model.Id,
            BaseAsset = model.BaseAsset ?? string.Empty,
            QuoteAsset = model.QuoteAsset ?? string.Empty,
            Name = model.Name ?? string.Empty,
            Notes = model.Notes ?? string.Empty,
            Collections = model.Collections.Select(ToDto).ToList(),
            Closed = ToDto(model.Closed),
            ChartXMin = model.ChartRange?.XMin,
            ChartXMax = model.ChartRange?.XMax,
            ChartYMin = model.ChartRange?.YMin,
            ChartYMax = model.ChartRange?.YMax
        };

        return dto;
    }

    public static PositionModel ToModel(PositionDto dto)
    {
        var model = new PositionModel
        {
            Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id,
            BaseAsset = dto.BaseAsset ?? string.Empty,
            QuoteAsset = dto.QuoteAsset ?? string.Empty,
            Name = dto.Name ?? string.Empty,
            Notes = dto.Notes ?? string.Empty,
            Collections = new ObservableCollection<LegsCollectionModel>(),
            Closed = ToModel(dto.Closed),
            ChartRange = dto.ChartXMin.HasValue && dto.ChartXMax.HasValue && dto.ChartYMin.HasValue && dto.ChartYMax.HasValue
                ? new ChartRange(dto.ChartXMin.Value, dto.ChartXMax.Value, dto.ChartYMin.Value, dto.ChartYMax.Value)
                : null
        };

        if (dto.Collections is not null)
        {
            foreach (var collection in dto.Collections)
            {
                model.Collections.Add(ToModel(collection));
            }
        }

        return model;
    }

    private static LegsCollectionDto ToDto(LegsCollectionModel model)
    {
        return new LegsCollectionDto
        {
            Id = model.Id,
            Name = model.Name ?? string.Empty,
            Color = model.Color ?? string.Empty,
            IsVisible = model.IsVisible,
            Legs = model.Legs.Select(ToDto).ToList()
        };
    }

    private static LegsCollectionModel ToModel(LegsCollectionDto dto)
    {
        var model = new LegsCollectionModel
        {
            Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id,
            Name = dto.Name ?? string.Empty,
            Color = dto.Color ?? string.Empty,
            IsVisible = dto.IsVisible,
            Legs = new ObservableCollection<LegModel>()
        };

        if (dto.Legs is not null)
        {
            foreach (var leg in dto.Legs)
            {
                model.Legs.Add(ToModel(leg));
            }
        }

        return model;
    }

    private static LegDto ToDto(LegModel model)
    {
        return new LegDto
        {
            Id = model.Id ?? string.Empty,
            IsIncluded = model.IsIncluded,
            IsReadOnly = model.IsReadOnly,
            Type = (BlazorOptions.API.Positions.LegType)model.Type,
            Strike = model.Strike,
            ExpirationDate = model.ExpirationDate,
            Size = model.Size,
            Price = model.Price,
            ImpliedVolatility = model.ImpliedVolatility,
            Symbol = model.Symbol
        };
    }

    private static LegModel ToModel(LegDto dto)
    {
        return new LegModel
        {
            Id = string.IsNullOrWhiteSpace(dto.Id) ? Guid.NewGuid().ToString("N") : dto.Id,
            IsIncluded = dto.IsIncluded,
            IsReadOnly = dto.IsReadOnly,
            Type = (ViewModels.LegType)dto.Type,
            Strike = dto.Strike,
            ExpirationDate = dto.ExpirationDate,
            Size = dto.Size,
            Price = dto.Price,
            ImpliedVolatility = dto.ImpliedVolatility,
            Symbol = dto.Symbol
        };
    }

    private static ClosedPositionsDto ToDto(ClosedModel? model)
    {
        var dto = new ClosedPositionsDto
        {
            Include = model?.Include ?? false,
            TotalClosePnl = model?.TotalClosePnl ?? 0m,
            TotalFee = model?.TotalFee ?? 0m,
            Positions = new List<ClosedPositionDto>()
        };

        if (model?.Positions is not null)
        {
            foreach (var position in model.Positions)
            {
                dto.Positions.Add(ToDto(position));
            }
        }

        return dto;
    }

    private static ClosedModel ToModel(ClosedPositionsDto? dto)
    {
        var model = new ClosedModel
        {
            Include = dto?.Include ?? false,
            TotalClosePnl = dto?.TotalClosePnl ?? 0m,
            TotalFee = dto?.TotalFee ?? 0m
        };

        if (dto?.Positions is not null)
        {
            foreach (var position in dto.Positions)
            {
                model.Positions.Add(ToModel(position));
            }
        }

        return model;
    }

    private static ClosedPositionDto ToDto(ClosedPositionModel model)
    {
        return new ClosedPositionDto
        {
            Id = model.Id,
            Symbol = model.Symbol ?? string.Empty,
            SinceDate = model.SinceDate,
            FirstTradeTimestamp = model.FirstTradeTimestamp,
            LastProcessedTimestamp = model.LastProcessedTimestamp,
            LastProcessedIdsAtTimestamp = model.LastProcessedIdsAtTimestamp?.ToList() ?? new List<string>(),
            PositionSize = model.PositionSize,
            AvgPrice = model.AvgPrice,
            EntryQty = model.EntryQty,
            EntryValue = model.EntryValue,
            CloseQty = model.CloseQty,
            CloseValue = model.CloseValue,
            Realized = model.Realized,
            FeeTotal = model.FeeTotal
        };
    }

    private static ClosedPositionModel ToModel(ClosedPositionDto dto)
    {
        return new ClosedPositionModel
        {
            Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id,
            Symbol = dto.Symbol ?? string.Empty,
            SinceDate = dto.SinceDate,
            FirstTradeTimestamp = dto.FirstTradeTimestamp,
            LastProcessedTimestamp = dto.LastProcessedTimestamp,
            LastProcessedIdsAtTimestamp = dto.LastProcessedIdsAtTimestamp?.ToList() ?? new List<string>(),
            PositionSize = dto.PositionSize,
            AvgPrice = dto.AvgPrice,
            EntryQty = dto.EntryQty,
            EntryValue = dto.EntryValue,
            CloseQty = dto.CloseQty,
            CloseValue = dto.CloseValue,
            Realized = dto.Realized,
            FeeTotal = dto.FeeTotal
        };
    }
}
