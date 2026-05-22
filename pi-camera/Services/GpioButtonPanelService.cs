using System.Device.Gpio;

namespace pi_camera.Services;

public sealed class GpioButtonPanelService<TButton> : IDisposable where TButton : struct, Enum
{
    private sealed class ButtonState
    {
        public TButton Button { get; init; }
        public int Pin { get; init; }
        public bool PreviousPressed { get; set; }
        public DateTime LastTrigger { get; set; } = DateTime.MinValue;
    }

    private readonly List<ButtonState> _buttons;
    private readonly int _debounceMs;
    private GpioController? _gpio;
    private CancellationTokenSource? _cts;

    public event Func<TButton, Task>? ButtonPressed;
    public event Action<string>? StatusChanged;

    public GpioButtonPanelService(IEnumerable<(TButton Button, int Pin)> buttons, int debounceMs = 180)
    {
        _buttons = buttons
            .Where(x => x.Pin >= 0)
            .GroupBy(x => x.Pin)
            .Select(g => g.First())
            .Select(x => new ButtonState { Button = x.Button, Pin = x.Pin })
            .ToList();

        _debounceMs = Math.Clamp(debounceMs, 50, 1000);
    }

    public bool HasButtons => _buttons.Count > 0;

    public void Start()
    {
        if (_buttons.Count == 0)
        {
            StatusChanged?.Invoke("Brak skonfigurowanych pinow przyciskow");
            return;
        }

        try
        {
            _gpio = new GpioController();

            foreach (var button in _buttons)
                _gpio.OpenPin(button.Pin, PinMode.InputPullUp);

            _cts = new CancellationTokenSource();
            _ = Task.Run(() => LoopAsync(_cts.Token));

            StatusChanged?.Invoke("Przyciski gotowe: " + string.Join(", ", _buttons.Select(b => $"{b.Button}=GPIO{b.Pin}")));
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("GPIO przyciskow niedostepne: " + ex.Message);
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            foreach (var button in _buttons)
            {
                var pressed = false;

                try
                {
                    pressed = _gpio?.Read(button.Pin) == PinValue.Low;
                }
                catch
                {
                    pressed = false;
                }

                if (pressed && !button.PreviousPressed)
                {
                    var now = DateTime.UtcNow;
                    if ((now - button.LastTrigger).TotalMilliseconds >= _debounceMs)
                    {
                        button.LastTrigger = now;

                        if (ButtonPressed is not null)
                        {
                            try { await ButtonPressed.Invoke(button.Button); }
                            catch (Exception ex) { StatusChanged?.Invoke(ex.Message); }
                        }
                    }
                }

                button.PreviousPressed = pressed;
            }

            await Task.Delay(20, token);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();

        if (_gpio is not null)
        {
            foreach (var button in _buttons)
            {
                try
                {
                    if (_gpio.IsPinOpen(button.Pin))
                        _gpio.ClosePin(button.Pin);
                }
                catch { }
            }
        }

        _gpio?.Dispose();
    }
}
