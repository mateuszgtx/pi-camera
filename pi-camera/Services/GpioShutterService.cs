using System.Device.Gpio;

namespace pi_camera.Services;

public sealed class GpioShutterService : IDisposable
{
    private readonly int _pin;
    private GpioController? _gpio;
    private CancellationTokenSource? _cts;
    private bool _previousPressed;
    private DateTime _lastTrigger = DateTime.MinValue;

    public event Func<Task>? ShutterPressed;
    public event Action<string>? StatusChanged;

    public GpioShutterService(int pin)
    {
        _pin = pin;
    }

    public void Start()
    {
        try
        {
            _gpio = new GpioController();
            _gpio.OpenPin(_pin, PinMode.InputPullUp);

            _cts = new CancellationTokenSource();
            _ = Task.Run(() => LoopAsync(_cts.Token));

            StatusChanged?.Invoke($"GPIO{_pin} gotowy");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"GPIO niedostepne: {ex.Message}");
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var pressed = false;

            try
            {
                pressed = _gpio?.Read(_pin) == PinValue.Low;
            }
            catch { }

            if (pressed && !_previousPressed)
            {
                var now = DateTime.Now;

                if ((now - _lastTrigger).TotalMilliseconds > 600)
                {
                    _lastTrigger = now;

                    if (ShutterPressed is not null)
                    {
                        try { await ShutterPressed.Invoke(); }
                        catch (Exception ex) { StatusChanged?.Invoke(ex.Message); }
                    }
                }
            }

            _previousPressed = pressed;
            await Task.Delay(25, token);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();

        try
        {
            if (_gpio is not null && _gpio.IsPinOpen(_pin))
                _gpio.ClosePin(_pin);
        }
        catch { }

        _gpio?.Dispose();
    }
}
