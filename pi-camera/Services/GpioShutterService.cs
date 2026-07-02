using System.Device.Gpio;

namespace pi_camera.Services;

public sealed class GpioShutterService : IDisposable
{
    private readonly int _pin;
    private readonly int _stablePressMs;
    private readonly int _cooldownMs;
    private readonly int _pollMs;
    private GpioController? _gpio;
    private CancellationTokenSource? _cts;
    private bool _armed = true;
    private DateTime _pressedSinceUtc = DateTime.MinValue;
    private DateTime _lastTriggerUtc = DateTime.MinValue;
    private volatile bool _isPressed;

    public bool IsPressed => _isPressed;

    public event Func<Task>? ShutterPressed;
    public event Action<string>? StatusChanged;

    public GpioShutterService(int pin, int stablePressMs = 80, int cooldownMs = 900, int pollMs = 10)
    {
        _pin = pin;
        _stablePressMs = Math.Clamp(stablePressMs, 5, 500);
        _cooldownMs = Math.Clamp(cooldownMs, 50, 3000);
        _pollMs = Math.Clamp(pollMs, 2, 100);
    }

    public void Start()
    {
        try
        {
            _gpio = new GpioController();
            _gpio.OpenPin(_pin, PinMode.InputPullUp);

            // Nie wyzwalaj zdjęcia przy starcie, nawet jeżeli pin jest chwilowo LOW.
            _armed = _gpio.Read(_pin) == PinValue.High;

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

            _isPressed = pressed;

            var now = DateTime.UtcNow;

            if (!pressed)
            {
                _pressedSinceUtc = DateTime.MinValue;
                _armed = true;
                await Task.Delay(_pollMs, token);
                continue;
            }

            if (_pressedSinceUtc == DateTime.MinValue)
                _pressedSinceUtc = now;

            var stablePress = (now - _pressedSinceUtc).TotalMilliseconds >= _stablePressMs;
            var cooledDown = (now - _lastTriggerUtc).TotalMilliseconds >= _cooldownMs;

            if (_armed && stablePress && cooledDown)
            {
                _armed = false; // czekamy na puszczenie przycisku
                _lastTriggerUtc = now;

                if (ShutterPressed is not null)
                {
                    try { await ShutterPressed.Invoke(); }
                    catch (Exception ex) { StatusChanged?.Invoke(ex.Message); }
                }
            }

            await Task.Delay(_pollMs, token);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _isPressed = false;

        try
        {
            if (_gpio is not null && _gpio.IsPinOpen(_pin))
                _gpio.ClosePin(_pin);
        }
        catch { }

        _gpio?.Dispose();
    }
}
