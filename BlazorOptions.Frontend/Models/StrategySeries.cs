using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BlazorChart.Models;

public sealed class StrategySeries : INotifyPropertyChanged
{
    private string _id;
    private string _name;
    private string _color;
    private bool _showBreakEvens;
    private IReadOnlyList<PayoffPoint> _tempPnl;
    private IReadOnlyList<PayoffPoint> _expiredPnl;
    private bool _visible;

    public StrategySeries(
        string id,
        string name,
        string color,
        bool showBreakEvens,
        IReadOnlyList<PayoffPoint> tempPnl,
        IReadOnlyList<PayoffPoint> expiredPnl,
        bool visible = true)
    {
        _id = id;
        _name = name;
        _color = color;
        _showBreakEvens = showBreakEvens;
        _tempPnl = tempPnl;
        _expiredPnl = expiredPnl;
        _visible = visible;
    }

    public string Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string Color
    {
        get => _color;
        set => SetField(ref _color, value);
    }

    public bool ShowBreakEvens
    {
        get => _showBreakEvens;
        set => SetField(ref _showBreakEvens, value);
    }

    public IReadOnlyList<PayoffPoint> TempPnl
    {
        get => _tempPnl;
        set => SetField(ref _tempPnl, value);
    }

    public IReadOnlyList<PayoffPoint> ExpiredPnl
    {
        get => _expiredPnl;
        set => SetField(ref _expiredPnl, value);
    }

    public bool Visible
    {
        get => _visible;
        set => SetField(ref _visible, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
