using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class TradesViewModelFactory
{
    public TradesViewModel Create(
        ClosedPositionsViewModel closedPositionsViewModel,
        ITransactionHistoryService transactionHistory)
    {
        return new TradesViewModel(closedPositionsViewModel, transactionHistory);
    }
}
