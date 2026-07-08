using Microsoft.JSInterop;

namespace Velodromify.Services;

public class BluetoothInterop : IAsyncDisposable
{
    private readonly IJSRuntime _jsRuntime;
    private readonly DotNetObjectReference<BluetoothInterop> _objRef;
    
    public event Action<double, double, double>? OnDataReceived;

    public BluetoothInterop(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
        _objRef = DotNetObjectReference.Create(this);
    }

    public async Task InitializeAsync()
    {
        await _jsRuntime.InvokeVoidAsync("VelodromifyBLE.init", _objRef);
    }

    public async Task<bool> ConnectAsync()
    {
        return await _jsRuntime.InvokeAsync<bool>("VelodromifyBLE.connect");
    }

    [JSInvokable]
    public void UpdateRideData(double speed, double power, double cadence)
    {
        OnDataReceived?.Invoke(speed, power, cadence);
    }

    public async ValueTask DisposeAsync()
    {
        _objRef.Dispose();
    }
}
