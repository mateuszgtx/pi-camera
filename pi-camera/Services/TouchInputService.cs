using System.Runtime.InteropServices;

namespace pi_camera.Services;

public sealed class TouchInputService : IDisposable
{
    private const ushort EV_KEY = 0x01;
    private const ushort EV_ABS = 0x03;

    private const ushort BTN_TOUCH = 0x14a;
    private const ushort ABS_X = 0x00;
    private const ushort ABS_Y = 0x01;

    private readonly string _path;
    private readonly int _screenW;
    private readonly int _screenH;
    private readonly bool _invertX;
    private readonly bool _invertY;

    private CancellationTokenSource? _cts;
    private FileStream? _stream;
    private int _rawX;
    private int _rawY;

    public event Action<int, int>? Touched;
    public event Action<string>? StatusChanged;

    public TouchInputService(string path, int screenW, int screenH, bool invertX, bool invertY)
    {
        _path = path;
        _screenW = screenW;
        _screenH = screenH;
        _invertX = invertX;
        _invertY = invertY;
    }

    public void Start()
    {
        try
        {
            _stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => LoopAsync(_cts.Token));
            StatusChanged?.Invoke($"Touch: {_path}");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Touch niedostepny: {ex.Message}");
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        var size = Marshal.SizeOf<InputEvent>();
        var buffer = new byte[size];

        while (!token.IsCancellationRequested && _stream is not null)
        {
            try
            {
                var read = await _stream.ReadAsync(buffer.AsMemory(0, size), token);
                if (read != size)
                    continue;

                var ev = MemoryMarshal.Read<InputEvent>(buffer);

                if (ev.Type == EV_ABS)
                {
                    if (ev.Code == ABS_X) _rawX = ev.Value;
                    if (ev.Code == ABS_Y) _rawY = ev.Value;
                }
                else if (ev.Type == EV_KEY && ev.Code == BTN_TOUCH)
                {
                    if (ev.Value == 0)
                    {
                        var x = Scale(_rawX, 0, 4095, 0, _screenW - 1);
                        var y = Scale(_rawY, 0, 4095, 0, _screenH - 1);

                        if (_invertX)
                            x = _screenW - 1 - x;

                        if (_invertY)
                            y = _screenH - 1 - y;

                        Touched?.Invoke(x, y);
                    }
                }
            }
            catch
            {
                break;
            }
        }
    }

    private static int Scale(int value, int inMin, int inMax, int outMin, int outMax)
    {
        value = Math.Clamp(value, inMin, inMax);
        return (value - inMin) * (outMax - outMin) / (inMax - inMin) + outMin;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _stream?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeVal
    {
        public long tv_sec;
        public long tv_usec;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InputEvent
    {
        public TimeVal Time;
        public ushort Type;
        public ushort Code;
        public int Value;
    }
}
